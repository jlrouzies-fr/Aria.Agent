using System.Text;
using System.Text.Json;
using Aria.Agent;
using Aria.Harness.Tools;
using Aria.Shared;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace Aria.Console;

public class ConsoleHelper
{
    private static readonly Color BloodRed = new(0xCC, 0x3D, 0x00);

    // Reads a line of input with bracketed paste mode support so that multi-line
    // pastes arrive as a single submission rather than one submission per line.
    public static string? ReadInput()
    {
        System.Console.Write("\x1b[?2004h");

        var input = new StringBuilder();
        var escBuf = new StringBuilder();
        bool inPaste = false;
        bool readingEsc = false;

        try
        {
            while (true)
            {
                var keyInfo = System.Console.ReadKey(intercept: true);
                char ch = keyInfo.KeyChar;

                if (readingEsc)
                {
                    escBuf.Append(ch);
                    string seq = escBuf.ToString();

                    if (seq == "[200~") { inPaste = true; readingEsc = false; escBuf.Clear(); continue; }
                    if (seq == "[201~") { inPaste = false; readingEsc = false; escBuf.Clear(); continue; }

                    if (seq.Length >= 5)
                    {
                        input.Append('\x1b').Append(seq);
                        AnsiConsole.Write(new Text($"\x1b{seq}", new Style(AriaRetroChrome.BodyText)));
                        readingEsc = false;
                        escBuf.Clear();
                    }
                    continue;
                }

                if (keyInfo.Key == ConsoleKey.Escape)
                {
                    readingEsc = true;
                    escBuf.Clear();
                    continue;
                }

                if (keyInfo.Key == ConsoleKey.Enter || ch == '\r' || ch == '\n')
                {
                    if (inPaste)
                    {
                        input.Append('\n');
                        AnsiConsole.Write(new Text("\n", new Style(AriaRetroChrome.BodyText)));
                    }
                    else
                    {
                        AnsiConsole.WriteLine();
                        return input.ToString();
                    }
                    continue;
                }

                if (keyInfo.Key == ConsoleKey.Backspace)
                {
                    if (input.Length > 0)
                    {
                        input.Remove(input.Length - 1, 1);
                        AnsiConsole.Write(new Text("\b \b", new Style(AriaRetroChrome.BodyText)));
                    }
                    continue;
                }

                if (ch != '\0')
                {
                    input.Append(ch);
                    AnsiConsole.Write(new Text(ch.ToString(), new Style(AriaRetroChrome.BodyText)));
                }
            }
        }
        finally
        {
            System.Console.Write("\x1b[?2004l");
        }
    }

