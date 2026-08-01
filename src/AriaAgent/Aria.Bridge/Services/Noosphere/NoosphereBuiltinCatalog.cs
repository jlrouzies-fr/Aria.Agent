namespace Aria.Bridge.Services.Noosphere;

/// <summary>
/// Pinned artifacts for opt-in built-in Noosphere models. URLs + SHA256 are the trust boundary —
/// a mismatched file is refused rather than silently loaded.
/// </summary>
public static class NoosphereBuiltinCatalog
{
    public const string RoleExtract = "extract";
    public const string RoleEmbed = "embed";

    /// <summary>Legacy / smallest default — keeps existing downloads working.</summary>
    public const string DefaultExtractModelId = "lfm25-1.2b-q4km";

    public const string WarnTip1p2B =
        "1.2B extract — weak at entity kinds and relations. Q5/Q6 help a little vs Q4, but prefer LFM2-2.6B when RAM allows.";

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

    private const string Lfm25Repo =
        "https://huggingface.co/LiquidAI/LFM2.5-1.2B-Instruct-GGUF/resolve/main/";
    private const string Lfm26Repo =
        "https://huggingface.co/LiquidAI/LFM2-2.6B-GGUF/resolve/main/";

    public static readonly IReadOnlyList<ExtractVariant> ExtractVariants =
    [
        new("lfm25-1.2b-q4km", "LFM2.5-1.2B-Instruct Q4_K_M", "LFM Open License",
            "LFM2.5-1.2B-Instruct-Q4_K_M", WarnTip1p2B, Recommended: false,
            [File(Lfm25Repo, "LFM2.5-1.2B-Instruct-Q4_K_M.gguf",
                "b1b3de114215d9507409a662a501a631095a479a419584e8a2ded6304b19b4f5", 730_895_168L)]),

        new("lfm25-1.2b-q5km", "LFM2.5-1.2B-Instruct Q5_K_M", "LFM Open License",
            "LFM2.5-1.2B-Instruct-Q5_K_M", WarnTip1p2B, Recommended: false,
            [File(Lfm25Repo, "LFM2.5-1.2B-Instruct-Q5_K_M.gguf",
                "fa03f3ac4da941a53a0cd4450aacf6a80804c6a1ff885d2fdcbe9406c03215c4", 843_354_944L)]),

        new("lfm25-1.2b-q6k", "LFM2.5-1.2B-Instruct Q6_K", "LFM Open License",
            "LFM2.5-1.2B-Instruct-Q6_K", WarnTip1p2B, Recommended: false,
            [File(Lfm25Repo, "LFM2.5-1.2B-Instruct-Q6_K.gguf",
                "c5e895c191a066f6b26a8f09f10e94cdb799e579216f87df61a7e27beacd9a2b", 962_843_456L)]),

        new("lfm2-2.6b-q4km", "LFM2-2.6B Q4_K_M", "LFM Open License",
            "LFM2-2.6B-Q4_K_M", WarnTip: null, Recommended: false,
            [File(Lfm26Repo, "LFM2-2.6B-Q4_K_M.gguf",
                "384bc877b6c37064982f96885bef69e4475919f5969218ed4e3b9399ae0340df", 1_563_668_704L)]),

        new("lfm2-2.6b-q5km", "LFM2-2.6B Q5_K_M", "LFM Open License",
            "LFM2-2.6B-Q5_K_M", WarnTip: null, Recommended: true,
            [File(Lfm26Repo, "LFM2-2.6B-Q5_K_M.gguf",
                "1f1d46904e25f1b67b538bd658ee4e11ed311864e5e8247b22ea5ab7488c83ee", 1_828_958_432L)]),

        new("lfm2-2.6b-q6k", "LFM2-2.6B Q6_K", "LFM Open License",
            "LFM2-2.6B-Q6_K", WarnTip: null, Recommended: false,
            [File(Lfm26Repo, "LFM2-2.6B-Q6_K.gguf",
                "9de6f14a0dc6be851d0a1ca60acc76fc98ae59ba92921c6da90dea95159a8f85", 2_110_828_768L)]),
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
                           ?? "LFM2.5-1.2B-Instruct-Q4_K_M",
            RoleEmbed => "all-MiniLM-L6-v2",
            _ => role
        };

    public static string ProgressKey(string role, string? extractModelId = null) =>
        string.Equals(role, RoleExtract, StringComparison.OrdinalIgnoreCase)
            ? $"{RoleExtract}:{ResolveExtractId(extractModelId)}"
            : RoleEmbed;
}
