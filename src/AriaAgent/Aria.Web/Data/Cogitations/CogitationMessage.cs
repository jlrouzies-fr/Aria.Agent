namespace Aria.Web.Data.Cogitations;

public class CogitationMessage
{
    public int      Id               { get; set; }
    public int      CogitationId     { get; set; }
    public Cogitation Cogitation     { get; set; } = null!;
    public string   Role             { get; set; } = "";
    public string   Content          { get; set; } = "";
    public string?  ThinkingContent  { get; set; }
    public string?  SectionsJson     { get; set; }   // serialized MessageSection[] for tool activity / diff cards
    public string?  ImageBase64      { get; set; }   // set only on a "screenshot" message
    public string?  ImageMediaType   { get; set; }
    public DateTime CreatedAt        { get; set; } = DateTime.UtcNow;
}
