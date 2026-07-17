using System.ComponentModel;

namespace Aria.Tools;

public static class WargameTools
{
    private static Func<string>? _reportProvider;

    /// <summary>
    /// Called once at startup (Program.cs) with a lambda that reads WargameService.
    /// Avoids a circular DI dependency (WargameService already depends on AgentService).
    /// </summary>
    public static void Configure(Func<string> reportProvider) =>
        _reportProvider = reportProvider;

    [Description(
        "Get a full strategic situation report for the WAR.PLAN wargame simulation. " +
        "Returns current turn, all factions (race, alive/dead, unit count, tile count, buildings, resources), " +
        "and the last several battle log entries. Use this when the user asks about the war, " +
        "which faction is winning, what resources they have, or what happened last turn." +
        "Reply with Emojis representing what you get.")]
    public static string GetWarSituationReport() =>
        _reportProvider?.Invoke() ?? "// WAR.PLAN — No battle active. Generate a map from the /wargame page first.";
}
