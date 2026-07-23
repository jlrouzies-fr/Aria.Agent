using System.Text.Json;
using Aria.Bridge;
using Aria.Shared;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Behaviour of the read_image builtin: magic-byte sniffing (extension is never trusted), the
/// 10 MB cap, EnforcePath validation, Layer B classification (Benign read, same trust level as
/// read_file), and the image-content shape — the same ToolCallResponse ImageBase64/ImageMediaType
/// mechanism TakeScreenshot uses to reach the chat UI and vision-capable models.
/// </summary>
public class BuiltinReadImageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"aria-readimg-{Guid.NewGuid():N}");

    public BuiltinReadImageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Dictionary<string, JsonElement> Args(object o) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 }, "image/jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1 }, "image/gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 4, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 1 }, "image/webp")]
    public async Task SupportedFormats_SniffedByContent_ReturnImageBlock(byte[] bytes, string expectedMediaType)
    {
        // Deliberately misleading extension — detection must come from the magic bytes.
        var path = WriteFile("image.txt", bytes);

        var result = await BuiltinTools.InvokeAsync("read_image", Args(new { path }), policy: null);

        Assert.False(result.IsError);
        Assert.Equal(expectedMediaType, result.ImageMediaType);
        Assert.Equal(Convert.ToBase64String(bytes), result.ImageBase64);
        Assert.Contains(path, result.Text);
    }

    [Fact]
    public async Task JunkBytes_Refused_WithClearMessage()
    {
        var path = WriteFile("fake.png", "this is definitely not an image"u8.ToArray());

        var result = await BuiltinTools.InvokeAsync("read_image", Args(new { path }), policy: null);

        Assert.True(result.IsError);
        Assert.Contains("not png/jpeg/gif/webp", result.Text);
        Assert.Null(result.ImageBase64);
    }

    [Fact]
    public async Task Over10Mb_Refused()
    {
        var bytes = new byte[10 * 1024 * 1024 + 1];
        PngBytes.CopyTo(bytes, 0);   // valid magic, oversized payload
        var path = WriteFile("big.png", bytes);

        var result = await BuiltinTools.InvokeAsync("read_image", Args(new { path }), policy: null);

        Assert.True(result.IsError);
        Assert.Contains("10 MB limit", result.Text);
        Assert.Null(result.ImageBase64);
    }

    [Fact]
    public async Task OutsideAllowedPaths_Blocked()
    {
        var path = WriteFile("ok.png", PngBytes);
        var policy = new SecurityPolicy(AllowedPaths: [Path.Combine(_dir, "elsewhere")]);

        var result = await BuiltinTools.InvokeAsync("read_image", Args(new { path }), policy);

        Assert.True(result.IsError);
        Assert.StartsWith("BLOCKED:", result.Text);
    }

    [Fact]
    public async Task MissingFile_ReportsNotFound()
    {
        var result = await BuiltinTools.InvokeAsync("read_image",
            Args(new { path = Path.Combine(_dir, "nope.png") }), policy: null);

        Assert.True(result.IsError);
        Assert.Contains("File not found", result.Text);
    }

    [Fact]
    public void Classification_IsBenignRead_LikeReadFile()
    {
        Assert.Equal(RequestSensitivity.Benign,
            RequestClassifier.Classify("POST", "/tools/call", """{"toolName":"read_image"}"""));
    }
}
