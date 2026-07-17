using System.IO.Pipelines;
using System.Text;
using Aria.Shared;

namespace Aria.Web.Services.ModelBridge;

/// <summary>
/// Custom HttpMessageHandler that routes outgoing AI HTTP requests through the
/// WASM bridge instead of making a direct TCP connection. Drop-in replacement
/// for HttpClientHandler inside UniversalReasoningHandler for bridged sources.
/// </summary>
public sealed class ModelBridgeHandler : HttpMessageHandler
{
    private readonly ModelBridgeRegistry _registry;
    private readonly string              _userId;
    private readonly string?             _keyRef;
    private readonly bool                _requireKey;
    private readonly string?             _nodeId;   // target bridge node (null = default node)

    /// <param name="keyRef">
    /// Source/provider name the bridge uses to look up the stored API key.
    /// Cloud providers: key is required (401 if absent). Local bridged sources: key is optional —
    /// the bridge proceeds without auth when none is stored (<paramref name="requireKey"/> = false).
    /// </param>
    public ModelBridgeHandler(ModelBridgeRegistry registry, string userId, string? keyRef = null,
        bool requireKey = true, string? nodeId = null)
    {
        _registry   = registry;
        _userId     = userId;
        _keyRef     = keyRef;
        _requireKey = requireKey;
        _nodeId     = nodeId;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var body      = request.Content != null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : "";

        string? apiKey = null;
        if (request.Headers.TryGetValues("Authorization", out var authVals))
        {
            var auth = authVals.FirstOrDefault();
            if (auth?.StartsWith("Bearer ", StringComparison.Ordinal) == true)
                apiKey = auth["Bearer ".Length..];
        }

        var bridgeReq = new BridgeRequest(
            requestId,
            request.RequestUri!.ToString(),
            body,
            // When keyRef is set the bridge resolves the key — don't also forward the credential so
            // the stored key takes precedence. Without keyRef (direct-key path) forward as-is.
            _keyRef != null ? null : apiKey,
            _keyRef,
            _requireKey);

        // Create a Pipe so we can stream chunks from the channel into the response body
        var pipe = new Pipe();

        _ = Task.Run(async () =>
        {
            // Retry once if the bridge connection dropped mid-request (brief instability
            // window after page load). A new connection typically registers within seconds.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await foreach (var chunk in _registry.SendRequestAsync(_userId, bridgeReq, cancellationToken, _nodeId))
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
                    return;
                }
                catch (Exception ex) when (attempt == 0 && ex.Message.Contains("Bridge connection lost"))
                {
                    // Connection dropped — wait for it to reconnect then retry
                    await Task.Delay(3000, cancellationToken);
                }
                catch (Exception ex)
                {
                    pipe.Writer.Complete(ex);
                    return;
                }
            }
            pipe.Writer.Complete(new Exception("Bridge connection lost after retry"));
        }, CancellationToken.None);

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        response.Content = new StreamContent(pipe.Reader.AsStream());
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return response;
    }
}
