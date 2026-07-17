using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Trust;
using Aria.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Infrastructure;

/// <summary>
/// Outbound SignalR connection from Aria.Bridge → Aria.Web server.
/// Eliminates the browser-tab dependency for LLM calls and key-custody routing.
/// Authenticates with the soul's ECDSA private key (same challenge-response as the WASM path).
/// The server prefers this direct connection over the WASM relay when routing requests.
/// </summary>
public sealed class DirectTunnel : IHostedService
{
    private readonly IServiceScopeFactory    _scopes;
    private readonly Action<string, string>  _log;
    private readonly HttpClient              _http   = new() { Timeout = TimeSpan.FromMinutes(10) };
    private CancellationTokenSource?         _cts;
    private CancellationTokenSource?         _delayCts;
    private HubConnection?                   _hub;
    // requestId → CTS for in-flight LLM requests. The server sends AbortRequest to cancel upstream generation.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requestCts = new();
    // Stable pairing code for an unenrolled joined node, shown to the human to approve this device.
    // Generated once per process so it stays consistent across reconnect attempts. Exposed statically
    // (one tunnel per process) so the bridge status page can display it; cleared once enrolled.
    private string?                          _joinCode;
    public static string? CurrentJoinCode { get; private set; }

    private readonly PtySessionStore _ptySessions;
    private readonly SiblingRoster   _siblingRoster;

    public DirectTunnel(IServiceScopeFactory scopes, Action<string, string> log, PtySessionStore ptySessions, SiblingRoster siblingRoster)
    {
        _scopes         = scopes;
        _log            = log;
        _ptySessions    = ptySessions;
        _siblingRoster  = siblingRoster;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        _delayCts?.Cancel();
        await _ptySessions.KillAllAsync();
        if (_hub != null)
            await _hub.DisposeAsync();
    }

    /// <summary>
    /// Drops the current tunnel connection so the reconnect loop picks up a new ServerUrl/ServerSoulId
    /// from the database. Called after /soul/link-server or /soul/unlink.
    /// </summary>
    public void RequestReconnect()
    {
        var hub = _hub;
        if (hub is not null)
        {
            _ = hub.DisposeAsync();
        }
        try
        {
            _delayCts?.Cancel();
        }
        catch { }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunAsync(ct);
                delay = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException) { if (ct.IsCancellationRequested) break; }
            catch (Exception ex)
            {
                _log("WARN", $"[Tunnel] {ex.Message} — reconnecting in {delay.TotalSeconds:0}s");
                try
                {
                    _delayCts?.Dispose();
                    _delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    await Task.Delay(delay, _delayCts.Token);
                }
                catch (OperationCanceledException) { if (ct.IsCancellationRequested) break; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
        _log("INFO", "[Tunnel] Stopped.");
    }

    private async Task ConnectAndRunAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        // Connect if we hold either the soul master key (primary bridge) or an enrolled node key
        // (additional bridge that joined via /soul/join).
        var soul = await db.Souls.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ServerSoulId != null && s.ServerUrl != null
                && (s.PrivateKeyBase64 != null || s.NodePrivateKeyBase64 != null), ct);

