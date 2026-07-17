using System.Data;
using System.Text;
using System.Text.Json;
using Aria.Bridge.Data;
using Aria.Bridge.Services.Vault;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// Verifies F-7: sensitive values in the bridge SQLite vault are encrypted at rest via the OS-backed
/// vault encryption layer.
/// </summary>
public class VaultEncryptionTests : IDisposable
{
    private readonly WebApplicationFactory<Aria.Bridge.Program> _factory;
    private readonly HttpClient _client;
    private readonly string _dbPath;
    private readonly string _keyDir;

    public VaultEncryptionTests()
    {
        var testId = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-vault-test-{testId}.db");
        _keyDir = Path.Combine(Path.GetTempPath(), $"aria-vault-key-{testId}");
        Directory.CreateDirectory(_keyDir);

        _factory = new WebApplicationFactory<Aria.Bridge.Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BridgeDbContext>));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddDbContext<BridgeDbContext>(opts => opts.UseSqlite($"Data Source={_dbPath}"));

                    var optsDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Options.IOptions<VaultEncryptionOptions>));
                    if (optsDescriptor != null) services.Remove(optsDescriptor);
                    services.Configure<VaultEncryptionOptions>(o => o.KeyDirectory = _keyDir);
                });
            });

        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_keyDir, recursive: true); } catch { }
    }

    [Fact]
    public void VaultEncryption_RoundTripsString()
    {
        var vault = new VaultEncryption(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VaultEncryption>.Instance,
            new VaultEncryptionOptions { KeyDirectory = _keyDir });

        const string secret = "my-soul-private-key";
        var encrypted = vault.Encrypt(secret);
        Assert.NotNull(encrypted);
        Assert.StartsWith("enc:1:", encrypted);

        var decrypted = vault.Decrypt(encrypted);
        Assert.Equal(secret, decrypted);
    }

    [Fact]
    public void VaultEncryption_LeavesPlaintextLegacyValuesReadable()
    {
        var vault = new VaultEncryption(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VaultEncryption>.Instance,
            new VaultEncryptionOptions { KeyDirectory = _keyDir });

        const string legacy = "unencrypted-legacy-value";
        Assert.Equal(legacy, vault.Decrypt(legacy));
    }

    [Fact]
    public async Task BridgeDbContext_EncryptsSensitiveColumns()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await using var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();

        var soul = new BridgeSoul
        {
            Name = "Vault Test Soul",
            PublicKeyBase64 = "pub",
            PrivateKeyBase64 = "super-secret-private-key",
        };
        db.Souls.Add(soul);
        await db.SaveChangesAsync();

        // Verify the raw DB value is encrypted, not the plaintext secret.
        var connectionString = $"Data Source={_dbPath}";
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PrivateKeyBase64 FROM Souls LIMIT 1";
        var raw = (string?)await cmd.ExecuteScalarAsync();
        Assert.NotNull(raw);
        Assert.StartsWith("enc:1:", raw);
        Assert.DoesNotContain("super-secret-private-key", raw);

        // Verify reading back through EF decrypts transparently.
        var read = await db.Souls.AsNoTracking().FirstAsync();
        Assert.Equal("super-secret-private-key", read.PrivateKeyBase64);
    }

    [Fact]
    public async Task BridgeDbContext_PlaintextLegacyValue_DecryptsOnRead()
    {
        var connectionString = $"Data Source={_dbPath}";
        await using var seedConn = new SqliteConnection(connectionString);
        await seedConn.OpenAsync();
        await using (var cmd = seedConn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Souls (
                    Id TEXT PRIMARY KEY NOT NULL,
                    Name TEXT NOT NULL DEFAULT '',
                    PublicKeyBase64 TEXT,
                    PrivateKeyBase64 TEXT,
                    TerminalEnabled INTEGER NOT NULL DEFAULT 0,
                    ProjectsEnabled INTEGER NOT NULL DEFAULT 0,
                    QuickExecEnabled INTEGER NOT NULL DEFAULT 0,
                    PtyEnabled INTEGER NOT NULL DEFAULT 0,
                    PtySealMinutes INTEGER NOT NULL DEFAULT 10,
                    CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
                );
                INSERT INTO Souls (Id, Name, PublicKeyBase64, PrivateKeyBase64, TerminalEnabled, ProjectsEnabled, QuickExecEnabled, PtyEnabled, PtySealMinutes, CreatedAt)
                VALUES ('legacy-soul', 'Legacy Soul', 'pub', 'plaintext-legacy-key', 0, 0, 0, 0, 10, datetime('now'));
            """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        await using var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
        var soul = await db.Souls.AsNoTracking().FirstAsync(s => s.Id == "legacy-soul");
        Assert.Equal("plaintext-legacy-key", soul.PrivateKeyBase64);
    }
}
