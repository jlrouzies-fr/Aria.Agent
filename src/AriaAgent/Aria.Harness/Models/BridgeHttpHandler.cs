using System.IO.Pipelines;
using System.Text;
using Aria.Harness.Core;

namespace Aria.Harness.Models;

/// <summary>
/// Routes outgoing AI HTTP requests through the bridge via <see cref="IHarnessRuntime.BridgeStreamAsync"/>.
/// Drop-in replacement for HttpClientHandler inside UniversalReasoningHandler for bridged sources.
/// </summary>
public sealed class BridgeHttpHandler : HttpMessageHandler
{
    private readonly IHarnessRuntime _runtime;
    private readonly HarnessContext _context;
    private readonly string? _keyRef;
    private readonly bool _requireKey;
    private readonly string? _nodeId;

    public BridgeHttpHandler(IHarnessRuntime runtime, HarnessContext context, string? keyRef = null, bool requireKey = true, string? nodeId = null)
    {
        _runtime    = runtime;
        _context    = context;
        _keyRef     = keyRef;
        _requireKey = requireKey;
        _nodeId     = nodeId;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content != null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : "";

        var pipe = new Pipe();

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in _runtime.BridgeStreamAsync(
                    request.RequestUri!.ToString(), body, _context, cancellationToken, _keyRef, _requireKey, _nodeId))
                {
                    // Generic error protocol: bridge streams the failure message as an SSE data line.
                    // Surface it as an HTTP exception so the caller sees the real message instead of a
                    // later JSON parse failure.
                    if (chunk.StartsWith("data: ERROR:", StringComparison.Ordinal))
                    {
                        var error = chunk["data: ERROR:".Length..].TrimEnd('\n');
                        pipe.Writer.Complete(new HttpRequestException(error));
                        return;
                    }
                    var bytes = Encoding.UTF8.GetBytes(chunk);
                    await pipe.Writer.WriteAsync(bytes, cancellationToken);
                    await pipe.Writer.FlushAsync(cancellationToken);
                }
                pipe.Writer.Complete();
            }
            catch (Exception ex)
            {
                pipe.Writer.Complete(ex);
            }
        }, CancellationToken.None);

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        response.Content = new StreamContent(pipe.Reader.AsStream());
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return response;
    }
}
