using System.Net;
using System.Text;
using System.Text.Json;

namespace Aria.Tests.Fakes;

/// <summary>
/// Routes HttpClient requests to in-memory JSON responses for testing bridge-facing code.
/// </summary>
public sealed class FakeBridgeHttpHandler : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Dictionary<string, object> _responses = new(StringComparer.OrdinalIgnoreCase);

    public void SetResponse(string path, object response) => _responses[path.Trim('/')] = response;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath.Trim('/') ?? "";
        if (_responses.TryGetValue(path, out var value))
        {
            var body = JsonSerializer.Serialize(value, Json);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
