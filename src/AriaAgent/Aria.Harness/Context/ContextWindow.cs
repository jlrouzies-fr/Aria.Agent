namespace Aria.Harness.Context;

/// <summary>
/// A discovered or configured context-window size for a specific source+model.
/// <see cref="Assumed"/> is true when the value is the fallback default (100k) rather than
/// an authoritative override or provider-reported discovery.
/// </summary>
public sealed record ContextWindow(int Tokens, bool Assumed)
{
    /// <summary>The fallback window used when nothing more specific is known.</summary>
    public static ContextWindow AssumedDefault { get; } = new(100_000, true);
}
