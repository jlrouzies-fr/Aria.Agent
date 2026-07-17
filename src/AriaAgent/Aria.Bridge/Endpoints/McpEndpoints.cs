using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Infrastructure;
using Aria.Bridge.Services.Logging;
using Aria.Shared;
using Aria.Tools;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Endpoints;

/// <summary>
/// Node-authoritative MCP server configuration. Servers are authored ONLY on this node (bridge status
/// page); the server receives a read-only name list through <c>GET /mcps</c> and never sees env secrets.
/// </summary>
public static class McpEndpoints
{
    public static void MapMcpEndpoints(this WebApplication app)
    {
        // GET /mcps — read-only mirror the web fetches over the tunnel. No env values are returned.
        app.MapGet("/mcps", async (BridgeDbContext db) =>
        {
            var servers = await db.McpServers
                .AsNoTracking()
                .OrderBy(s => s.SortOrder)
                .ThenBy(s => s.Name)
                .ToListAsync();

            return Results.Ok(new
            {
                servers = servers.Select(s => new
                {
                    name      = s.Name,
                    transport = s.Transport,
                    command   = s.Command,
                    url       = s.Url,
                    enabled   = s.Enabled,
                    argsCount = ParseArgs(s.ArgsJson).Length,
                })
            });
        });

        // GET /mcps/{name} — full local-only config including env secrets, for the bridge UI edit form.
        // NOT on the tunnel allowlist; requires local origin so env never leaves this node.
        app.MapGet("/mcps/{name}", async (string name, HttpContext ctx, BridgeDbContext db) =>
        {
            if (!LocalRequestGuard.IsLocalOrigin(ctx.Request))
                return Results.Forbid();

            name = name.Trim();
            var s = await db.McpServers.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name);
            if (s == null) return Results.NotFound();

            return Results.Ok(new
            {
                name      = s.Name,
                transport = s.Transport,
                command   = s.Command,
                args      = ParseArgs(s.ArgsJson),
                env       = DeserializeEnv(s.EnvJson),
                url       = s.Url,
                enabled   = s.Enabled,
            });
        });

        // PUT /mcps/{name} — create/update. Local-origin only (not tunnel-relayable).
        app.MapPut("/mcps/{name}", async (string name, SaveMcpRequest req, BridgeDbContext db) =>
        {
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest("server name required");

            var transport = (McpTransport)req.Transport;
            if (transport == McpTransport.Sse && string.IsNullOrWhiteSpace(req.Url))
                return Results.BadRequest("SSE server requires a URL");
            if (transport != McpTransport.Sse && string.IsNullOrWhiteSpace(req.Command))
                return Results.BadRequest("command required");

            var argsJson = JsonSerializer.Serialize(
                (req.Args ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToArray());
            var envJson = req.Env != null && req.Env.Count > 0
                ? JsonSerializer.Serialize(req.Env)
                : null;

            var existing = await db.McpServers.FirstOrDefaultAsync(s => s.Name == name);
            if (existing == null)
            {
                var maxSort = await db.McpServers.AnyAsync() ? await db.McpServers.MaxAsync(s => s.SortOrder) : 0;
                db.McpServers.Add(new BridgeMcpServer
                {
                    Name      = name,
                    Transport = (int)transport,
                    Command   = (req.Command ?? "").Trim(),
                    ArgsJson  = argsJson,
                    EnvJson   = envJson,
                    Url       = string.IsNullOrWhiteSpace(req.Url) ? null : req.Url.Trim(),
                    Enabled   = req.Enabled ?? true,
                    SortOrder = maxSort + 1,
                });
            }
            else
            {
                existing.Transport = (int)transport;
                existing.Command   = (req.Command ?? "").Trim();
                existing.ArgsJson  = argsJson;
                existing.EnvJson   = envJson;
                existing.Url       = string.IsNullOrWhiteSpace(req.Url) ? null : req.Url.Trim();
                existing.Enabled   = req.Enabled ?? existing.Enabled;
            }

            await db.SaveChangesAsync();
            BridgeLogger.Log("INFO", $"MCP server saved: {name}");
            return Results.Ok(new { ok = true });
        });

        // DELETE /mcps/{name} — remove. Local-origin only.
        app.MapDelete("/mcps/{name}", async (string name, BridgeDbContext db) =>
        {
            name = name.Trim();
            var existing = await db.McpServers.FirstOrDefaultAsync(s => s.Name == name);
            if (existing != null)
            {
                db.McpServers.Remove(existing);
                await db.SaveChangesAsync();
                BridgeLogger.Log("INFO", $"MCP server deleted: {name}");
            }
            return Results.Ok(new { ok = true });
        });

        // POST /mcps/{name}/probe — test a saved server by listing tools. Local-origin only.
        app.MapPost("/mcps/{name}/probe", async (string name, BridgeDbContext db, SessionStore sessions) =>
        {
            name = name.Trim();
            var config = await ResolveMcpConfigAsync(db, name);
            if (config == null)
                return Results.Ok(new { ok = false, error = "Server not found" });

            return await ProbeMcpAsync(config, sessions);
        });

        // POST /mcps/probe — test an unsaved config (used by the bridge UI before saving).
        app.MapPost("/mcps/probe", async (SaveMcpRequest req, SessionStore sessions) =>
        {
            var transport = (McpTransport)req.Transport;
            var config = new McpServerConfig(
                Name:        "probe",
                Command:     (req.Command ?? "").Trim(),
                Arguments:   (req.Args ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToArray(),
                Enabled:     true,
                Environment: req.Env != null && req.Env.Count > 0 ? req.Env : null,
                Transport:   transport,
                Url:         string.IsNullOrWhiteSpace(req.Url) ? null : req.Url.Trim());

            return await ProbeMcpAsync(config, sessions);
        });
    }

    private static async Task<IResult> ProbeMcpAsync(McpServerConfig config, SessionStore sessions)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var session = await sessions.GetOrCreateAsync(config);
            session.Touch();
            var tools = await session.Client.ListToolsAsync();
            sw.Stop();
            return Results.Ok(new { ok = true, toolCount = tools.Count, latencyMs = (int)sw.ElapsedMilliseconds });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Results.Ok(new { ok = false, error = ex.Message, latencyMs = (int)sw.ElapsedMilliseconds });
        }
    }

    internal static async Task<McpServerConfig?> ResolveMcpConfigAsync(BridgeDbContext db, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var s = await db.McpServers.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name);
        if (s == null) return null;

        return new McpServerConfig(
            Name:        s.Name,
            Command:     s.Command,
            Arguments:   ParseArgs(s.ArgsJson),
            Enabled:     s.Enabled,
            Environment: DeserializeEnv(s.EnvJson),
            Transport:   (McpTransport)s.Transport,
            Url:         s.Url);
    }

    private static string[] ParseArgs(string? json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? []; }
        catch { return []; }
    }

    private static Dictionary<string, string>? DeserializeEnv(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch { return null; }
    }
}

public record SaveMcpRequest(
    int Transport,
    string? Command = null,
    string[]? Args = null,
    Dictionary<string, string>? Env = null,
    string? Url = null,
    bool? Enabled = null);
