using Aria.Harness.Tools;

namespace Aria.Web.Services.Tool;

public record ToolConfigField(
    string Key,
    string Label,
    string InputType,   // text | url | path | password
    string? Placeholder = null,
    bool    Required    = false
);

public record SetupStep(string Html, string? Code = null);

public record ToolDefinition(
    string Id,
    string Label,
    string Icon,
    string Description,
    string Category,
    IReadOnlyList<ToolConfigField> ConfigFields,
    IReadOnlyList<SetupStep>       SetupGuide
)
{
    public bool HasConfig => ConfigFields.Count > 0;
}

public static class ToolRegistry
{
    public static readonly IReadOnlyList<ToolDefinition> All = new ToolDefinition[]
    {
        new("websearch", "Web Search", "🔍",
            "Enables web search via Ollama's web search API.",
            "Built-in", [],
            [
                new("Go to <a href=\"https://ollama.com\" target=\"_blank\">ollama.com</a> and create an account."),
                new("Navigate to your account → <strong>API Keys</strong> and generate a new key."),
                new("Save the key to a file on the machine running the Aria Bridge — never put raw keys in the config:", "echo \"your-api-key\" > ~/.config/aria-agent/ollama.key"),
                new("Add the path to that file in your local bridge appsettings under <code>OllamaWebSearch:ApiKeyFile</code>."),
                new("The bridge connects to <code>https://ollama.com</code> by default."),
            ]),

        new("graph_email", "Microsoft Email", "📧",
            "Read Microsoft 365 / Outlook emails. Connect your Microsoft account via the ⚙ button.",
            "Integrations", [], AzureSetupSteps()),

        new("graph_calendar", "Microsoft Calendar", "📅",
            "Read Microsoft 365 / Outlook calendar events. Connect your Microsoft account via the ⚙ button.",
            "Integrations", [], AzureSetupSteps()),

        new("google_email", "Gmail", "📧",
            "Read Gmail emails. Connect your Google account via the ⚙ button.",
            "Integrations", [], GoogleSetupSteps()),

        new("google_calendar", "Google Calendar", "📅",
            "Read Google Calendar events. Connect your Google account via the ⚙ button.",
            "Integrations", [], GoogleSetupSteps()),

        new("memory", "Memory (Noosphere)", "🧠",
            "Persistent memory native to the Aria Bridge — inscribed facts, entities, and relations, probed and contemplated upon by the agent. No external server; runs on your own machine.",
            "Built-in", [], NoosphereSetupSteps()),

        new("terminal", "Terminal / Projects", "⌨",
            "Built-in shell and file tools: run commands, read/write/edit files, search the filesystem. Requires Aria MCP Bridge.",
            "Built-in",
            [],
            [
                new("Grants the agent direct shell access and file manipulation on <strong>your local machine</strong> via the Aria MCP Bridge app."),
                new("<strong>Enable Terminal on the bridge first</strong>: open <code>http://localhost:5741</code> on the machine running the bridge, go to the Telemetry tab, and turn on <strong>Terminal Capability</strong>. Enabling this tool in the web UI alone is not enough."),
                new("PTY mode additionally requires a time-limited Inquisitorial Seal grant on the bridge."),
                new("The agent can run any shell command, read/write/edit files, and search the filesystem."),
                new("<strong>Allowed Paths</strong>: declared on the bridge. They restrict all file and directory access to listed paths. Leave empty on the bridge to block all filesystem access."),
                new("<strong>Blocked Commands</strong>: extra patterns declared on the bridge (in addition to built-in dangerous defaults: rm -rf /, format, mkfs, fork bombs, etc.)."),
                new("The agent has access to <code>commands_index(topic)</code> to recall build commands for Python, Rust, Go, Node, .NET, Java, Docker, Git, and more."),
            ]),

        new("webfetch", "Web Page Fetch", "🌐",
            "Fetches and reads the text content of any web page from a URL.",
            "Built-in", [], [
                new("No configuration required — enable and start a new cogitation."),
            ]),

        new("screenshot", "Screenshot", "📸",
            "Captures a screenshot of a page running on your own machine (localhost) via a headless browser on the Aria Bridge. When the active model supports vision, it's shown the image directly for visual verification; otherwise you get a text description only.",
            "Built-in", [], [
                new("No configuration required — enable and start a new cogitation. Requires the Aria Bridge."),
                new("Only localhost/127.0.0.1 URLs can be captured — e.g. your own dev server."),
                new("The headless Chromium browser is downloaded automatically on first use (one-time, a few hundred MB)."),
                new("Whether the agent can actually <em>see</em> the screenshot depends on the active model's vision support, which is auto-detected the first time you use this tool on a given channel."),
            ]),

        new("http_request", "HTTP Request", "🌐",
            "Performs HTTP requests from your own machine via the Aria Bridge and returns the raw response (status, headers, body). Useful for API testing against localhost or LAN services the hosted server cannot reach. Redirects are reported, not followed.",
            "Built-in", [], [
                new("No configuration required — enable and start a new cogitation. Requires the Aria Bridge."),
                new("Requests originate from the bridge machine, so they can reach your LAN/localhost services — and carry data out. Classified as a sensitive operation under Layer B (node context grant)."),
            ]),

        new("read_image", "Image Read", "🖼",
            "Reads a local image file (png/jpeg/gif/webp, up to 10 MB) from your machine via the Aria Bridge. When the active model supports vision, the image is shown to it directly; otherwise the model only learns the path, format, and size.",
            "Built-in", [], [
                new("No configuration required — enable and start a new cogitation. Requires the Aria Bridge."),
                new("Same trust level as <code>read_file</code>: restricted to the bridge's declared Allowed Paths."),
                new("Whether the agent can actually <em>see</em> the image depends on the active model's vision support, which is auto-detected."),
            ]),

        new("wargame", "WAR.PLANNER", "⚔️",
            "Gives the agent access to a live strategic situation report of the WAR.PLANNER simulation: faction status, unit positions, resources, buildings, and recent battle history.",
            "Built-in", [], [
                new("No configuration required. Start a battle on the <strong>/wargame</strong> page, then enable this tool and ask the agent about the war."),
            ]),

        new("mcp", "MCP Servers", "🔌",
            "Connect to any Model Context Protocol server to expose additional tools dynamically.",
            "MCP", [],
            [
                new("MCP servers are managed below — add any server that exposes tools via stdio transport."),
                new("Each server is launched as a subprocess. <code>Command</code> is the executable, <code>Arguments</code> are passed as-is."),
                new("Servers that fail to connect at startup are skipped with a warning — they do not prevent the agent from starting."),
                new("Example — a dotnet MCP project:", "dotnet run --project /path/to/MyMcpServer.csproj"),
            ]),
    };

