using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aria.Bridge.Services.Metrics;

/// <summary>
/// Runs a long-lived <c>sudo powermetrics</c> child process on macOS to read
/// privileged GPU telemetry. The sudo password is piped once and never stored.
/// </summary>
public sealed class PowermetricsTelemetrySource : IDisposable
{
    private readonly object _lock = new();
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public double? LatestGpuUtilizationPercent { get; private set; }
    public double? LatestGpuPowerMw { get; private set; }

    /// <summary>
    /// Starts a root <c>powermetrics</c> process with the given sudo password.
    /// Does nothing if already running.
    /// </summary>
    public void Start(string sudoPassword)
    {
        lock (_lock)
        {
            if (IsRunning) return;
            StopLocked();

            LastError = null;
            LatestGpuUtilizationPercent = null;
            LatestGpuPowerMw = null;

            _cts = new CancellationTokenSource();
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sudo",
                    Arguments = "-S -k powermetrics --samplers gpu_power -n -1 -i 1000",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            _process.Start();
            _process.StandardInput.WriteLine(sudoPassword);
            _process.StandardInput.Close();

            IsRunning = true;
            _readerTask = Task.Run(() => ReadLoopAsync(_process, _cts.Token), _cts.Token);
        }
    }

    /// <summary>Stops the privileged telemetry process.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        try { _cts?.Cancel(); } catch { }
        try { _process?.Kill(entireProcessTree: true); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
        _cts = null;
        IsRunning = false;
    }

    private async Task ReadLoopAsync(Process process, CancellationToken ct)
    {
        var block = new StringBuilder();
        var stderr = new StringBuilder();

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(ct)) != null)
                {
                    if (line.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Sorry", StringComparison.OrdinalIgnoreCase))
                    {
                        LastError = "Incorrect sudo password";
                    }
                    stderr.AppendLine(line);
                }
            }
            catch { }
        }, ct);

        try
        {
            await foreach (var line in process.StandardOutput.ReadAllLinesAsync(ct))
            {
                if (ct.IsCancellationRequested) break;

                if (line.Contains("**** GPU usage ****", StringComparison.OrdinalIgnoreCase))
                {
                    ParseBlock(block.ToString());
                    block.Clear();
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    block.AppendLine(line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            lock (_lock)
            {
                IsRunning = false;
                if (string.IsNullOrEmpty(LastError) && process.ExitCode != 0 && process.ExitCode != -1)
                {
                    var errTail = stderr.ToString().Trim();
                    LastError = string.IsNullOrEmpty(errTail)
                        ? $"powermetrics exited ({process.ExitCode})"
                        : errTail.Split('\n').Last().Trim();
                }
            }
        }
    }

    private void ParseBlock(string block)
    {
        double? util = null;
        double? power = null;

        foreach (var raw in block.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("GPU HW active residency:", StringComparison.OrdinalIgnoreCase))
            {
                util = ExtractLeadingPercent(line);
            }
            else if (line.StartsWith("GPU Power:", StringComparison.OrdinalIgnoreCase))
            {
                power = ExtractLeadingDouble(line);
            }
        }

        if (util > 1000) util /= 100.0; // Some Apple drivers report scaled integers.
        if (util.HasValue) util = Math.Clamp(util.Value, 0, 100);

        LatestGpuUtilizationPercent = util;
        LatestGpuPowerMw = power;
    }

    private static double? ExtractLeadingPercent(string line)
    {
        var match = Regex.Match(line, @"([0-9]+(?:\.[0-9]+)?)\s*%");
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static double? ExtractLeadingDouble(string line)
    {
        var match = Regex.Match(line, @"([0-9]+(?:\.[0-9]+)?)\s*m?W");
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public void Dispose() => Stop();
}

internal static class StreamReaderExtensions
{
    // IAsyncEnumerable wrapper so the read loop can be cancelled cleanly.
    public static async IAsyncEnumerable<string> ReadAllLinesAsync(
        this StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) yield break;
            yield return line;
        }
    }
}
