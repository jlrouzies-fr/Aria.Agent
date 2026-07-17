namespace Aria.Web.Data.Users;
public class UserSourcePreference {
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public User User { get; set; } = null!;
    public string SourceName { get; set; } = "";
    public string ModelId { get; set; } = "";
}