    public static ToolDefinition? Get(string id) =>
        All.FirstOrDefault(t => t.Id == id);

    public static IEnumerable<IGrouping<string, ToolDefinition>> ByCategory() =>
        All.GroupBy(t => t.Category);

    // ── Shared step lists ────────────────────────────────────────────────────

    private static IReadOnlyList<SetupStep> AzureSetupSteps() =>
    [
        new("Go to <a href=\"https://portal.azure.com\" target=\"_blank\">portal.azure.com</a> → <strong>Microsoft Entra ID</strong> → <strong>App registrations</strong> → <strong>New registration</strong>."),
        new("Give it a name (e.g. <code>aria-agent</code>). Under <em>Supported account types</em> choose <em>Personal Microsoft accounts only</em> for a personal Outlook/Hotmail account."),
        new("Under <em>Redirect URI</em>, select <strong>Public client / native</strong> and enter <code>http://localhost</code>. Click <strong>Register</strong>."),
        new("On the overview page copy the <strong>Application (client) ID</strong> and <strong>Directory (tenant) ID</strong>. Use <code>consumers</code> as Tenant ID for personal accounts."),
        new("Go to <strong>API permissions</strong> → <em>Add a permission</em> → <em>Microsoft Graph</em> → <em>Delegated</em>, and add:", "User.Read\nMail.Read\nCalendars.Read"),
        new("For personal Microsoft accounts: go to <strong>Manifest</strong>, find <code>accessTokenAcceptedVersion</code>, set it to <code>2</code>, and save."),
        new("On the Bridge <strong>OAuth</strong> tab, enter the Tenant ID, Application (client) ID, and client secret under <strong>App Credentials</strong> (encrypted at rest), then authenticate. The server never sees the secret or token."),
    ];

    private static IReadOnlyList<SetupStep> GoogleSetupSteps() =>
    [
        new("Go to <a href=\"https://console.cloud.google.com\" target=\"_blank\">console.cloud.google.com</a> → create a new project (or select an existing one). A personal Gmail account is sufficient — no Google Workspace required."),
        new("Navigate to <strong>APIs &amp; Services</strong> → <strong>Library</strong>. Search for and enable the <strong>Gmail API</strong> and the <strong>Google Calendar API</strong>."),
        new("Go to <strong>APIs &amp; Services</strong> → <strong>OAuth consent screen</strong>. Choose <em>External</em>, fill in the app name and your email, then add your own Google account as a <strong>Test user</strong>."),
        new("Go to <strong>APIs &amp; Services</strong> → <strong>Credentials</strong> → <strong>Create Credentials</strong> → <strong>OAuth client ID</strong>. Select <em>Desktop app</em> as the application type. Click <strong>Create</strong>."),
        new("Click the <strong>Download JSON</strong> button (↓) next to the credential you just created. Save the file to a safe location, e.g.:", "~/.aria-agent/google-credentials.json"),
        new("On the Bridge <strong>OAuth</strong> tab, paste the whole downloaded JSON under <strong>App Credentials</strong> (encrypted at rest), then authenticate. The server never sees the secret or token."),
    ];

    private static IReadOnlyList<SetupStep> NoosphereSetupSteps() =>
    [
        new("<strong>No server needed</strong> — runs inside your Aria Bridge, stores engrams in the local SQLite vault."),
        new("<strong>Extraction &amp; contemplation</strong> borrow your already-bridged local model channel by default."),
        new("<strong>Semantic (vector) probe:</strong> pick an existing channel for embeddings under <strong>Memory</strong> on the bridge."),
        new("<strong>Fallback:</strong> without embeddings, probe uses keyword (BM25) + entity-graph search."),
        new("<strong>Use it:</strong> enable the tool, ask the agent to remember something, then ask about it in a new cogitation."),
    ];
}