        if (soul == null)
        {
            _log("INFO", "[Tunnel] No linked soul — waiting. Link via /soul/link-server to activate the direct tunnel.");
            try
            {
                _delayCts?.Dispose();
                _delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await Task.Delay(TimeSpan.FromSeconds(30), _delayCts.Token);
            }
            catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; }
            return;
        }

        var hubUrl = soul.ServerUrl!.TrimEnd('/') + "/api/modelbridge";
        _log("INFO", $"[Tunnel] Connecting to {hubUrl}...");

        var hub = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .Build();
        _hub = hub;
        _ptySessions.SetHub(hub);

        // Handlers must return promptly so the SignalR client receive loop can process the next
        // server message. HandleRequest is long-running (it streams LLM tokens), so run it
        // fire-and-forget; otherwise local REST calls (e.g. metrics) would queue behind it.
        hub.On<BridgeRequest>("HandleRequest",
            req => { _ = HandleLlmRequestAsync(req); return Task.CompletedTask; });
        hub.On<string>("AbortRequest",
            requestId => { AbortRequest(requestId); return Task.CompletedTask; });
        hub.On<LocalRestRequest>("HandleLocalRest",
            req => { _ = HandleLocalRestAsync(req); return Task.CompletedTask; });
        hub.On<string, string>("TerminalInput",
            (sessionId, dataB64) => { _ = HandleTerminalInputAsync(sessionId, dataB64); return Task.CompletedTask; });
        hub.On<string, int, int>("TerminalResize",
            (sessionId, cols, rows) => { _ = _ptySessions.ResizeAsync(sessionId, cols, rows); return Task.CompletedTask; });
        hub.On<string>("TerminalClose",
            sessionId => { _ = _ptySessions.KillAsync(sessionId); return Task.CompletedTask; });

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += ex => { closed.TrySetResult(); return Task.CompletedTask; };

        await hub.StartAsync(ct);

        // Node identity: the primary bridge has no separate node key → it signs with the soul key,
        // which the server treats as the implicitly-allowed primary node. Additional bridges have
        // their own enrolled node keypair.
        var nodePriv = soul.NodePrivateKeyBase64 ?? soul.PrivateKeyBase64!;
        var nodePub  = soul.NodePublicKeyBase64  ?? soul.PublicKeyBase64!;
        var nodeLabel = soul.NodeLabel ?? Environment.MachineName;
        var platform = OperatingSystem.IsWindows() ? "Windows"
                     : OperatingSystem.IsMacOS()   ? "macOS"
                     : OperatingSystem.IsLinux()   ? "Linux" : "Unknown";

        // Challenge-response: server issues a nonce, bridge signs with its NODE private key
        var nonceB64 = await hub.InvokeAsync<string>("GetDaemonChallenge",
            soul.ServerSoulId!, ct);

        var nonce = Convert.FromBase64String(nonceB64);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(nodePriv), out _);
        var sig    = ecdsa.SignData(nonce, HashAlgorithmName.SHA256);
        var sigB64 = Convert.ToBase64String(sig);

        var ok = await hub.InvokeAsync<bool>("RegisterDirectBridge",
            soul.ServerSoulId!, nodePub, nodeLabel, platform, nonceB64, sigB64, ct);

        if (!ok)
        {
            await hub.DisposeAsync();
            _hub = null;

            // A joined node (own node key, no soul master key) that's rejected just isn't enrolled yet.
            // Register as a pending device + surface a pairing code so the human can approve it.
            var isPrimary = soul.PrivateKeyBase64 != null;
            if (!isPrimary)
                await RegisterPendingEnrollmentAsync(soul.ServerUrl!, soul.ServerSoulId!,
                    nodePub, nodeLabel, platform, ct);
            else
                _log("WARN", "[Tunnel] Auth rejected — ensure the soul public key on the server matches this bridge's key. Re-link via /soul/link-server.");

            throw new Exception("Authentication rejected by server");
        }

        CurrentJoinCode = null;   // enrolled & connected — no longer awaiting pairing
        _log("INFO", $"[Tunnel] Authenticated as soul {soul.ServerSoulId} — direct tunnel active.");

        // Start the periodic UI-access knock so the gate opens for this network IP.
        var knockCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hub.Closed += ex => { knockCts.Cancel(); return Task.CompletedTask; };
        _ = Task.Run(() => KnockLoopAsync(hub, knockCts.Token), CancellationToken.None);

        // Ensure this node holds the sync DEK (§11): the primary mints it; additional nodes fetch the
        // copy the approver wrapped to them at enrollment and unwrap it locally.
        await EnsureDataKeyAsync(hub, soul.Id, isPrimary: soul.PrivateKeyBase64 != null, nodePub, nodePriv, ct);

        // Layer B Phase 2: refresh the verifiable sibling roster now and periodically while connected.
        var rosterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hub.Closed += ex => { rosterCts.Cancel(); return Task.CompletedTask; };
        _ = Task.Run(() => SiblingRosterLoopAsync(hub, soul, rosterCts.Token), CancellationToken.None);

        using var reg = ct.Register(() => closed.TrySetResult());
        await closed.Task;

        try { rosterCts.Cancel(); } catch { }
        rosterCts.Dispose();

        try { knockCts.Cancel(); } catch { }
        knockCts.Dispose();

        // Drop any PTY sessions tied to this tunnel — the server can't route to them anymore.
        await _ptySessions.KillAllAsync();

        _log("INFO", "[Tunnel] Connection closed.");
    }

    // Ensures soul.DataKeyBase64 is populated. Primary mints a fresh DEK; an additional node asks the
    // server for the DEK the approver wrapped to it at enrollment and unwraps with its node key.
    private async Task EnsureDataKeyAsync(HubConnection hub, string soulId, bool isPrimary,
        string nodePub, string nodePriv, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db   = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
            var soul = await db.Souls.FirstOrDefaultAsync(s => s.Id == soulId, ct);
            if (soul == null || soul.DataKeyBase64 != null) return;

            if (isPrimary)
            {
                soul.DataKeyBase64 = SyncCrypto.GenerateDek();
                await db.SaveChangesAsync(ct);
                _log("INFO", "[Tunnel] Minted sync data key (primary node).");
                return;
            }

            var wrapped = await hub.InvokeAsync<string?>("GetWrappedDek",
                soul.ServerSoulId!, nodePub, ct);
            if (string.IsNullOrEmpty(wrapped))
            {
                _log("WARN", "[Tunnel] No wrapped data key available yet — sync will be inactive until enrollment delivers one.");
                return;
            }
            soul.DataKeyBase64 = SyncCrypto.UnwrapDek(wrapped, nodePriv);
            await db.SaveChangesAsync(ct);
            _log("INFO", "[Tunnel] Received and unwrapped sync data key (enrolled node).");
        }
        catch (Exception ex)
        {
            _log("WARN", $"[Tunnel] Data-key setup failed: {ex.Message}");
        }
    }

    // Periodic "knock" that tells the web UI the bridge is alive on this public IP, opening
    // the access gate for the same network even before the browser proves bridge control.
    private async Task KnockLoopAsync(HubConnection hub, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && hub.State == HubConnectionState.Connected)
        {
            try
            {
                await hub.InvokeAsync("UiAccessKnock", ct);
                _log("INFO", "[Knock] UI access knock sent.");
            }
            catch (Exception ex)
            {
                _log("WARN", $"[Knock] UI access knock failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Layer B Phase 2: keep the locally-verified sibling roster fresh so co-equal approvals are accepted.
    private async Task SiblingRosterLoopAsync(HubConnection hub, BridgeSoul soul, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && hub.State == HubConnectionState.Connected)
        {
            await _siblingRoster.RefreshAsync(hub, soul, ct);
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Registers this not-yet-enrolled node as a pending device on the server and prints the pairing
    // code the human enters in Aria.Web → Devices to approve it. Best-effort: failures just retry.
    private async Task RegisterPendingEnrollmentAsync(string serverUrl, string serverSoulId,
        string nodePub, string label, string platform, CancellationToken ct)
    {
        _joinCode ??= RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        CurrentJoinCode = _joinCode;
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                serverSoulId, nodePublicKey = nodePub, label, platform, code = _joinCode
            });
            using var req = new HttpRequestMessage(HttpMethod.Post,
                serverUrl.TrimEnd('/') + "/api/bridge/pending-enroll")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                _log("INFO", $"[Tunnel] Awaiting enrollment. In Aria.Web → Devices, approve this device with code: {_joinCode[..3]}-{_joinCode[3..]}");
            else
                _log("WARN", $"[Tunnel] Could not register for pairing ({(int)resp.StatusCode}). Retrying…");
        }
        catch (Exception ex)
        {
            _log("WARN", $"[Tunnel] Pairing registration failed: {ex.Message} — retrying.");
        }
    }

    // Receives BridgeRequest from the server, calls /llm/proxy on self, streams chunks back.
    private async Task HandleLlmRequestAsync(BridgeRequest req)
    {
        var hub = _hub;
        // IDLE timeout, not a total cap: a long-reasoning local model (e.g. deepseek-r1) can stream
        // for many minutes, and a total cap would cut its thinking mid-flight while LM Studio keeps
        // generating — the exact "thinking interrupted but the model's still going" bug. This fires
        // only when NO token has arrived for IdleTimeout; it's reset on every streamed line below, so
        // a stream that keeps producing tokens is never cut. HardCap is a walk-away backstop that
        // matches the web-side per-turn cap (30 min).
        var idleTimeout = TimeSpan.FromMinutes(4);
        using var idleCts    = new CancellationTokenSource(idleTimeout);
        using var hardCts    = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var requestCts = new CancellationTokenSource();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(idleCts.Token, hardCts.Token, requestCts.Token);
        _requestCts[req.RequestId] = requestCts;

        var sw = Stopwatch.StartNew();
        var chunkCount = 0;

        string? model = null;
        try
        {
            using var doc = JsonDocument.Parse(req.Body ?? "{}");
            if (doc.RootElement.TryGetProperty("model", out var m))
                model = m.GetString();
        }
        catch { /* body may not be JSON */ }

        _log("INFO", $"[LLM] {req.RequestId[..8]} start url={req.Url} model={model ?? "(unknown)"} keyRef={req.KeyRef ?? "(none)"}");

        // Layer B gate on the streaming path. Classify by the *inner* url path (the actual operation):
        // a /tools/call is a gated action; a provider completion url is benign, so plain chat is never
        // blocked. Session-scoped when the server stamped a session id.
        var innerPath = Uri.TryCreate(req.Url, UriKind.Absolute, out var u) ? u.AbsolutePath : req.Url;
        var toolRefusal = await GateSensitiveAsync("POST", innerPath, req.Body, req.SessionId);
        if (toolRefusal != null)
        {
            if (hub?.State == HubConnectionState.Connected)
                await hub.SendAsync("CompleteRequest", req.RequestId, false, toolRefusal);
            _requestCts.TryRemove(req.RequestId, out _);
            return;
        }

        try
        {
            var proxyBody = JsonSerializer.Serialize(new
            {
                url        = req.Url,
                body       = req.Body,
                keyRef     = req.KeyRef,
                apiKey     = req.ApiKey,
                requireKey = req.RequireKey
            });

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "http://localhost:5741/llm/proxy")
            {
                Content = new StringContent(proxyBody, Encoding.UTF8, "application/json")
            };
            using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(cts.Token);
                string? detail = null;
                try
                {
                    using var errDoc = JsonDocument.Parse(errBody);
                    detail = errDoc.RootElement.TryGetProperty("detail", out var d) ? d.GetString()
                           : errDoc.RootElement.TryGetProperty("title", out var t) ? t.GetString()
                           : null;
                }
                catch { /* not JSON */ }
                throw new InvalidOperationException(detail ?? $"/llm/proxy returned {(int)resp.StatusCode}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token)) != null &&
                   !cts.Token.IsCancellationRequested)
            {
                // A token arrived — push the idle deadline out. Only genuine silence (a stalled
                // upstream) trips the timeout; continuous reasoning output keeps it alive forever.
                idleCts.CancelAfter(idleTimeout);
                chunkCount++;
                // ReadLineAsync strips the line terminator; re-add \n so the server's
                // InterceptChunk (which splits on \n) can parse SSE lines correctly.
                if (hub?.State == HubConnectionState.Connected)
                    await hub.SendAsync("SendChunk", req.RequestId, line + "\n", cts.Token);
            }

            sw.Stop();
            _log("INFO", $"[LLM] {req.RequestId[..8]} complete {sw.ElapsedMilliseconds}ms chunks={chunkCount}");

            if (hub?.State == HubConnectionState.Connected)
                await hub.SendAsync("CompleteRequest", req.RequestId, true, (string?)null);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var why = requestCts.IsCancellationRequested ? "user-abort"
                    : idleCts.IsCancellationRequested    ? $"idle>{idleTimeout.TotalSeconds:0}s"
                    : hardCts.IsCancellationRequested    ? "hard-cap-30m"
                    : "canceled";
            _log("INFO", $"[LLM] {req.RequestId[..8]} {why} after {sw.ElapsedMilliseconds}ms chunks={chunkCount}");
            if (hub?.State == HubConnectionState.Connected)
            {
                try { await hub.SendAsync("CompleteRequest", req.RequestId, true, (string?)null); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log("ERROR", $"[LLM] {req.RequestId[..8]} failed after {sw.ElapsedMilliseconds}ms: {ex.Message}");
            if (hub?.State == HubConnectionState.Connected)
            {
                try
                {
                    // Generic failure path: stream the error message back as an SSE data line so the
                    // UI can display it. The web-side handler detects the ERROR: prefix and surfaces it.
                    var errorLine = $"data: ERROR:{ex.Message.Replace('\n', ' ').Replace('\r', ' ')}\n";
                    await hub.SendAsync("SendChunk", req.RequestId, errorLine, cts.Token);
                    await hub.SendAsync("CompleteRequest", req.RequestId, true, (string?)null);
                }
                catch { }
            }
        }
        finally
        {
            _requestCts.TryRemove(req.RequestId, out _);
        }
    }

    // Called by the server to forward raw terminal keystrokes into a PTY session.
    private async Task HandleTerminalInputAsync(string sessionId, string dataBase64)
    {
        try
        {
            var data = Convert.FromBase64String(dataBase64);
            await _ptySessions.WriteAsync(sessionId, data);
        }
        catch (Exception ex)
        {
            _log("WARN", $"[PTY] HandleTerminalInput failed: {ex.Message}");
        }
    }

    // Called by the server when the user presses STOP. Cancels the upstream LLM request.
    private void AbortRequest(string requestId)
    {
        if (_requestCts.TryRemove(requestId, out var cts))
        {
            try { cts.Cancel(); }
            catch { /* may already be disposed */ }
            _log("INFO", $"[LLM] {requestId[..8]} abort requested");
        }
    }

    // Throttle for auto-opening the local approval page — one blocked sensitive op shouldn't spawn a
    // browser tab per retry.
    private DateTime _lastApprovalPrompt = DateTime.MinValue;

    // Layer B gate (defense-in-depth plan §4): before running a server-relayed request, classify it.
    // Sensitive requests (provider-key spend, shell, tool execution) require a node-approved context
    // grant; without one the bridge refuses (fail-closed) and opens a local approval page. Enforcement
    // is OFF unless ARIA_BRIDGE_ENFORCE_GRANTS is set — when off, this only logs, changing nothing.
    // Returns a refusal message if the request must be blocked, or null to proceed.
    // Overload for the streaming path (BridgeRequest → /llm/proxy). The logical operation is decided
    // by the *inner* url: a /tools/call is a gated action, a provider completion url is benign — so
    // this gates the agent's tool actions without gating plain LLM chat.
    private Task<string?> GateSensitiveAsync(string method, string path, string? body, string? sessionId) =>
        GateSensitiveCoreAsync(method, path, body, sessionId);

    private async Task<string?> GateSensitiveCoreAsync(string method, string path, string? body, string? sessionId)
    {
        if (Aria.Shared.RequestClassifier.Classify(method, path, body) != Aria.Shared.RequestSensitivity.Sensitive)
            return null;

        using var scope = _scopes.CreateScope();
        var db   = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var soul = await db.Souls.AsNoTracking().FirstOrDefaultAsync(x => x.Name != "")
                   ?? await db.Souls.AsNoTracking().FirstOrDefaultAsync();

        // Session-scoped when the server stamped a session id on the request; else soul-wide (legacy).
        if (await ContextGrantStore.HasValidGrantForRequestAsync(db, soul, sessionId)) return null;

        if (!ContextGrantStore.EnforcementEnabled)
        {
            _log("INFO", $"[Gate] (observe) sensitive {method} {path} would require a context grant — enforcement off.");
            return null;
        }

        // Enforcing + no grant → refuse and surface the local approval page for this session.
        var approveUrl = "http://localhost:5741/context/approve"
            + (string.IsNullOrEmpty(sessionId) ? "" : $"?session={Uri.EscapeDataString(sessionId)}");
        // Only auto-open the page for legacy/soul-wide (no session) callers — e.g. the Console client.
        // For a session-scoped (Web) request the browser drives the reactive in-chat ceremony, which
        // opens ITS OWN approval page (/context/approve/request) AND polls for the verdict to retry the
        // turn. Auto-opening a second, separate page here just splits the human across two pages: they
        // approve this one, a grant is written, but the browser's ceremony never learns of it → no retry.
        if (string.IsNullOrEmpty(sessionId) && DateTime.UtcNow - _lastApprovalPrompt > TimeSpan.FromSeconds(30))
        {
            _lastApprovalPrompt = DateTime.UtcNow;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(approveUrl) { UseShellExecute = true });
            }
            catch { /* headless — page still reachable manually */ }
        }
        _log("WARN", $"[Gate] BLOCKED sensitive {method} {path} — no context grant. Approve at {approveUrl}");
        // Machine-readable prefix lets the server detect the refusal and extract the session id.
        var sessionTag = string.IsNullOrEmpty(sessionId) ? "" : $" sessionId='{sessionId}'";
        return $"[CONTEXT_APPROVAL_REQUIRED{sessionTag}] Context approval required — approve sensitive operations at your node ({approveUrl}).";
    }

    // Receives LocalRestRequest from the server, calls the local endpoint, returns the response.
    private async Task HandleLocalRestAsync(LocalRestRequest req)
    {
        var hub = _hub;

        // F-2: the server must not be able to drive arbitrary loopback endpoints. Refuse anything
        // not on the explicit tunnel allowlist before any local authority is exercised.
        if (!TunnelAllowlist.IsAllowed(req.Path))
        {
            _log("WARN", $"[Tunnel] BLOCKED disallowed path {req.Method} {req.Path}");
            if (hub?.State == HubConnectionState.Connected)
            {
                var refusalBody = System.Text.Json.JsonSerializer.Serialize(new
                {
                    error = $"Path not reachable through tunnel: {req.Path}",
                    path = req.Path,
                    tunnelAllowlistBlocked = true
                });
                await hub.SendAsync("CompleteLocalRest", req.RequestId, 403, refusalBody);
            }
            return;
        }

        // Layer B gate: refuse classified-sensitive requests that lack a node-approved context grant.
        var refusal = await GateSensitiveAsync(req.Method, req.Path, req.Body, req.SessionId);
        if (refusal != null)
        {
            if (hub?.State == HubConnectionState.Connected)
                await hub.SendAsync("CompleteLocalRest", req.RequestId, 403,
                    System.Text.Json.JsonSerializer.Serialize(new { error = refusal, contextApprovalRequired = true, sessionId = req.SessionId }));
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            var method = req.Method.ToUpperInvariant() switch
            {
                "POST"   => HttpMethod.Post,
                "PUT"    => HttpMethod.Put,
                "DELETE" => HttpMethod.Delete,
                _        => HttpMethod.Get
            };

            var httpReq = new HttpRequestMessage(method, "http://localhost:5741" + req.Path);
            if (!string.IsNullOrEmpty(req.Body))
                httpReq.Content = new StringContent(req.Body, Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(httpReq);
            var body = await resp.Content.ReadAsStringAsync();

            if (hub?.State == HubConnectionState.Connected)
                await hub.SendAsync("CompleteLocalRest", req.RequestId, (int)resp.StatusCode, body);
        }
        catch (Exception ex)
        {
            _log("ERROR", $"[Tunnel] HandleLocalRest {req.Path} failed: {ex.Message}");
            if (hub?.State == HubConnectionState.Connected)
            {
                try { await hub.SendAsync("CompleteLocalRest", req.RequestId, 500, ex.Message); }
                catch { }
            }
        }
    }
}
