namespace Aria.Web.Data.Users;

public class UserToolConfig
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public User User { get; set; } = null!;
    public string ToolId { get; set; } = "";
    public bool Enabled { get; set; }
    public string? ConfigJson { get; set; }
}
