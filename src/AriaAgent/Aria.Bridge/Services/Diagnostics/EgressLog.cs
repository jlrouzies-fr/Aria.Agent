namespace Aria.Bridge.Services.Diagnostics;

/// <summary>
/// In-memory ring buffer of the bridge's recent outbound LLM calls (url, status, response head).
/// Exposed at GET /debug/llm-log so cross-machine issues ("my chat shows nothing") can be diagnosed
/// remotely over the tunnel without shell access to the machine.
/// </summary>
public static class EgressLog
{
    private const int Capacity = 25;
    private static readonly object _lock = new();
    private static readonly Queue<Entry> _entries = new();

    public sealed record Entry(
        DateTime AtUtc, string Url, bool HadAuthHeader, int Status, string? ContentType, string? BodyHead);

    public static void Add(string url, bool hadAuthHeader, int status, string? contentType, string? bodyHead)
    {
        lock (_lock)
        {
            _entries.Enqueue(new Entry(DateTime.UtcNow, url, hadAuthHeader, status, contentType, bodyHead));
            while (_entries.Count > Capacity) _entries.Dequeue();
        }
    }

    public static Entry[] List()
    {
        lock (_lock) return _entries.Reverse().ToArray();
    }
}
