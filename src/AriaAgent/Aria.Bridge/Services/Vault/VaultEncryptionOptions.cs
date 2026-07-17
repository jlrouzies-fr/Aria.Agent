namespace Aria.Bridge.Services.Vault;

/// <summary>
/// Options for the F-7 vault encryption layer. Primarily exists so tests can isolate the protected
/// DEK from the production path.
/// </summary>
public sealed class VaultEncryptionOptions
{
    /// <summary>
    /// Directory where the protected DEK sidecar file is stored. Defaults to the bridge app-data
    /// directory in the user's profile.
    /// </summary>
    public string? KeyDirectory { get; set; }
}
