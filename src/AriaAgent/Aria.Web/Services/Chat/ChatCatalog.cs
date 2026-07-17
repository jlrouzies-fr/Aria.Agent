namespace Aria.Web.Services.Chat;

/// <summary>Whether a chat command / reference is wired up today, or documented but not yet built.</summary>
public enum CatalogStatus { Available, Planned }

/// <summary>A "/" command surfaced in the chat palette and the left-menu INDEX.</summary>
public record CatalogCommand(string Name, string ArgHint, string Description, string Group, CatalogStatus Status);

/// <summary>A "#" context reference surfaced in the left-menu INDEX.</summary>
public record CatalogReference(string Token, string Description, string Backing, string Group, CatalogStatus Status);

/// <summary>
/// Single source of truth for the chat input's "/" commands and "#" references. The inline command
/// palette (<c>Chat.FilePicker</c>) shows the <see cref="CatalogStatus.Available"/> commands; the
/// left-menu INDEX panel (<c>NavMenuReferencePanel</c>) lists the full catalog with status badges.
/// See <c>docs/commands-and-references-plan.md</c> for the design and rollout phases.
/// </summary>
public static class ChatCatalog
{
    public static readonly IReadOnlyList<CatalogCommand> Commands =
    [
        // ── Session / context ────────────────────────────────────────────────
        new("/clear",   "",          "Start a fresh cogitation — clears the current conversation.",         "Session",  CatalogStatus.Available),
        new("/compact", "",          "Summarise history to reclaim context window.",                        "Session",  CatalogStatus.Available),
        new("/resume",  "",          "Reload a previous cogitation.",                                       "Session",  CatalogStatus.Planned),
        new("/rewind",  "",          "Checkpoint-restore the conversation to an earlier turn.",             "Session",  CatalogStatus.Planned),
        new("/export",  "",          "Dump the transcript to markdown.",                                    "Session",  CatalogStatus.Planned),
        new("/cost",    "",          "Show token + spend for the session.",                                 "Session",  CatalogStatus.Planned),

        // ── Memory / project ─────────────────────────────────────────────────
        new("/project", "",          "Choose which project the # file picker searches.",                    "Project",  CatalogStatus.Available),
        new("/remember","<text>",    "Write a Noosphere memory.",                                           "Project",  CatalogStatus.Planned),
        new("/init",    "",          "Scan the project and generate a CLAUDE.md-style brief.",              "Project",  CatalogStatus.Planned),

        // ── Capabilities (open the matching left-menu panel) ──────────────────
        new("/tools",   "",          "Open the Tools panel — toggle agent tools.",                          "Capability", CatalogStatus.Available),
        new("/mcp",     "",          "Open the Tools panel — connect / list MCP servers.",                  "Capability", CatalogStatus.Available),
        new("/agents",  "",          "Open the Agents panel — sub-agent personas.",                         "Capability", CatalogStatus.Available),
        new("/skills",  "",          "Open the Skills panel — reusable skill snippets.",                    "Capability", CatalogStatus.Available),
        new("/soul",    "",          "Open the Souls panel — link / unlink / verify.",                      "Capability", CatalogStatus.Available),
        new("/devices", "",          "Open the Devices panel — enrolled cogitator nodes.",                  "Capability", CatalogStatus.Available),
        new("/model",   "<source>",  "Switch the active model source.",                                     "Capability", CatalogStatus.Planned),

        // ── Dev workflow ─────────────────────────────────────────────────────
        new("/review",  "",          "Review the working diff.",                                            "Dev",      CatalogStatus.Planned),
        new("/commit",  "",          "Draft a commit message from the diff.",                               "Dev",      CatalogStatus.Planned),
        new("/diff",    "",          "Show working changes inline.",                                        "Dev",      CatalogStatus.Planned),
        new("/test",    "",          "Run tests and feed results back.",                                    "Dev",      CatalogStatus.Planned),

        // ── Aria-native ──────────────────────────────────────────────────────
        new("/hive",    "",          "Open the Hive panel — agent collectives. (Armed-input flow planned.)","Aria",     CatalogStatus.Available),
        new("/vigil",   "",          "Open the Vigil scheduler — cron-driven directives.",                  "Aria",     CatalogStatus.Available),
        new("/vox",     "",          "Toggle voice input.",                                                 "Aria",     CatalogStatus.Available),
        new("/wargame", "",          "Open WAR.COGITATOR — the pixel-art wargame.",                         "Aria",     CatalogStatus.Available),
        new("/exchange","",          "Start a soul-to-soul exchange session.",                              "Aria",     CatalogStatus.Planned),
        new("/help",    "",          "Open this command & reference index.",                                "Aria",     CatalogStatus.Available),
        new("/index",   "",          "Open this command & reference index.",                                "Aria",     CatalogStatus.Available),
    ];

