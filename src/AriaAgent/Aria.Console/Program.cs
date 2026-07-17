using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using NTokenizers.Extensions.Spectre.Console;
using NTokenizers.Extensions.Spectre.Console.Styles;
using Spectre.Console;
using Aria.Agent;
using Aria.Console;
using Aria.Console.Harness;
using Aria.Harness;
using Aria.Harness.Core;
using Aria.Harness.Formats;
using Aria.Harness.Tools;
using Aria.Shared;
using Aria.Tools;
using Microsoft.Extensions.Logging.Abstractions;

// ==========================
// Aria Agent — Console Edition
// ==========================

// Wipe debug logs from previous run
foreach (var log in Directory.GetFiles(Directory.GetCurrentDirectory(), "*.log"))
    File.Delete(log);

ConsoleHelper.ShowHeader();
AnsiConsole.Write(AriaRetroChrome.Rule("Boot"));

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .Build();

// ===========================
// Ensure the local bridge is running
// ===========================

var bridge = await EnsureBridgeAsync();
await EnsureSoulAsync(bridge);

AnsiConsole.Write(AriaRetroChrome.Rule("Auth"));

string tenantId = config["Azure:TenantId"] ?? string.Empty;
string clientId = config["Azure:ApplicationId"] ?? string.Empty;
if (!string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(clientId))
    Aria.Tools.GraphTools.Configure(tenantId, clientId);

string googleCredentialsFile = config["Google:CredentialsFile"] ?? string.Empty;
if (!string.IsNullOrWhiteSpace(googleCredentialsFile))
    Aria.Tools.GoogleTools.Configure(googleCredentialsFile);

// Noosphere memory — native to the local bridge, no separate service to configure.
bool memoryAvailable = await bridge.IsMemoryAvailableAsync();
if (memoryAvailable)
    AriaRetroChrome.Success("Noosphere memory array is available.");
else
    AriaRetroChrome.Warning("Noosphere memory is not available on this bridge, Agent won't inscribe memory of conversations between sessions.");

// Authenticate to Microsoft Graph (interactive, browser-based)
string userIdentity = "";
bool graphAvailable = false;
if (!string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(clientId))
{
    try
    {
        AriaRetroChrome.Info("Authenticating to Microsoft Graph...");
        var user = await Aria.Tools.GraphTools.EnsureAuthenticatedAsync();
        userIdentity = user?.DisplayName ?? "";
        graphAvailable = true;
        AriaRetroChrome.Success($"Microsoft Graph authenticated as {userIdentity}.");
    }
    catch (Exception ex)
    {
        AriaRetroChrome.Error($"Warning: Could not authenticate to Microsoft Graph: {ex.Message}");
        AriaRetroChrome.Warning("Graph tools will be unavailable.");
    }
}

// Authenticate to Google (interactive, browser-based)
bool googleAvailable = false;
if (!string.IsNullOrWhiteSpace(googleCredentialsFile))
{
    try
    {
        AriaRetroChrome.Info("Authenticating to Google...");
        var googleEmail = await Aria.Tools.GoogleTools.EnsureAuthenticatedAsync();
        googleAvailable = true;
        if (string.IsNullOrEmpty(userIdentity)) userIdentity = googleEmail;
        AriaRetroChrome.Success($"Google authenticated as {googleEmail}.");
    }
    catch (Exception ex)
    {
        AriaRetroChrome.Error($"Warning: Could not authenticate to Google: {ex.Message}");
        AriaRetroChrome.Warning("Google tools will be unavailable.");
    }
}

AnsiConsole.Write(AriaRetroChrome.Rule("Sync"));

// ======================
// Agent Harness
// ======================

var runtime = new ConsoleHarnessRuntime(config);
var harness = new Harness(NullLogger<Aria.Harness.Core.Harness>.Instance, runtime);

if (graphAvailable) runtime.SetOAuthToken("microsoft", "configured");
if (googleAvailable) runtime.SetOAuthToken("google", "configured");

// Load synced config from the bridge
var agentsTask = bridge.GetAgentsAsync();
var toolConfigsTask = bridge.GetToolConfigsAsync();
var localSourcesTask = bridge.GetLocalSourcesAsync();
var mcpServersTask = bridge.GetMcpServersAsync();
await Task.WhenAll(agentsTask, toolConfigsTask, localSourcesTask, mcpServersTask);