    public static void ShowHeader()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(AriaRetroChrome.Header());
        AnsiConsole.WriteLine();
    }

    public static string SelectFromOptions(List<string> options, string title)
    {
        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .AddChoices(options));

        AnsiConsole.MarkupLine($"Selected: [{AriaRetroChrome.Amber.ToMarkup()}]{selection}[/]");
        return selection;
    }

    public static SyncedSubAgentDto? SelectAgent(List<SyncedSubAgentDto> agents)
    {
        if (agents.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim {AriaRetroChrome.MutedText.ToMarkup()}]No synced agents — using the default Aria persona.[/]\n");
            return null;
        }

        var choices = new List<string> { "Default Aria persona" };
        choices.AddRange(agents.Select(a => $"{a.DisplayName} ({a.ArchetypeName})"));

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"Select an [{AriaRetroChrome.PhosphorRed.ToMarkup()}]agent[/] to embody:")
                .AddChoices(choices));

        if (selected == "Default Aria persona") return null;
        var agent = agents.First(a => $"{a.DisplayName} ({a.ArchetypeName})" == selected);
        AnsiConsole.MarkupLine($"Embodying: [{AriaRetroChrome.Amber.ToMarkup()}]{agent.DisplayName}[/]\n");
        return agent;
    }

    public static (string SourceName, string Model) SelectSourceAndModel(
        List<SyncedLocalSourceDto> localSources)
    {
        var allSources = new List<(string Name, List<string> Models)>();

        foreach (var src in localSources)
        {
            var models = DeserializeModels(src.ModelsJson);
            if (models.Count > 0) allSources.Add((src.Name, models));
        }

        foreach (var provider in PublicModelSourceCatalog.Providers)
            allSources.Add((provider.Name, provider.Models.ToList()));

        if (allSources.Count == 0)
            throw new InvalidOperationException("No model sources available. Add one in Aria.Web.");

        var sourceName = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"Select a [{AriaRetroChrome.PhosphorRed.ToMarkup()}]model source[/]:")
                .AddChoices(allSources.Select(s => s.Name)));

        var source = allSources.First(s => s.Name == sourceName);
        var model = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"Select a [{AriaRetroChrome.PhosphorRed.ToMarkup()}]model[/] for {sourceName}:")
                .AddChoices(source.Models));

        AnsiConsole.MarkupLine($"Channel: [{AriaRetroChrome.Amber.ToMarkup()}]{sourceName}[/] / [{AriaRetroChrome.Amber.ToMarkup()}]{model}[/]\n");
        return (sourceName, model);
    }

    public static List<ActiveToolConfig> SelectTools(
        SyncedSubAgentDto? agent,
        Dictionary<string, SyncedToolConfigDto> toolConfigs,
        bool graphAvailable,
        bool googleAvailable,
        bool memoryAvailable,
        List<SyncedMcpServerDto> mcpServers,
        IConfiguration config)
    {
        var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (agent != null)
            foreach (var ts in agent.ToolStates.Where(t => t.Enabled))
                defaults.Add(ts.ToolId);

        var choices = new List<string>
        {
            "websearch",
            "webfetch",
            "datetime",
            "terminal",
            "wargame",
            "mcp",
        };

        if (graphAvailable)
        {
            choices.Add("graph_email");
            choices.Add("graph_calendar");
        }
        if (googleAvailable)
        {
            choices.Add("google_email");
            choices.Add("google_calendar");
        }
        if (memoryAvailable)
            choices.Add("memory");

        if (choices.Count == 0) return [];

        var prompt = new MultiSelectionPrompt<string>()
            .Title($"Toggle [{AriaRetroChrome.PhosphorRed.ToMarkup()}]tools[/] for this session (space to select, enter to confirm):")
            .AddChoices(choices)
            .UseConverter(id => $"{id} {ToolEmoji(id)}");

        foreach (var d in defaults)
            prompt.Select(d);

        var selected = AnsiConsole.Prompt(prompt);

        var result = new List<ActiveToolConfig>();
        foreach (var toolId in selected)
        {
            var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (toolConfigs.TryGetValue(toolId, out var synced) && !string.IsNullOrWhiteSpace(synced.ConfigJson))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(synced.ConfigJson) ?? [];
                    foreach (var kv in parsed) cfg[kv.Key] = kv.Value;
                }
                catch { /* malformed config */ }
            }

            result.Add(new ActiveToolConfig(toolId, cfg));
        }

        if (selected.Contains("mcp") && mcpServers.Count > 0)
            AnsiConsole.MarkupLine($"[dim {AriaRetroChrome.MutedText.ToMarkup()}]MCP servers enabled for this agent will be loaded automatically.[/]");

        return result;
    }

    public static string GetString(string prompt)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>(prompt)
                .PromptStyle("white")
                .ValidationErrorMessage($"[{AriaRetroChrome.BloodRed.ToMarkup()}]Invalid prompt[/]")
                .Validate(value =>
                {
                    if (value.Length < 3)
                        return ValidationResult.Error($"[{AriaRetroChrome.BloodRed.ToMarkup()}]Value too short[/]");
                    if (value.Length > 200)
                        return ValidationResult.Error($"[{AriaRetroChrome.BloodRed.ToMarkup()}]Value too long[/]");
                    return ValidationResult.Success();
                }));
    }

    private static List<string> DeserializeModels(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static string ToolEmoji(string toolId) => toolId switch
    {
        "websearch" => "🔍",
        "webfetch" => "🌐",
        "datetime" => "🕒",
        "terminal" => "⌨",
        "wargame" => "⚔️",
        "mcp" => "🔌",
        "graph_email" => "📧",
        "graph_calendar" => "📅",
        "google_email" => "📧",
        "google_calendar" => "📅",
        "memory" => "🧠",
        _ => ""
    };
}
