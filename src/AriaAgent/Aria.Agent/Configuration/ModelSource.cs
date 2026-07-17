using System.IO;

namespace Aria.Agent;

public class ModelSource
{
    public string Name { get; set; } = "";
    public string? ApiKeyFile { get; set; }
    public string Url { get; set; } = "";
    public List<string> Models { get; set; } = [];
    public bool IsBridged        { get; set; } = false;
    public bool IsPublicProvider { get; set; } = false;
    // Optional: pin this local channel to a specific bridge node (remote-nodes plan §5). null = default node.
    public string? BridgeNodeId  { get; set; }
    // The node-side channel name used as the bridge keyRef. Null when it equals Name; set only when the
    // display Name was disambiguated because the same channel name exists on more than one node — the
    // keyRef must stay the real channel name the node stored, not the "Name · Label" display string.
    public string? ChannelName  { get; set; }

    public string GetApiKey()
    {
        if (string.IsNullOrEmpty(ApiKeyFile)) return string.Empty;
        var path = Path.IsPathRooted(ApiKeyFile)
            ? ApiKeyFile
            : Path.Combine(Directory.GetCurrentDirectory(), ApiKeyFile);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
    }
}
