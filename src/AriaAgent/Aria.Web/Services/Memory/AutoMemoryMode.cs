namespace Aria.Web.Services.Memory;

// How aggressively the Noosphere Inscribe tool gets used without an explicit user request.
public enum AutoMemoryMode
{
    Off,        // Only when the user explicitly asks ("remember this") — the model still has the tool.
    ModelAuto,  // The model uses its own judgment on when something is worth inscribing (default).
    Regular,    // The harness auto-inscribes a batch of turns every N exchanges (buffered, no gaps).
    Always,     // The harness auto-inscribes every single turn.
}