var agents = agentsTask.Result;
var toolConfigs = toolConfigsTask.Result.ToDictionary(c => c.ToolId, StringComparer.OrdinalIgnoreCase);
var localSources = localSourcesTask.Result;
var mcpServers = mcpServersTask.Result;

// Select agent
var selectedAgent = ConsoleHelper.SelectAgent(agents);

// Select source and model
var (selectedSourceName, selectedModel) = ConsoleHelper.SelectSourceAndModel(localSources);

AnsiConsole.Write(AriaRetroChrome.Rule("Channel"));

// Build enabled tool list from agent + user choices
var enabledTools = ConsoleHelper.SelectTools(
    selectedAgent,
    toolConfigs,
    graphAvailable,
    googleAvailable,
    memoryAvailable,
    mcpServers,
    config);

// Resolve MCP servers for the agent
IEnumerable<McpServerConfig> userMcpServers = [];
if (selectedAgent?.EnabledMcpNamesJson is { } json && !string.IsNullOrWhiteSpace(json))
{
    List<string> enabledMcpNames;
    try { enabledMcpNames = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
    catch { enabledMcpNames = []; }

    userMcpServers = mcpServers
        .Where(s => enabledMcpNames.Contains(s.Name))
        .Select(s => new McpServerConfig(
            s.Name,
            s.Command,
            DeserializeArgs(s.ArgsJson),
            s.Enabled,
            DeserializeEnv(s.EnvJson),
            (McpTransport)s.Transport,
            s.Url))
        .ToList();
}

Action<string> onThinkingToken = text => AnsiConsole.Write(
    AriaRetroChrome.Card(
        new Markup($"[dim {AriaRetroChrome.MutedText.ToMarkup()}]{Markup.Escape(text)}[/]"),
        "Reasoning",
        expand: true));

var options = new HarnessOptions
{
    SelectedSourceName = selectedSourceName,
    SelectedModel = selectedModel,
    ThinkingFormat = ThinkingFormat.None,
    EnabledTools = enabledTools,
    UserMcpServers = userMcpServers,
    InstructionsOverride = selectedAgent?.UserDirectives,
    AgentNameOverride = selectedAgent?.DisplayName,
    OnThinkingToken = onThinkingToken,
    OnProgress = msg => AriaRetroChrome.Info(msg),
    OnToolStart = (tool, args) => AriaRetroChrome.Info($"⟳ {tool}"),
    OnToolComplete = (tool, result, imageBase64, imageMediaType, metadataJson) => AriaRetroChrome.Success($"✓ {tool}")
};

var context = HarnessContext.Empty;
var (agent, session) = await harness.CreateSessionAsync(options, context);

AnsiConsole.Write(AriaRetroChrome.Rule("Session"));

// ======================
// Chat Session
// ======================

string? input = null;
while (true)
{
    AnsiConsole.WriteLine();

    string currentMessage = !string.IsNullOrWhiteSpace(input)
        ? input
        : $"Soul entering the chat with you is named {userIdentity}. Present yourself and hail the soul only one time, and recall its memories.";

    File.AppendAllText("agent-debug.log", $"[turn start] {DateTime.Now:O} source={selectedSourceName} model={selectedModel} msg={currentMessage.Length}ch\n");

    if (memoryAvailable && !string.IsNullOrWhiteSpace(input))
    {
        AriaRetroChrome.Info("Inscribing Input...");
        await bridge.InscribeMemoryAsync(input);
    }

    AnsiConsole.Write(AriaRetroChrome.Rule("Aria"));

    Pipe pipe = new();
    bool receivedAny = false;

    Task producer = Task.Run(async () =>
    {
        try
        {
            bool started = false;
            await foreach (var text in harness.StreamAsync(currentMessage, agent, session, context))
            {
                if (string.IsNullOrEmpty(text)) continue;
                var output = text;
                if (!started)
                {
                    output = text.TrimStart();
                    if (output.Length == 0) continue;
                    started = true;
                }
                receivedAny = true;
                await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(output));
            }
            await pipe.Writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            File.AppendAllText("agent-debug.log", $"[exception] {ex}\n");
            AriaRetroChrome.Error($"Agent stream failed: {ex.Message}");
            await pipe.Writer.CompleteAsync();
        }
    });

    try
    {
        await AnsiConsole.Console.WriteMarkdownAsync(pipe.Reader.AsStream(), AriaMarkdownTheme.Styles);
        await producer;
    }
    catch (Exception ex)
    {
        File.AppendAllText("agent-debug.log", $"[render exception] {ex}\n");
        AriaRetroChrome.Error($"Could not render Aria's response: {ex.Message}");
    }

    File.AppendAllText("agent-debug.log", $"[turn end] receivedAny={receivedAny}\n");

    if (!receivedAny)
    {
        AriaRetroChrome.Warning("Aria did not produce a response.");
        DumpDebugLogTail("DebugLogs/foundry-request.log", "Last request");
        DumpDebugLogTail("DebugLogs/universal-sse-debug.log", "Last SSE stream");
    }

    AnsiConsole.Write(AriaRetroChrome.Rule());

    AriaRetroChrome.StatusBar();
    AriaRetroChrome.Prompt("User");
    input = ConsoleHelper.ReadInput();
    if (string.IsNullOrWhiteSpace(input)) continue;

    AnsiConsole.Write(AriaRetroChrome.UserCard(input));

    if (input.Trim() == "/exit") break;
    if (input.Trim() == "/reset")
    {
        (agent, session) = await harness.CreateSessionAsync(options, context);
        AriaRetroChrome.Info("Session reset.");
        input = null;
        continue;
    }
}

