namespace Aria.Web.Data.Collectives;

public enum EdgeNodeType
{
    Transform = 0,  // rewrites the instruction via a template ({{original}})
    Condition = 1   // gates the drone: skip it unless the test passes.
                    // Config: {"mode":"contains"|"llm","value":"...","negate":false}
}

public class MemberEdgeNode
{
    public int           Id       { get; set; }
    public int           MemberId { get; set; }
    public CollectiveMember Member { get; set; } = null!;
    public int           Position { get; set; }  // lower = closer to overmind; Gate renders at 500
    public EdgeNodeType  NodeType { get; set; }
    public string?       Config   { get; set; }  // JSON: {"template":"...{{original}}..."}
    public DateTime      CreatedAt { get; set; } = DateTime.UtcNow;
}
