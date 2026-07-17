namespace Aria.Harness.Formats;

public enum ThinkingFormat
{
    Unknown,           // not yet detected
    None,              // model emits no thinking tokens
    ReasoningContent,  // {"reasoning_content": "..."} in the delta (OpenAI/DeepSeek style)
    ThinkTags,         // <think>…</think> wrapping inside content
    StartsInThinkMode, // </think> only — model starts thinking immediately with no opening tag
    ChannelThought,    // <|channel>thought…<channel|> (Gemma 12b via LM Studio)
    Harmony            // <|channel|>analysis/commentary/final (OpenAI GPT-OSS via LM Studio)
}