// ===========================
// Helpers
// ===========================

static void DumpDebugLogTail(string path, string title, int maxLines = 60)
{
    if (!File.Exists(path)) return;
    try
    {
        var lines = File.ReadAllLines(path);
        var tail = lines.Skip(Math.Max(0, lines.Length - maxLines));
        var text = string.Join(Environment.NewLine, tail.Select(Markup.Escape));
        if (string.IsNullOrWhiteSpace(text)) return;

        AnsiConsole.Write(AriaRetroChrome.Card(
            new Markup($"[dim]{text}[/]"),
            title,
            expand: true));
    }
    catch { /* best-effort diagnostics */ }
}

static async Task<BridgeConsoleClient> EnsureBridgeAsync()
{
    var bridge = new BridgeConsoleClient();
    if (await bridge.IsHealthyAsync()) return bridge;

    AriaRetroChrome.Warning("Local Aria.Bridge is not running. Starting it now...");

    var bridgeProject = FindBridgeProjectPath();
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"run --project \"{bridgeProject}\" --urls \"http://localhost:5741\"",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
    };
    Process.Start(psi);

    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline)
    {
        if (await bridge.IsHealthyAsync()) return bridge;
        await Task.Delay(500);
    }

    throw new InvalidOperationException("Aria.Bridge did not become healthy in time.");
}

static string FindBridgeProjectPath()
{
    var searchRoots = new[]
    {
        Directory.GetCurrentDirectory(),
        AppContext.BaseDirectory,
    }.Distinct(StringComparer.OrdinalIgnoreCase);

    foreach (var root in searchRoots)
    {
        if (string.IsNullOrWhiteSpace(root)) continue;
        var dir = new DirectoryInfo(root);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Aria.Bridge", "Aria.Bridge.csproj");
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }
    }

    throw new FileNotFoundException("Could not find Aria.Bridge.csproj relative to the console.");
}

static async Task EnsureSoulAsync(BridgeConsoleClient bridge)
{
    var profile = await bridge.GetProfileAsync();
    if (profile?.ServerSoulId is not null) return;

    AriaRetroChrome.Warning("This bridge has no linked soul yet.");
    var name = AnsiConsole.Ask<string>($"[bold {AriaRetroChrome.PhosphorRed.ToMarkup()}]Soul name:[/]");
    var serverUrl = AnsiConsole.Ask<string>($"[bold {AriaRetroChrome.PhosphorRed.ToMarkup()}]Aria.Web server URL:[/]", "http://localhost:5129");

    await bridge.CreateSoulAsync(name);
    await bridge.LinkServerAsync(serverUrl);

    // Wait for the tunnel to come up and the first sync to arrive.
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Linking to server...", async _ =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var p = await bridge.GetProfileAsync();
                if (p?.ServerSoulId is not null) return;
                await Task.Delay(500);
            }
        });

    AriaRetroChrome.Success("Bridge linked and ready.");
}

static string[] DeserializeArgs(string? json)
{
    try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? []; }
    catch { return []; }
}

static Dictionary<string, string>? DeserializeEnv(string? json)
{
    try { return string.IsNullOrWhiteSpace(json) ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
    catch { return null; }
}
