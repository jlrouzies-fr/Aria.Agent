namespace Aria.Web.Data.Llm;

public class ModelFormatCache
{
    public int      Id              { get; set; }
    public string   EndpointUrl     { get; set; } = "";
    public string   ModelId         { get; set; } = "";
    public string   ThinkingFormat  { get; set; } = "Unknown";
    public string   ToolCallFormat  { get; set; } = "Unknown";
    public string   VisionSupport   { get; set; } = "Unknown";
    public DateTime DetectedAt      { get; set; }

    /// <summary>
    /// True when a human explicitly accepted this detection via the "couldn't detect format" modal
    /// (e.g. confirmed a channel has no thinking / native tools). A confirmed row is authoritative and
    /// is never re-probed automatically — even when it stores <c>None</c> — so an ambiguous probe never
    /// silently keeps guessing. Cleared only by the "Re-detect format" action.
    /// </summary>
    public bool     Confirmed       { get; set; }

    /// <summary>
    /// Provider-discovered or user-overridden context-window size for this source+model, in tokens.
    /// Null when unknown; today's default (100k) is stored with <see cref="ContextWindowAssumed"/> true.
    /// </summary>
    public int?     ContextWindowTokens { get; set; }

    /// <summary>
    /// True when <see cref="ContextWindowTokens"/> is the fallback assumption (100k), not an
    /// authoritative value. Assumed windows must not change today's behaviour.
    /// </summary>
    public bool     ContextWindowAssumed { get; set; } = true;
}
