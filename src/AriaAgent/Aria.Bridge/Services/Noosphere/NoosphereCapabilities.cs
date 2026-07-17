namespace Aria.Bridge.Services.Noosphere;

// Runtime-detected capabilities of the local vault, set once at startup.
public static class NoosphereCapabilities
{
    // False if the bundled SQLite build lacks FTS5 (unexpected, but guarded) — probe then
    // falls back to a plain LIKE scan for the keyword leg instead of bm25() ranking.
    public static bool FtsAvailable { get; set; } = true;
}
