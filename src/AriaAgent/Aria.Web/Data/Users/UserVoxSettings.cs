namespace Aria.Web.Data.Users;

public class UserVoxSettings
{
    public int     Id                       { get; set; }
    public string  UserId                   { get; set; } = "";
    public string? TranscriptionChannelName { get; set; }
    public string? FixingChannelName        { get; set; }
}
