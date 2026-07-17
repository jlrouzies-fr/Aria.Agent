# Bug — Agent thinking content rendered as normal message text

## Summary
During a live assistant response, the model's internal reasoning/thinking text that should appear inside the collapsible "INTERNAL COGITATION" block is instead rendered inline as normal message content. This happens intermittently — the thinking block may initially render correctly, then later thinking tokens appear as regular content.

## Observed behavior (excerpt)
```
> ARIA
The user wants to know the current date and time, so I'll use the GetCurrentDateTime tool directly.

The current date and time is June 30, 2026 at 7:39 AM. I'll present this clearly to the user, using a Warhammer-appropriate tone as specified in my context.
The current date is June 30, 2026 and the time is 7:39 AM.
```

The prefix sentences ("The user wants...", "I'll present this clearly...") are reasoning text and should be inside the thinking block. They were rendered as normal content.

## Context
- Occurred during normal chat streaming (tool call followed by content).
- Also coincided with a `// TRANSMISSION INTERRUPTED //` marker and a repeated greeting in the same session.
- At the time of the report, Phase 1 bridge-owned chats were active, but the issue is in the live streaming path, not persistence/reload.

## Suspected area
`Chat.Messaging.razor.cs` / `OnThinkingToken` in `Chat.razor.cs`. Thinking tokens are delivered out-of-band from a thread-pool thread. `_thinkingTarget` is meant to catch late tokens after `_streamingMsg` is nulled, but if the target message is interrupted or the dispatcher posts arrive out of order, thinking text may be appended to the wrong section.

## Reproduction
Not consistently reproduced. Steps attempted:
1. Start a new cogitation.
2. Ask a question that triggers tool use + reasoning (e.g. "get the date time").
3. Observe whether reasoning text appears inside the thinking block or as normal content.

## Expected
All reasoning/thinking text from the model should be inside the collapsible thinking block.

## Actual
Reasoning text occasionally leaks into the normal message body.

## Related code
- `src/AriaAgent/Aria.Web/Components/Pages/Chat.razor.cs` — `_thinkingTarget`, `OnThinkingToken`.
- `src/AriaAgent/Aria.Web/Components/Pages/Chat.Messaging.razor.cs` — streaming loop and `finally` block.
- `src/AriaAgent/Aria.Web/Models/MessageEntry.cs` — `ThinkingContent` setter / section parsing.

## Notes
- This may be unrelated to the bridge-data-owner (Phase 1) work, which only changes where messages are persisted, not how streaming tokens are classified.
- If reproduction becomes reliable, add logging to `OnThinkingToken` to capture `_thinkingTarget`, `_streamingMsg`, and current message section types at the time each token arrives.