    public static readonly IReadOnlyList<CatalogReference> References =
    [
        // ── Files ─────────────────────────────────────────────────────────────
        new("#<path>",            "Reference a project file by path; the agent reads it with its file tools.", "ProjectFileEndpoints", "Files",   CatalogStatus.Available),
        new("#folder:<dir>",      "Inject a directory tree (files + subdirectories).",                         "/project-files/tree",  "Files",   CatalogStatus.Available),

        // ── Git ───────────────────────────────────────────────────────────────
        new("#git:diff",          "Inject the current working diff.",                                          "GitEndpoints",         "Git",     CatalogStatus.Available),
        new("#git:status",        "Inject staged / unstaged file status.",                                     "GitEndpoints",         "Git",     CatalogStatus.Available),
        new("#git:log",           "Inject the recent commit log.",                                             "GitEndpoints",         "Git",     CatalogStatus.Available),
        new("#git:@<sha> · #pr:N","Inject a specific commit or pull-request diff.",                            "git / gh",             "Git",     CatalogStatus.Planned),

        // ── Knowledge ─────────────────────────────────────────────────────────
        new("#url:<https…>",      "Fetch and inject a cleaned web page.",                                      "web-fetch tooling",    "Knowledge", CatalogStatus.Planned),
        new("#mem:<query>",       "Inject a matching Noosphere memory.",                                       "Noosphere",            "Knowledge", CatalogStatus.Planned),

        // ── Code intelligence ─────────────────────────────────────────────────
        new("#sym:<Name>",        "Inject a symbol's definition body.",                                        "LSP / ctags index",    "Code",    CatalogStatus.Planned),
        new("#diag · #problems",  "Inject compiler / LSP diagnostics.",                                        "build parse / LSP",    "Code",    CatalogStatus.Planned),

        // ── Live context ──────────────────────────────────────────────────────
        new("#mcp:<server>/<id>", "Inject an MCP server resource.",                                            "McpTools / SessionStore","Live",  CatalogStatus.Planned),
        new("#term · #out",       "Inject the last terminal / tool output.",                                   "session buffer",       "Live",    CatalogStatus.Planned),
        new("#agent:<name>",      "Inject a sub-agent persona.",                                               "SubAgent table",       "Live",    CatalogStatus.Planned),
        new("#skill:<name>",      "Inject a skill snippet.",                                                   "Skill table",          "Live",    CatalogStatus.Planned),
    ];

    /// <summary>
    /// Renders the currently-<see cref="CatalogStatus.Available"/> commands and references as a
    /// plain-text index for the <c>list_chat_capabilities</c> agent tool (see
    /// <c>Aria.Tools.ChatCapabilitiesTools</c>). Terse tool-call output, not UI copy — self-updates
    /// as entries flip from <see cref="CatalogStatus.Planned"/> to <see cref="CatalogStatus.Available"/>.
    /// </summary>
    public static string BuildAgentCapabilitiesText()
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("Chat UI \"/\" commands (typed at the start of the input box):");
        foreach (var group in Commands.Where(c => c.Status == CatalogStatus.Available).GroupBy(c => c.Group))
        {
            foreach (var c in group)
            {
                var hint = string.IsNullOrEmpty(c.ArgHint) ? "" : $" {c.ArgHint}";
                sb.AppendLine($"  {c.Name}{hint} — {c.Description}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Chat UI \"#\" context references (typed inline in a message; the referenced content is injected before your reply):");
        foreach (var r in References.Where(r => r.Status == CatalogStatus.Available))
            sb.AppendLine($"  {r.Token} — {r.Description}");

        return sb.ToString().TrimEnd();
    }
}
