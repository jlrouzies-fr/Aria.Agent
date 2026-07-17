#if DEBUG
using System.Text;
using System.Text.Json;
using Aria.Web.Services;

namespace Aria.Web.Debug;

// Debug-only API for verifying the "#" file-reference system end-to-end — calls the bridge
// directly from the server (no WASM tunnel) and runs the SAME deserialization ProjectFilesClient
// uses, so a silent null in list/read mapping shows up here.
//
//   # List files under a declared project root
//   curl -s -X POST http://localhost:5129/api/debug/project-files/list \
//     -H "Content-Type: application/json" \
//     -d '{"root":"/abs/project","filter":"test","allowedPaths":["/abs/project"]}' | jq
//
//   # Read a file (abs path), reports content length + preview
//   curl -s -X POST http://localhost:5129/api/debug/project-files/read \
//     -H "Content-Type: application/json" \
//     -d '{"path":"/abs/project/test.txt","allowedPaths":["/abs/project"]}' | jq
public static class ProjectFilesDebugApiEndpoints
{
    private const string BridgeBase = "http://localhost:5741";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Register(WebApplication app)
    {
        var grp = app.MapGroup("/api/debug/project-files");

        // Direct bridge list + ProjectFilesClient-style deserialization.
        grp.MapPost("/list", async (HttpRequest req) =>
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            try
            {
                var resp = await Http.PostAsync(BridgeBase + "/project-files/list",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var raw = await resp.Content.ReadAsStringAsync();

                List<ProjectFileEntry> parsed = [];
                try
                {
                    var r = JsonSerializer.Deserialize<ListResponse>(raw, Json);
                    parsed = r?.Files ?? [];
                }
                catch (Exception ex) { return Results.Ok(new { status = (int)resp.StatusCode, raw, parseError = ex.Message }); }

                return Results.Ok(new
                {
                    status = (int)resp.StatusCode,
                    parsedCount = parsed.Count,
                    // Surface RelPath + whether AbsPath actually deserialized (the read depends on it).
                    parsed = parsed.Take(10).Select(f => new { f.RelPath, f.AbsPath, absPathOk = !string.IsNullOrEmpty(f.AbsPath) }),
                    raw,
                });
            }
            catch (Exception ex) { return Results.Problem($"Bridge unreachable: {ex.Message}"); }
        });

        // Direct bridge read + ProjectFilesClient-style deserialization.
        grp.MapPost("/read", async (HttpRequest req) =>
        {
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            try
            {
                var resp = await Http.PostAsync(BridgeBase + "/project-files/read",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var raw = await resp.Content.ReadAsStringAsync();

                string? content = null; bool? truncated = null; string? parseError = null;
                try
                {
                    var r = JsonSerializer.Deserialize<ReadResponse>(raw, Json);
                    content = r?.Content; truncated = r?.Truncated;
                }
                catch (Exception ex) { parseError = ex.Message; }

                return Results.Ok(new
                {
                    status = (int)resp.StatusCode,
                    parseError,
                    contentNull = content == null,
                    contentLength = content?.Length ?? 0,
                    contentEmpty = content is { Length: 0 },
                    truncated,
                    preview = content is { Length: > 0 } ? content[..Math.Min(200, content.Length)] : null,
                });
            }
            catch (Exception ex) { return Results.Problem($"Bridge unreachable: {ex.Message}"); }
        });
    }

    private record ListResponse(List<ProjectFileEntry> Files, int Scanned, bool Truncated);
    private record ReadResponse(string Path, string Content, bool Truncated);
}
#endif
