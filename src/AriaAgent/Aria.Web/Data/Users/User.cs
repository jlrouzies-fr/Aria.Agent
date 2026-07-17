namespace Aria.Web.Data.Users;

public class User
{
    public string   Id              { get; set; } = Guid.NewGuid().ToString();
    public string   Name            { get; set; } = "";
    public string?  Email           { get; set; }
    public string?  LastModelSource  { get; set; }
    public string?  AvatarSpriteKey  { get; set; }
    public DateTime CreatedAt        { get; set; } = DateTime.UtcNow;
    public string?  Timezone         { get; set; }
    public string?  PublicKey        { get; set; }
    public bool     KeepTelemetryExpanded { get; set; }
}
