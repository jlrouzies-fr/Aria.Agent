using Aria.Web.Data;
using Aria.Web.Services;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Layout;

// Bridge identity tracking, extracted from NavMenu for cohesion.
// Partial members of the same NavMenu component (shared fields/services live in NavMenu.razor.cs).
//
// Soul verification is fully handled by the daemon's ECDSA challenge-response in
// ModelBridgeHub.RegisterDirectBridge. This file handles NavMenu side-effects: refreshing
// configured providers when the daemon connects, and updating soul state on disconnect.
public partial class NavMenu
{
    // THE gate (§12): true only when THIS browser circuit has proven, via its own localhost bridge,
    // that it controls a key enrolled for the selected soul. Per-circuit — a bridge connected from
    // another machine does NOT unlock this browser. Everything soul-scoped keys off this.
    internal bool SoulVerified =>
        SessionState.CurrentUser != null &&
        CircuitAuth.IsVerified(SessionState.CurrentUser.Id);

    // Attestation in flight (so the locked panel can show "verifying" vs "no bridge").
    internal bool _attesting;

    /// <summary>Proves this circuit controls the selected soul's local bridge. Idempotent; safe to
    /// call on first render, after a soul switch, and when a node connects.</summary>
    internal async Task AttestCircuitAsync()
    {
        if (_attesting || SessionState.CurrentUser is not { } u || CircuitAuth.IsVerified(u.Id)) return;
        _attesting = true;
        try
        {
            var payload = CircuitAuth.Begin(u.Id);
            var json = await JS.InvokeAsync<string?>("ariaInterop.attestViaLocalBridge", new object[] { payload });
            if (string.IsNullOrEmpty(json))
            { Log.LogWarning("[Attest] Browser could not reach the local bridge (attest for {UserId})", u.Id); return; }
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var pub = doc.RootElement.GetProperty("publicKey").GetString();
            var sig = doc.RootElement.GetProperty("signature").GetString();
            if (pub != null && sig != null)
            {
                var ok = await CircuitAuth.CompleteAsync(u.Id, pub, sig);   // sets SoulVerified → fires status event
                if (!ok) Log.LogWarning("[Attest] CompleteAsync rejected attestation for {UserId}", u.Id);
            }
        }
        catch (Exception ex) { Log.LogWarning(ex, "[Attest] Attestation attempt failed for {UserId}", u.Id); /* remain locked */ }
        finally
        {
            _attesting = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>On page load, ask the local bridge which soul it is. In the one-bridge = one-soul
    /// model this selects the current user without needing localStorage or manual picking.</summary>
    internal async Task DiscoverAndSelectUserAsync()
    {
        if (_attesting || SessionState.CurrentUser != null) return;
        _attesting = true;
        try
        {
            var payload = CircuitAuth.DiscoverBegin();
            var json = await JS.InvokeAsync<string?>("ariaInterop.discoverViaLocalBridge", new object[] { payload });
            if (string.IsNullOrEmpty(json))
            { Log.LogInformation("[Attest] Discovery: browser could not reach a local bridge"); return; }
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var pub = doc.RootElement.GetProperty("publicKey").GetString();
            var sig = doc.RootElement.GetProperty("signature").GetString();
            if (pub == null || sig == null) return;

            var user = await CircuitAuth.DiscoverCompleteAsync(pub, sig);
            if (user == null)
            { Log.LogWarning("[Attest] Discovery: bridge signed but DiscoverCompleteAsync matched no soul"); return; }

            _users = await UserService.GetUsersAsync();
            if (!_users.Any(u => u.Id == user.Id))
                _users.Add(user);

            await SelectUserAsync(user, skipAttest: true); // already verified by discovery
        }
        catch (Exception ex) { Log.LogWarning(ex, "[Attest] Discovery attempt failed"); /* stay on onboarding */ }
        finally
        {
            _attesting = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    // Daemon connected and authenticated — reload provider list so key icons show immediately.
    internal void OnDirectBridgeRegisteredNav(string userId)
    {
        if (SessionState.CurrentUser?.Id.ToString() != userId) return;
        _ = InvokeAsync(async () =>
        {
            await RefreshConfiguredProvidersAsync();
            StateHasChanged();
        });
    }

    // Daemon disconnected — re-render so SoulVerified → false is reflected immediately.
    internal void OnDirectBridgeDisconnectedNav(string userId)
    {
        if (SessionState.CurrentUser?.Id.ToString() != userId) return;
        _ = InvokeAsync(StateHasChanged);
    }

    // A node connected/disconnected — refresh the devices list and channel node pickers (online dots),
    // and (re)try attestation in case the user just started THIS machine's local bridge.
    internal void OnNodesChangedNav(string userId)
    {
        if (SessionState.CurrentUser == null)
        {
            // No soul selected on this circuit yet. If THIS machine's bridge was just enrolled
            // (new-device approval), discovery can now succeed — unlock without a manual refresh.
            _ = InvokeAsync(DiscoverAndSelectUserAsync);
            return;
        }
        if (SessionState.CurrentUser.Id.ToString() != userId) return;
        _ = InvokeAsync(async () =>
        {
            await AttestCircuitAsync();
            // Channels are node-authoritative and each node holds its own — but the picker aggregates
            // across ALL connected nodes, so a node connecting/dropping changes the available channels.
            // Re-fetch so the model picker reflects the new roster live (no manual refresh needed).
            await RefreshUserSourcesAsync(userId);
            if (_activePanel == "devices") await LoadNodesAsync();
            else                           StateHasChanged();
        });
    }

    // Tool config saved (possibly from ANOTHER device's circuit) — reload this circuit's tool state
    // so panels reflect it live instead of requiring a page refresh.
    internal void OnToolsChangedNav(string userId)
    {
        if (SessionState.CurrentUser?.Id != userId) return;
        _ = InvokeAsync(async () =>
        {
            var states = await ToolService.GetToolStatesAsync(userId);
            SessionState.LoadToolStates(states);
            SessionState.Governance = await ToolService.GetGovernanceModeAsync(userId);
            (SessionState.AutoMemory, SessionState.AutoMemoryInterval) = await ToolService.GetAutoMemorySettingsAsync(userId);
            SessionState.RecallScope = await ToolService.GetRecallScopeAsync(userId);
            await SyncTerminalAnchorsAsync(userId);
            StateHasChanged();
        });
    }

    internal async void OnSoulUnlinkedNav(string userId) =>
        await InvokeAsync(StateHasChanged);

    internal async void OnSoulRegisteredNav(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;
        _users = await UserService.GetUsersAsync();

        // Fresh install / nothing selected: adopt the new soul so the daemon can connect.
        if (SessionState.CurrentUser == null)
        {
            var u = _users.FirstOrDefault(x => x.Id == userId);
            if (u != null) await InvokeAsync(() => SelectUserAsync(u));
        }

        await InvokeAsync(StateHasChanged);
    }

    // Single implementation of "paste a session code → unlock the matching soul for this circuit and
    // switch to it". Registered on SessionState so the gateway modal routes through the same path.
    internal async Task<(bool Ok, string? Error)> HandleCodeUnlockAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return (false, "Enter the session code from your bridge.");
        try
        {
            // The code self-identifies its soul: unlock, then switch the UI to that soul (it may differ
            // from whatever was auto-selected — e.g. the picker defaulted to a soul on a different host).
            var (ok, uid, error) = await CircuitAuth.UnlockByCodeAsync(code);
            if (!ok || uid == null) return (false, error ?? "Unlock failed.");

            // Switch to the matched soul FIRST, then mark verified so the status event fires with that
            // soul current — this makes every gated surface settle on a single click.
            if (SessionState.CurrentUser?.Id != uid)
            {
                _users = await UserService.GetUsersAsync();
                var u = _users.FirstOrDefault(x => x.Id == uid);
                if (u != null) await SelectUserAsync(u);
            }
            CircuitAuth.MarkVerified(uid);

            // Cache for this tab so a refresh re-unlocks silently (re-verified live against the bridge).
            try { await JS.InvokeVoidAsync("sessionStorage.setItem", "aria_unlock_code", code.Trim()); } catch { }
            return (true, null);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "[Attest] Code unlock failed after server match (soul switch / verify step threw)");
            return (false, "Unlock failed.");
        }
    }

    // On (re)load: if this tab has a cached code and isn't verified yet, try it silently — so a refresh
    // doesn't force the user to re-enter the code. Safe: the server re-verifies it against the live bridge.
    internal async Task TryCachedUnlockAsync()
    {
        if (SessionState.CurrentUser is { } u && CircuitAuth.IsVerified(u.Id)) return;
        string? cached = null;
        try { cached = await JS.InvokeAsync<string?>("sessionStorage.getItem", "aria_unlock_code"); } catch { }
        if (string.IsNullOrWhiteSpace(cached)) return;
        var (ok, err) = await HandleCodeUnlockAsync(cached);
        Log.LogInformation("[Attest] Cached session-code retry: {Result}", ok ? "unlocked" : $"failed ({err})");
        if (!ok) { try { await JS.InvokeVoidAsync("sessionStorage.removeItem", "aria_unlock_code"); } catch { } }
        await InvokeAsync(StateHasChanged);
    }
}
