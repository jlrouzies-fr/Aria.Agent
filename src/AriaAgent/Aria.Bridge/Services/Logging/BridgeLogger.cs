using System.Collections.Concurrent;
using System.Reflection;
using Aria.Bridge.Infrastructure;

namespace Aria.Bridge.Services.Logging;

public static class BridgeLogger
{
    public static string Version { get; } =
        typeof(BridgeLogger).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0-unknown";

    public static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    private static readonly ConcurrentQueue<string> logEntries = new();
    private static readonly string logFilePath = Path.Combine(BridgeDataDir.Resolve(), "aria-bridge.log");

    public static IReadOnlyCollection<string> LogEntries => logEntries;
    public static string LogFilePath => logFilePath;

    public static void Log(string level, string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        logEntries.Enqueue(entry);
        while (logEntries.Count > 200) logEntries.TryDequeue(out _);
        try
        {
            var dir = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(logFilePath, entry + Environment.NewLine);
        }
        catch { }
    }
}
