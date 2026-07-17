using System.Text.Json;
using Aria.Tools;
using Aria.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Services.Tool;

public class UserMcpService(IDbContextFactory<AppDbContext> dbFactory, BridgeSyncService? sync = null)
{
    public async Task<List<UserMcpServer>> GetServersAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.UserMcpServers
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<UserMcpServer> AddServerAsync(
        string userId, string name,
        McpTransport transport = McpTransport.Stdio,
        string command = "", string[] args = default!, bool enabled = true,
        Dictionary<string, string>? env = null, string? url = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var server = new UserMcpServer
        {
            UserId    = userId,
            Name      = name.Trim(),
            Transport = transport,
            Command   = command.Trim(),
            ArgsJson  = JsonSerializer.Serialize(args ?? []),
            EnvJson   = env != null && env.Count > 0 ? JsonSerializer.Serialize(env) : null,
            Url       = url?.Trim(),
            Enabled   = enabled,
        };
        db.UserMcpServers.Add(server);
        await db.SaveChangesAsync();

        _ = sync?.PushSnapshotAsync(userId);
        return server;
    }

    public async Task UpdateServerAsync(
        int serverId, string name,
        McpTransport transport = McpTransport.Stdio,
        string command = "", string[] args = default!, bool enabled = true,
        Dictionary<string, string>? env = null, string? url = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var server = await db.UserMcpServers.FirstAsync(s => s.Id == serverId);
        server.Name      = name.Trim();
        server.Transport = transport;
        server.Command   = command.Trim();
        server.ArgsJson  = JsonSerializer.Serialize(args ?? []);
        server.EnvJson   = env != null && env.Count > 0 ? JsonSerializer.Serialize(env) : null;
        server.Url       = url?.Trim();
        server.Enabled   = enabled;
        await db.SaveChangesAsync();

        _ = sync?.PushSnapshotAsync(server.UserId);
    }

    public async Task SetEnabledAsync(int serverId, bool enabled)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var server = await db.UserMcpServers.FirstAsync(s => s.Id == serverId);
        await db.UserMcpServers
            .Where(s => s.Id == serverId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, enabled));

        _ = sync?.PushSnapshotAsync(server.UserId);
    }

    public async Task DeleteServerAsync(int serverId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var server = await db.UserMcpServers.FirstAsync(s => s.Id == serverId);
        await db.UserMcpServers.Where(s => s.Id == serverId).ExecuteDeleteAsync();

        _ = sync?.PushSnapshotAsync(server.UserId);
    }

    public static McpServerConfig ToConfig(UserMcpServer s) => new(
        Name:        s.Name,
        Command:     s.Command,
        Arguments:   s.ArgsJson != null
            ? JsonSerializer.Deserialize<string[]>(s.ArgsJson) ?? []
            : [],
        Enabled:     s.Enabled,
        Environment: s.EnvJson != null
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(s.EnvJson)
            : null,
        Transport:   s.Transport,
        Url:         s.Url
    );
}
