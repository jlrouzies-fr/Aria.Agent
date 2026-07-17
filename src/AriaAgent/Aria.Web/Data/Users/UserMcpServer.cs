using Aria.Tools;

namespace Aria.Web.Data.Users;

public class UserMcpServer
{
    public int          Id        { get; set; }
    public string       UserId    { get; set; } = "";
    public User         User      { get; set; } = null!;
    public string       Name      { get; set; } = "";
    public McpTransport Transport { get; set; } = McpTransport.Stdio;
    // Stdio / LocalBridge fields
    public string  Command  { get; set; } = "";
    public string  ArgsJson { get; set; } = "[]";  // JSON string[]
    public string? EnvJson  { get; set; }           // JSON Dictionary<string,string>
    // SSE field
    public string? Url      { get; set; }
    public bool    Enabled  { get; set; } = true;
}
