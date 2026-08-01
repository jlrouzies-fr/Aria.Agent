namespace Aria.Bridge.Services.Noosphere;

/// <summary>
/// Pinned artifacts for opt-in built-in Noosphere models. URLs + SHA256 are the trust boundary —
/// a mismatched file is refused rather than silently loaded.
/// </summary>
public static class NoosphereBuiltinCatalog
{
    public const string RoleExtract = "extract";
    public const string RoleEmbed = "embed";

    public sealed record ModelFile(string FileName, string Url, string Sha256Hex, long ApproxBytes);

    public sealed record RoleInfo(string Role, string Label, string License, IReadOnlyList<ModelFile> Files);

    public static readonly IReadOnlyList<RoleInfo> Roles =
    [
        new(RoleExtract, "LFM2.5-1.2B-Instruct (extraction)", "LFM Open License",
        [
            new(
                "LFM2.5-1.2B-Instruct-Q4_K_M.gguf",
                "https://huggingface.co/LiquidAI/LFM2.5-1.2B-Instruct-GGUF/resolve/main/LFM2.5-1.2B-Instruct-Q4_K_M.gguf",
                "b1b3de114215d9507409a662a501a631095a479a419584e8a2ded6304b19b4f5",
                730_895_168L)
        ]),
        new(RoleEmbed, "all-MiniLM-L6-v2 (embeddings)", "Apache-2.0",
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
        ]),
    ];

    public static RoleInfo? Lookup(string role) =>
        Roles.FirstOrDefault(r => string.Equals(r.Role, role, StringComparison.OrdinalIgnoreCase));

    public static string ModelIdFor(string role) => role.ToLowerInvariant() switch
    {
        RoleExtract => "LFM2.5-1.2B-Instruct-Q4_K_M",
        RoleEmbed => "all-MiniLM-L6-v2",
        _ => role
    };
}
