namespace Aria.Harness.Tools;

/// <summary>
/// Host-neutral description of an enabled tool and its configuration.
/// </summary>
public sealed record ActiveToolConfig(string ToolId, Dictionary<string, string> Config)
{
    public ActiveToolConfig(string toolId) : this(toolId, new Dictionary<string, string>()) { }
}
