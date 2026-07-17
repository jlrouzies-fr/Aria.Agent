using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;

namespace Aria.Tools;

public enum McpTransport { Stdio, Sse, LocalBridge }

public record McpServerConfig(
    string Name,
    string Command,
    string[] Arguments,
    bool Enabled = true,
    Dictionary<string, string>? Environment = null,
    McpTransport Transport = McpTransport.Stdio,
    string? Url = null,
    // Terminal security policy — null means no restriction.
    string[]? AllowedPaths    = null,
    string[]? BlockedCommands = null);

public static class McpTools
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .Build();

    public static Task<IList<McpClientTool>> GetTools() => GetTools(null);

    public static async Task<IList<McpClientTool>> GetTools(IEnumerable<McpServerConfig>? userServers)
    {
        var serversSection = _configuration.GetSection("MCP");

        var configs = serversSection.GetChildren().Select(configure =>
        {
            return new McpServerConfig(
                Name: configure["Name"] ?? throw new InvalidOperationException($"MCP server missing 'Name'"),
                Command: configure["Command"] ?? throw new InvalidOperationException($"MCP server '{configure["Name"]}' missing 'Command'"),
                Arguments: (configure.GetSection("Arguments").GetChildren()).Any()
                    ? configure.GetSection("Arguments").GetChildren().Select(a => a.Value!).ToArray()
                    : [],
                Enabled: configure.GetValue<bool>("Enabled", true),
                Environment: configure.GetSection("Environment").Get<Dictionary<string, string>>()
            );
        }).ToList();

        if (userServers != null)
            configs.AddRange(userServers);

        // LocalBridge servers are handled by AgentService via the browser tunnel — skip here.
        // Stdio is rejected for user-provided servers: spawning arbitrary processes server-side
        // is a code-injection risk in any shared or remote deployment. Stdio remains valid for
        // appsettings.json servers (admin-controlled) used by Aria.Console.
        configs.RemoveAll(s => s.Transport == McpTransport.LocalBridge);
        if (userServers != null)
            configs.RemoveAll(s => s.Transport == McpTransport.Stdio);

        if (configs.Count == 0) return [];

        var allTools = new List<McpClientTool>();

        foreach (var server in configs)
        {
            if (!server.Enabled) continue;

            try
            {
                McpClient mcpClient;

                if (server.Transport == McpTransport.Sse && !string.IsNullOrEmpty(server.Url))
                {
                    var httpTransport = new HttpClientTransport(new HttpClientTransportOptions
                    {
                        Endpoint = new Uri(server.Url),
                        Name     = server.Name,
                    });
                    mcpClient = await McpClient.CreateAsync(httpTransport).ConfigureAwait(false);
                }
                else
                {
                    var transportOptions = new StdioClientTransportOptions
                    {
                        Name      = server.Name,
                        Command   = server.Command,
                        Arguments = [.. server.Arguments],
                        EnvironmentVariables = server.Environment?.Count > 0
                            ? new Dictionary<string, string?>(server.Environment!)
                            : null,
                    };
                    var stdioTransport = new StdioClientTransport(transportOptions);
                    mcpClient = await McpClient.CreateAsync(stdioTransport).ConfigureAwait(false);
                }

                var tools = await mcpClient.ListToolsAsync().ConfigureAwait(false);
                allTools.AddRange(tools);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MCP] Warning: failed to connect to server '{server.Name}': {ex.Message}");
            }
        }

        return allTools;
    }
}