using Microsoft.Extensions.Configuration;

namespace Aria.Agent;

public class FoundryLocalConfig
{
    public bool Enabled { get; set; }
    public string? ModelName { get; set; }
    public bool QwenToolCallHandling { get; set; }
}
