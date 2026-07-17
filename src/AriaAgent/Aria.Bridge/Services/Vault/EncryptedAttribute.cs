namespace Aria.Bridge.Services.Vault;

/// <summary>
/// Marks a string property that should be transparently encrypted at rest by the vault encryption
/// layer (F-7). Applied to entity properties in <see cref="BridgeDbContext"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EncryptedAttribute : Attribute
{
}
