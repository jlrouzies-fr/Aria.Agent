using Aria.Tools;

namespace Aria.Web.Services.Chat;

/// <summary>A single tool invocation made while producing an assistant message.</summary>
public class ToolCallInfo
{
    public string  Name           { get; init; } = "";
    public string  ArgsJson       { get; init; } = "";
    public string? Result         { get; set; }   // null while running
    public bool    Expanded       { get; set; }
    public string? ImageBase64    { get; set; }    // set only for a multimodal result (e.g. TakeScreenshot)
    public string? ImageMediaType { get; set; }
    public string? MetadataJson   { get; set; }    // UI-only structured payload (e.g. file diff card)
}

/// <summary>A section of content or activity within a message.</summary>
public class MessageSection
{
    public enum SectionType
    {
        Content,       // Regular text content
        Thinking,      // Thinking/reasoning content
        ToolActivity,  // Tool call activity (start/complete)
        TodoList       // Task manifest (todo checklist) posted by the agent
    }

    public SectionType Type { get; set; }
    public string Text { get; set; } = "";  // For Content and Thinking types
    public ToolCallInfo? ToolCall { get; set; }  // For ToolActivity type
    public List<TodoItem>? Todos { get; set; }   // For TodoList type

    // Thinking sections collapse once the agent finishes cogitating (content begins or
    // streaming ends). User can re-expand by clicking the header.
    public bool Collapsed { get; set; }
}

public class MessageEntry
{
    public string Role           { get; set; }
    public DateTime Timestamp    { get; init; } = DateTime.Now;

    // Backing IDs used when persisting mutable section state (e.g. diff-card reverted flag).
    // Exactly one of these is set for loaded messages; transient messages have neither.
    public int?    DbMessageId    { get; set; }
    public string? BridgeMessageId { get; set; }

    // Sections in the order they were received (content, thinking, tool calls can be interleaved)
    public List<MessageSection> Sections { get; } = [];

    // Set on assistant messages at creation time so the correct avatar persists
    // even if the active sub-agent changes later.
    public string? SpriteKey    { get; set; }
    public string? AccentColor  { get; set; }
    public string? AgentName    { get; set; }
    public bool    IsSoul       { get; set; }

    // True for the single summary message left behind by "/compact" — rendered with a divider
    // marking where the prior transcript was collapsed.
    public bool    IsCompactSummary { get; set; }

    // Set on a persisted "screenshot" message: the captured image, rendered inline in the transcript.
    // Never sent to the model as bytes — BuildHistoryContext replays only the text summary (Content).
    public string? ImageBase64    { get; set; }
    public string? ImageMediaType { get; set; }

    // Token usage for the turn that produced this (assistant) message — rendered as a footer under
    // the message, ChatGPT/Open WebUI style. Transient: populated from the live stream's usage event,
    // not persisted, so it shows only for messages generated in the current session.
    public int?    InputTokens  { get; set; }
    public int?    OutputTokens { get; set; }
    public double? Tps          { get; set; }

    // Backward-compat accessors used by history loading and DB save paths.
    // During streaming, write directly to Sections for correct ordering.
    public string Content
    {
        get => string.Join("\n", Sections.Where(s => s.Type == MessageSection.SectionType.Content).Select(s => s.Text));
        set
        {
            var existing = Sections.FirstOrDefault(s => s.Type == MessageSection.SectionType.Content);
            if (existing == null)
                Sections.Add(new MessageSection { Type = MessageSection.SectionType.Content, Text = value });
            else
                existing.Text = value;
        }
    }

    public string ThinkingContent
    {
        get => string.Join("\n", Sections.Where(s => s.Type == MessageSection.SectionType.Thinking).Select(s => s.Text));
        set
        {
            var existing = Sections.FirstOrDefault(s => s.Type == MessageSection.SectionType.Thinking);
            if (existing == null)
                Sections.Insert(0, new MessageSection { Type = MessageSection.SectionType.Thinking, Text = value });
            else
                existing.Text = value;
        }
    }

    // Tool calls made while generating this (assistant) message, in call order.
    public List<ToolCallInfo> ToolCalls
    {
        get => Sections.Where(s => s.Type == MessageSection.SectionType.ToolActivity && s.ToolCall != null)
                      .Select(s => s.ToolCall!)
                      .ToList();
        set
        {
            foreach (var tc in value)
            {
                Sections.Add(new MessageSection 
                {
                    Type = MessageSection.SectionType.ToolActivity,
                    ToolCall = tc
                });
            }
        }
    }

    public MessageEntry(string role, string content)
    {
        Role    = role;
        Content = content;
    }
}
