namespace Aria.Bridge.Services.Noosphere;

// Bridge-local config for the Noosphere memory system — bound from the "Noosphere" section of
// this node's appsettings.json. Mirrors the BuiltinTools.ConfigureWebSearch pattern: secrets stay
// on the node (KeyRef into the local LlmKeys vault, or a plain ApiKeyFile path), never the server.
public class NoosphereChannelOptions
{
    public string  Url        { get; set; } = "";
    public string  Model      { get; set; } = "";
    public string? KeyRef     { get; set; }
    public string? ApiKeyFile { get; set; }
}

public class NoosphereEmbeddingOptions : NoosphereChannelOptions
{
    public bool Enabled { get; set; } = true;
}

public class NoosphereOptions
{
    public const string SectionName = "Noosphere";

    public NoosphereChannelOptions   Extraction { get; set; } = new();
    public NoosphereEmbeddingOptions Embeddings { get; set; } = new();
}
