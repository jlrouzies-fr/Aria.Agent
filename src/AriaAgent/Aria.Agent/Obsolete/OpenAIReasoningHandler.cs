using System.Text;
using System.Text.Json;

namespace Aria.Agent;

/// <summary>
/// Intercepts streaming chat completions and extracts reasoning_content that
/// the OpenAI SDK silently drops. When the reasoning block ends, the full
/// buffered text is delivered via OnReasoningContent so callers can render it
/// however they like (Spectre panel in console, log entry in web, etc.).
/// </summary>
public class OpenAIReasoningHandler : DelegatingHandler
{
    /// <summary>Called once per turn with the full reasoning text when available.</summary>
    public Action<string>? OnReasoningContent { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);

        if (request.RequestUri?.AbsolutePath.Contains("chat/completions") == true)
        {
            var originalStream = await response.Content.ReadAsStreamAsync(ct);
            var wrapped = new OpenAISSEStream(originalStream) { OnReasoningContent = OnReasoningContent };
            var replacement = new StreamContent(wrapped);

            foreach (var header in response.Content.Headers)
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);

            response.Content = replacement;
        }

        return response;
    }
}

public class OpenAISSEStream : Stream
{
    private readonly Stream _inner;
    private readonly StringBuilder _lineBuffer = new();
    private readonly StringBuilder _reasoningBuffer = new();
    private bool _reasoningOpen = false;
    private bool _reasoningPrinted = false;
    private readonly StreamWriter? _log;
    private const string LogPath = "DebugLogs/sse-debug.log";

    public Action<string>? OnReasoningContent { get; set; }

    public string ReasoningContent => _reasoningBuffer.ToString();

    public OpenAISSEStream(Stream inner)
    {
        _inner = inner;

        try
        {
            _log = new StreamWriter(LogPath, append: true, Encoding.UTF8) { AutoFlush = true };
            _log.WriteLine($"\n=== SSE session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        }
        catch
        {
            _log = null;
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken ct = default)
    {
        int bytesRead = await _inner.ReadAsync(buffer, ct);
        if (bytesRead > 0)
            InterceptChunk(buffer.Span[..bytesRead]);
        return bytesRead;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int bytesRead = _inner.Read(buffer, offset, count);
        if (bytesRead > 0)
            InterceptChunk(buffer.AsSpan(offset, bytesRead));
        return bytesRead;
    }

    private void InterceptChunk(ReadOnlySpan<byte> bytes)
    {
        _lineBuffer.Append(Encoding.UTF8.GetString(bytes));

        int newline;
        while ((newline = _lineBuffer.ToString().IndexOf('\n')) >= 0)
        {
            var line = _lineBuffer.ToString(0, newline).TrimEnd('\r');
            _lineBuffer.Remove(0, newline + 1);
            _log?.WriteLine(string.IsNullOrEmpty(line) ? "<blank line>" : line);
            ProcessSSELine(line);
        }
    }

    private void ProcessSSELine(string line)
    {
        if (!line.StartsWith("data: ")) return;

        var json = line["data: ".Length..];

        if (json == "[DONE]")
        {
            _log?.WriteLine("[DONE] received — triggering safety flush");
            FlushReasoning();
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);

            var choices = doc.RootElement.GetProperty("choices");

            if (choices.GetArrayLength() == 0)
            {
                _log?.WriteLine("[skipped] empty choices array (usage chunk)");
                return;
            }

            var delta = choices[0].GetProperty("delta");

            if (delta.TryGetProperty("reasoning_content", out var reasoningChunk))
            {
                var chunk = reasoningChunk.GetString();
                if (!string.IsNullOrEmpty(chunk))
                {
                    _reasoningOpen = true;
                    _reasoningBuffer.Append(chunk);
                    _log?.WriteLine($"[reasoning buffered] +{chunk.Length} chars, total={_reasoningBuffer.Length}");
                }
            }

            bool hasContent   = delta.TryGetProperty("content", out var content)
                                && !string.IsNullOrEmpty(content.GetString());
            bool hasToolCalls = delta.TryGetProperty("tool_calls", out _);

            if (!_reasoningPrinted && _reasoningOpen && (hasContent || hasToolCalls))
                FlushReasoning();
        }
        catch (JsonException ex)
        {
            _log?.WriteLine($"[json error] {ex.Message} | raw={json[..Math.Min(80, json.Length)]}");
        }
        catch (Exception ex)
        {
            _log?.WriteLine($"[unexpected error] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void FlushReasoning()
    {
        if (_reasoningPrinted || !_reasoningOpen) return;

        _reasoningPrinted = true;
        _reasoningOpen = false;

        var reasoning = _reasoningBuffer.ToString().TrimEnd();
        _log?.WriteLine($"[flush] reasoning length={reasoning.Length}");

        if (!string.IsNullOrWhiteSpace(reasoning))
            OnReasoningContent?.Invoke(reasoning);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _log?.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
