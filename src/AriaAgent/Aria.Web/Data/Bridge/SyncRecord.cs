namespace Aria.Web.Data.Bridge;

/// <summary>
/// One opaque, end-to-end-encrypted replica of a soul-private record (feature plan §11.3). The server
/// is a dumb encrypted relay: it stores and fans out <see cref="CipherBlob"/> but never reads it — the
/// blob is AES-GCM ciphertext under the soul's DEK, which the server never holds.
///
/// Multi-master last-write-wins: on conflict the row with the greater <c>(UpdatedAt, LastWriterNodeId)</c>
/// wins. Deletions are tombstones (<see cref="Deleted"/>), never hard removals, so they propagate.
/// </summary>
public class SyncRecord
{
    public int      Id              { get; set; }
    public string   UserId          { get; set; } = "";
    public string   EntityType      { get; set; } = "";   // e.g. "SubAgent", "Skill", "AgentCollective"
    public string   EntityId        { get; set; } = "";   // the record's stable id within its type
    public DateTime UpdatedAt       { get; set; }          // UTC; LWW clock
    public bool     Deleted         { get; set; }          // tombstone
    public string   LastWriterNodeId { get; set; } = "";   // LWW tiebreaker
    public string   CipherBlob      { get; set; } = "";    // opaque AES-GCM(base64) — server never decrypts
}
