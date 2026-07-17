namespace Aria.Web.Data;

/// <summary>
/// A transient IP allow-list entry created when an authenticated bridge "knocks" from a network.
/// The IP address is encrypted at rest with ASP.NET Data Protection.
/// </summary>
public class UiAccessKnock
{
    public int Id { get; set; }

    public string UserId { get; set; } = "";

    public string IpAddressProtected { get; set; } = "";

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
