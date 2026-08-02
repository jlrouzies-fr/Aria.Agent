namespace Aria.Bridge.Services.Noosphere;

/// <summary>
/// Pinned artifacts for opt-in built-in Noosphere models. URLs + SHA256 are the trust boundary —
/// a mismatched file is refused rather than silently loaded.
/// </summary>
public static class NoosphereBuiltinCatalog
{
    public const string RoleExtract = "extract";
    public const string RoleEmbed = "embed";

    /// <summary>Smallest default — keeps cold installs light until the user picks a larger quant.</summary>
    public const string DefaultExtractModelId = "qwen25-1.5b-q4km";

    public const string WarnTip1p5B =
        "1.5B extract — weaker at entity kinds and long Inscribes. Prefer Qwen2.5-3B when RAM allows.";

    public sealed record ModelFile(string FileName, string Url, string Sha256Hex, long ApproxBytes);

    public sealed record ExtractVariant(
        string Id,
        string Label,
        string License,
        string ModelId,
        string? WarnTip,
        bool Recommended,
        IReadOnlyList<ModelFile> Files);

    public sealed record EmbedRole(string Role, string Label, string License, IReadOnlyList<ModelFile> Files);

    // Official Qwen GGUF repos — LFS oid is the file SHA256.
    private const string Qwen15Repo =
        "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/";
    private const string Qwen3BRepo =
        "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/";

    public static readonly IReadOnlyList<ExtractVariant> ExtractVariants =
    [
        new("qwen25-1.5b-q4km", "Qwen2.5-1.5B-Instruct Q4_K_M", "Apache-2.0",
            "Qwen2.5-1.5B-Instruct-Q4_K_M", WarnTip1p5B, Recommended: false,
            [File(Qwen15Repo, "qwen2.5-1.5b-instruct-q4_k_m.gguf",
                "6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e", 1_117_320_736L)]),

        new("qwen25-1.5b-q5km", "Qwen2.5-1.5B-Instruct Q5_K_M", "Apache-2.0",
            "Qwen2.5-1.5B-Instruct-Q5_K_M", WarnTip1p5B, Recommended: false,
            [File(Qwen15Repo, "qwen2.5-1.5b-instruct-q5_k_m.gguf",
                "b46661073c18e5b56a41fa320975f866a00def1ff08feef4718e013258896f8c", 1_285_494_304L)]),

        new("qwen25-1.5b-q6k", "Qwen2.5-1.5B-Instruct Q6_K", "Apache-2.0",
            "Qwen2.5-1.5B-Instruct-Q6_K", WarnTip1p5B, Recommended: false,
            [File(Qwen15Repo, "qwen2.5-1.5b-instruct-q6_k.gguf",
                "e16d94f3b1eb243f6f6be9eee51090ef5dfd741324394fd5b6e0e425c33df5c7", 1_464_178_720L)]),

        new("qwen25-3b-q4km", "Qwen2.5-3B-Instruct Q4_K_M", "Apache-2.0",
            "Qwen2.5-3B-Instruct-Q4_K_M", WarnTip: null, Recommended: true,
            [File(Qwen3BRepo, "qwen2.5-3b-instruct-q4_k_m.gguf",
                "626b4a6678b86442240e33df819e00132d3ba7dddfe1cdc4fbb18e0a9615c62d", 2_104_932_768L)]),

        new("qwen25-3b-q5km", "Qwen2.5-3B-Instruct Q5_K_M", "Apache-2.0",
            "Qwen2.5-3B-Instruct-Q5_K_M", WarnTip: null, Recommended: false,
            [File(Qwen3BRepo, "qwen2.5-3b-instruct-q5_k_m.gguf",
                "2c63dde5f2c9ab1fd64d47dee2d34dade6ba9ff62442d1d20b5342310c982081", 2_438_740_384L)]),

        new("qwen25-3b-q6k", "Qwen2.5-3B-Instruct Q6_K", "Apache-2.0",
            "Qwen2.5-3B-Instruct-Q6_K", WarnTip: null, Recommended: false,
            [File(Qwen3BRepo, "qwen2.5-3b-instruct-q6_k.gguf",
                "12da1a5d3fa6905111d8798b00ed49e0f6425441598c8c41bb37a2c36d49d0f3", 2_793_410_976L)]),
    ];

    public static readonly EmbedRole Embed = new(RoleEmbed, "all-MiniLM-L6-v2 (embeddings)", "Apache-2.0",
    [
        new(
            "all-MiniLM-L6-v2-quantized.onnx",
            "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/onnx/model_quantized.onnx",
            "afdb6f1a0e45b715d0bb9b11772f032c399babd23bfc31fed1c170afc848bdb1",
            22_972_370L),
        new(
            "vocab.txt",
            "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/vocab.txt",
            "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
            231_508L)
    ]);

    private static ModelFile File(string repo, string name, string sha, long bytes) =>
        new(name, repo + name, sha, bytes);

    public static string ResolveExtractId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return DefaultExtractModelId;
        var v = LookupExtract(id);
        return v?.Id ?? DefaultExtractModelId;
    }

    public static ExtractVariant? LookupExtract(string id) =>
        ExtractVariants.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnownExtractId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && LookupExtract(id!) != null;

    public static bool IsKnownRole(string role) =>
        string.Equals(role, RoleExtract, StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, RoleEmbed, StringComparison.OrdinalIgnoreCase);

    public static string ModelIdFor(string role, string? extractModelId = null) =>
        role.ToLowerInvariant() switch
        {
            RoleExtract => (LookupExtract(ResolveExtractId(extractModelId))?.ModelId)
                           ?? "Qwen2.5-1.5B-Instruct-Q4_K_M",
            RoleEmbed => "all-MiniLM-L6-v2",
            _ => role
        };

    public static string ProgressKey(string role, string? extractModelId = null) =>
        string.Equals(role, RoleExtract, StringComparison.OrdinalIgnoreCase)
            ? $"{RoleExtract}:{ResolveExtractId(extractModelId)}"
            : RoleEmbed;
}
