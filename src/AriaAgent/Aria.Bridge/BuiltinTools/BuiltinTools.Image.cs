using System.Text.Json;

namespace Aria.Bridge;

// Feeds a local image to a vision-capable model. The bytes ride the exact same mechanism
// TakeScreenshot uses (ToolCallResponse ImageBase64/ImageMediaType → MultimodalToolResult → a
// vision DataContent block when the active model was probed as vision-capable). Deliberately NOT
// added to GetToolInfos() — Harness registers it explicitly (case "read_image") so the vision
// probe gates whether the model receives the image; the terminal tool's /tools/list copy would
// carry no vision flag and the model would get text only. Same trust level as read_file:
// EnforcePath-validated, read-only, on the Layer B Benign list.
public static partial class BuiltinTools
{
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private static ToolCallResponse ReadImage(Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var path = Expand(args.Str("path") ?? throw new ArgumentException("'path' is required"));
        policy?.EnforcePath(path);

        if (Directory.Exists(path))
            return Err($"'{path}' is a directory, not a file.");
        if (!File.Exists(path))
            return Err($"File not found: {path}");

        var length = new FileInfo(path).Length;
        if (length > MaxImageBytes)
            return Err($"Image too large: {length / (1024.0 * 1024.0):F1} MB exceeds the 10 MB limit. " +
                       "Downscale or crop it first (e.g. with an image tool or bash_exec + sips/convert).");

        var bytes     = File.ReadAllBytes(path);
        var mediaType = SniffImageMediaType(bytes);
        if (mediaType == null)
            return Err($"Not a supported image: '{path}' is not png/jpeg/gif/webp (detected by content, not extension).");

        var text = $"Read image {path} ({mediaType}, {bytes.Length / 1024} KB).";
        return new ToolCallResponse(text, false, Convert.ToBase64String(bytes), mediaType);
    }

    // Magic-byte sniffing — never trust the extension. Supports png, jpeg, gif, webp.
    private static string? SniffImageMediaType(ReadOnlySpan<byte> b)
    {
        if (b is [0x89, 0x50, 0x4E, 0x47, ..]) return "image/png";                     // ‰PNG
        if (b is [0xFF, 0xD8, 0xFF, ..])       return "image/jpeg";                    // JFIF/Exif
        if (b is [0x47, 0x49, 0x46, 0x38, ..]) return "image/gif";                     // GIF8
        if (b.Length >= 12 &&
            b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&            // RIFF
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50)            // WEBP
            return "image/webp";
        return null;
    }
}
