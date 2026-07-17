using System.Reflection;
using Aria.Bridge.Services.Vault;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Data;

public class BridgeDbContext(DbContextOptions<BridgeDbContext> options, VaultEncryption? vault = null) : DbContext(options)
{
    private readonly VaultEncryption? _vault = vault;

    /// <summary>The vault encryption layer used for at-rest protection of sensitive values.</summary>
    public VaultEncryption? Vault => _vault;
    public DbSet<BridgeSoul>           Souls           { get; set; }
    public DbSet<BridgeServerLink>     ServerLinks     { get; set; }
    public DbSet<BridgeCogitation>     Cogitations     { get; set; }
    public DbSet<BridgeMessage>        Messages        { get; set; }
    public DbSet<BridgeContact>        Contacts        { get; set; }
    public DbSet<BridgeOAuthToken>     OAuthTokens     { get; set; }

    // Node-authoritative OAuth app credentials (Microsoft tenant/client id/secret, Google OAuth client
    // JSON) — overrides for the appsettings.json-configured defaults, entered on the bridge status page
    // and encrypted at rest. Editable only on this node; the server never sees these.
    public DbSet<BridgeOAuthAppConfig> OAuthAppConfigs { get; set; }

    // File mutation undo history — node-owned, never the server.
    public DbSet<FileUndo>             FileUndos       { get; set; }

    // Hive content lives on the bridge node that owns the collective.
    public DbSet<BridgeHiveCollective> HiveCollectives { get; set; }
    public DbSet<BridgeHiveTask>       HiveTasks       { get; set; }
    public DbSet<BridgeHiveEvent>      HiveEvents      { get; set; }

    // Synced copies of Aria.Web config (server-authoritative mirror).
    public DbSet<SyncedSubAgent>         SyncedSubAgents         { get; set; }
    public DbSet<SyncedSubAgentToolState> SyncedSubAgentToolStates { get; set; }
    public DbSet<SyncedToolConfig>       SyncedToolConfigs       { get; set; }
    public DbSet<SyncedLocalSource>      SyncedLocalSources      { get; set; }
    public DbSet<SyncedMcpServer>        SyncedMcpServers        { get; set; }

    // Node-authoritative channels (custom / local LLM sources). Public cloud providers are NOT stored
    // here — they are derived from Aria.Shared.PublicProviderCatalog + key presence. Channels are
    // authored ONLY on this node (bridge status page), never synced from the server.
    public DbSet<BridgeChannel>          Channels                { get; set; }

    // Node-authoritative Noosphere memory configuration: which existing channel to use for extraction
    // and (optionally) embeddings. Editable only on this node; the server never authors it.
    public DbSet<NoosphereConfig>        NoosphereConfigs        { get; set; }

    // Node-authoritative MCP servers. Config (command, args, env, URL) is authored ONLY on this node;
    // the server receives only a read-only list of names/transports for display and selection.
    public DbSet<BridgeMcpServer>        McpServers              { get; set; }

    public DbSet<SyncedCogitationFolder> SyncedCogitationFolders { get; set; }

    // Noosphere — native agent memory (Engrams + entity graph). FTS5 index lives in raw SQL only
    // (EngramsFts virtual table, bootstrapped in BridgeDatabaseInitializer); EF never models it.
    public DbSet<MemoryIngest> MemoryIngests { get; set; }
    public DbSet<Engram>       Engrams       { get; set; }
    public DbSet<MemoryEntity> MemoryEntities { get; set; }
    public DbSet<EngramEntity> EngramEntities { get; set; }
    public DbSet<EntityLink>   EntityLinks   { get; set; }
    public DbSet<MemoryAnchor> MemoryAnchors { get; set; }

    // Layer B (defense-in-depth plan §4): node-approved grants that let the bridge run server-relayed
    // sensitive operations for a context (soul/session) without re-prompting, until they expire.
    public DbSet<ContextGrant> ContextGrants { get; set; }

    // Layer B Phase 2: locally-verified sibling node public keys. A node's grant-signing key is
    // accepted only if its enrollment certificate chains to the soul key or another trusted sibling.
    public DbSet<TrustedSiblingKey> TrustedSiblingKeys { get; set; }

    // F-8: node-side security audit trail for sensitive capability invocations.
    public DbSet<BridgeAuditEvent> AuditEvents { get; set; }

    // Node-local key/value settings (e.g. Layer B enforcement toggle). Local-only: never relayed to
    // or writable by the hosted server.
    public DbSet<BridgeSetting> Settings { get; set; }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BridgeSoul>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasMany(s => s.Cogitations).WithOne(c => c.Soul).HasForeignKey(c => c.SoulId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(s => s.ServerLinks).WithOne(l => l.Soul).HasForeignKey(l => l.SoulId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BridgeServerLink>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => new { l.SoulId, l.ServerUrl }).IsUnique();
        });

        b.Entity<BridgeContact>(e => e.HasKey(c => c.Id));

        // Match the table created by the manual schema bootstrap in BridgeDatabaseInitializer.
        b.Entity<BridgeOAuthToken>(e => e.ToTable("BridgeOAuthTokens"));

        b.Entity<BridgeOAuthAppConfig>(e =>
        {
            e.ToTable("OAuthAppConfig");
            e.HasKey(c => c.Provider);
        });

        b.Entity<BridgeCogitation>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasMany(c => c.Messages).WithOne(m => m.Cogitation).HasForeignKey(m => m.CogitationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => new { c.SoulId, c.UpdatedAt });
        });

        b.Entity<BridgeMessage>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.CogitationId, m.CreatedAt });
        });

        b.Entity<FileUndo>(e =>
        {
            e.ToTable("FileUndo");
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.CreatedAt);
        });

        b.Entity<BridgeHiveCollective>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => new { c.SoulId, c.UpdatedAt });
            e.HasMany(c => c.Tasks).WithOne(t => t.Collective).HasForeignKey(t => t.CollectiveId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Events).WithOne(ev => ev.Collective).HasForeignKey(ev => ev.CollectiveId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BridgeHiveTask>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => new { t.CollectiveId, t.UpdatedAt });
        });

        b.Entity<BridgeHiveEvent>(e =>
        {
            e.HasKey(ev => ev.Id);
            e.HasIndex(ev => new { ev.CollectiveId, ev.Timestamp });
        });

        b.Entity<SyncedSubAgent>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasMany(a => a.ToolStates).WithOne(s => s.SubAgent).HasForeignKey(s => s.SubAgentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SyncedSubAgentToolState>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.SubAgentId, s.ToolId }).IsUnique();
        });

        b.Entity<SyncedToolConfig>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.ToolId).IsUnique();
        });

        b.Entity<SyncedLocalSource>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Name).IsUnique();
        });

        b.Entity<SyncedMcpServer>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Name).IsUnique();
        });

        b.Entity<BridgeMcpServer>(e =>
        {
            e.ToTable("BridgeMcpServers");
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Name).IsUnique();
        });

        b.Entity<NoosphereConfig>(e =>
        {
            e.ToTable("NoosphereConfig");
            e.HasKey(c => c.Id);
        });

        b.Entity<SyncedCogitationFolder>(e =>
        {
            e.HasKey(f => f.Id);
        });

        b.Entity<MemoryIngest>(e =>
        {
            e.HasKey(i => i.Id);
            e.HasIndex(i => i.Status);
        });

        b.Entity<Engram>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.SoulId, m.Bank, m.CreatedAt });
        });

        b.Entity<MemoryEntity>(e =>
        {
            e.HasKey(n => n.Id);
            e.HasIndex(n => new { n.SoulId, n.Bank, n.CanonicalName }).IsUnique();
        });

        b.Entity<EngramEntity>(e =>
        {
            e.HasKey(x => new { x.EngramId, x.EntityId });
            e.HasIndex(x => x.EntityId);
            // Explicit FKs so EF orders inserts correctly within one SaveChanges batch — the raw-SQL
            // schema enforces these too, but EF only knows to sequence around FKs it has itself modeled.
            e.HasOne<Engram>().WithMany().HasForeignKey(x => x.EngramId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MemoryEntity>().WithMany().HasForeignKey(x => x.EntityId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EntityLink>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.FromEntityId);
            e.HasIndex(l => l.ToEntityId);
            e.HasOne<MemoryEntity>().WithMany().HasForeignKey(l => l.FromEntityId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MemoryEntity>().WithMany().HasForeignKey(l => l.ToEntityId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MemoryAnchor>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.SoulId, a.Bank, a.Source, a.Name }).IsUnique();
        });

        b.Entity<ContextGrant>(e =>
        {
            e.ToTable("ContextGrants");
            e.HasKey(g => g.Id);
            e.HasIndex(g => g.ContextId);
        });

        b.Entity<TrustedSiblingKey>(e =>
        {
            e.ToTable("TrustedSiblingKeys");
            e.HasKey(k => k.Id);
            e.HasIndex(k => new { k.UserId, k.NodeId }).IsUnique();
        });

        b.Entity<BridgeAuditEvent>(e =>
        {
            e.ToTable("AuditEvents");
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.Timestamp);
            e.HasIndex(a => new { a.Category, a.Timestamp });
        });

        b.Entity<BridgeSetting>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(s => s.Key);
        });

        // F-7: transparently encrypt properties marked with [Encrypted].
        if (_vault is not null)
        {
            var converter = new EncryptedValueConverter(_vault);
            foreach (var entityType in b.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    var member = property.PropertyInfo ?? (System.Reflection.MemberInfo?)property.FieldInfo;
                    if (member?.GetCustomAttributes(typeof(EncryptedAttribute), inherit: false).Any() == true)
                    {
                        property.SetValueConverter(converter);
                    }
                }
            }
        }
    }
}

/// <summary>
/// A local record that a human at this node approved sensitive server-relayed operations for a
/// context (Layer B, §4). Presence of a non-revoked, unexpired grant lets the bridge run classified-
/// sensitive requests without re-prompting. Signed grants (for cross-node replication) are a later
/// increment; for now the record is local, established by a human at the node.
/// </summary>
public class ContextGrant
{
    public int      Id              { get; set; }
    public string   ContextId       { get; set; } = "";   // soul id (coarse) or session id (future)
    public string   GrantType       { get; set; } = "context";
    public long     ExpiryUnix      { get; set; }
    public string?  SignatureBase64 { get; set; }         // reserved for cross-node replication
    public bool     Revoked         { get; set; }
    public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A sibling node public key that this bridge has locally verified. The key is trusted only because
/// its enrollment certificate signed by the soul key (or by another already-trusted sibling) checked
/// out. The hosted server cannot inject a key here: it can only relay certs; verification is local.
/// </summary>
public class TrustedSiblingKey
{
    public int      Id                       { get; set; }
    public string   UserId                   { get; set; } = "";   // server soul id
    public string   NodeId                   { get; set; } = "";   // thumbprint of NodePublicKeyBase64
    public string   NodePublicKeyBase64      { get; set; } = "";
    public string   CertifiedByPublicKeyBase64 { get; set; } = ""; // thumbprint of the signing key that vouched for it
    public DateTime CertifiedAt              { get; set; } = DateTime.UtcNow;
}

/// <summary>Node-local key/value setting. Persisted in the bridge's SQLite vault; never relayed to
/// the hosted server. Used for the Layer B enforcement toggle and similar node-owned preferences.</summary>
public class BridgeSetting
{
    public string Key   { get; set; } = "";
    public string Value { get; set; } = "";
}

public class BridgeSoul
{
    public string   Id               { get; set; } = Guid.NewGuid().ToString();
    public string   Name             { get; set; } = "";
    public string?  AvatarSpriteKey  { get; set; }
    public string?  AccentColor      { get; set; }
    public string?  PublicKeyBase64  { get; set; }
    [Encrypted]
    public string?  PrivateKeyBase64 { get; set; }
    public string?  ServerSoulId     { get; set; }
    public string?  ServerUrl        { get; set; }
    // Per-node identity (bridge remote-nodes §9). Null on the original/primary bridge → it signs with
    // the soul key (which the server treats as the implicitly-allowed primary node). Additional
    // bridges generate their own node keypair via "Join existing soul" and get enrolled.
    public string?  NodePublicKeyBase64  { get; set; }
    [Encrypted]
    public string?  NodePrivateKeyBase64 { get; set; }
    public string?  NodeId               { get; set; }
    public string?  NodeLabel            { get; set; }
    // The soul's Data Encryption Key (AES-256, base64) for E2E data sync (§11). Minted by the primary
    // bridge; delivered to additional nodes ECDH-wrapped at enrollment, unwrapped here on first connect.
    [Encrypted]
    public string?  DataKeyBase64        { get; set; }

    // Master switch for exposing any terminal capability (Quick Exec + PTY) from this node.
    // Legacy master switch. Retained only to seed the split capability flags below on upgrade; new
    // code gates on ProjectsEnabled / QuickExecEnabled / PTY (PtyEnabledUntil) individually.
    public bool     TerminalEnabled      { get; set; }

    // Three independently-toggleable capabilities, each off by default (a human at the node opts in):
    //   ProjectsEnabled  — the agent may work inside declared projects: read/write/search files, git,
    //                       and bash_exec, all scoped to the Allowed Paths below.
    //   QuickExecEnabled — the user-facing web Terminal may run one-shot commands (Quick Exec).
    //   PTY              — the user-facing web Terminal's interactive shell; seal-gated and time-limited
    //                       via PtyEnabledUntil (see below), independent of the two flags above.
    public bool     ProjectsEnabled      { get; set; }
    public bool     QuickExecEnabled     { get; set; }

    // Node-side policy for the Terminal tool. These are the maximum paths and blocked patterns this
    // node will allow; a request from the web may only narrow the path set, never widen it, and may
    // only add blocked patterns, never remove node-side ones. Stored as JSON arrays for portability.
    public string?  TerminalAllowedPathsJson     { get; set; }
    public string?  TerminalBlockedCommandsJson  { get; set; }

    // Rich Terminal projects (name + path + description). The authoritative allowed-path set is still
    // TerminalAllowedPathsJson (kept in sync on save) so SecurityPolicy and every path check are
    // unchanged; this JSON only adds the human-facing name/description the web modal displays.
    public string?  TerminalProjectsJson         { get; set; }

    // Inquisitorial Seal gate for full PTY shell access. The grant is time-limited: a seal enables
    // PTY only until PtyEnabledUntil (UTC). PtySealMinutes is the grant lifetime, editable node-side.
    // TerminalEnabled must also be true for PTY to work.
    public bool     PtyEnabled           { get; set; }  // legacy flag, retained for column compat
    public DateTime? PtyEnabledUntil     { get; set; }
    public int      PtySealMinutes       { get; set; } = 10;
    public DateTime CreatedAt        { get; set; } = DateTime.UtcNow;

    /// <summary>Maximum directories the Terminal may access on this node. Empty means all paths are blocked.</summary>
    public string[] GetTerminalAllowedPaths()
    {
        if (string.IsNullOrWhiteSpace(TerminalAllowedPathsJson)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(TerminalAllowedPathsJson)
                ?.Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray() ?? [];
        }
        catch { return []; }
    }

    /// <summary>Blocked command patterns enforced by this node, in addition to the hardcoded denylist.</summary>
    public string[] GetTerminalBlockedCommands()
    {
        if (string.IsNullOrWhiteSpace(TerminalBlockedCommandsJson)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(TerminalBlockedCommandsJson)
                ?.Where(c => !string.IsNullOrWhiteSpace(c))
                .ToArray() ?? [];
        }
        catch { return []; }
    }

    /// <summary>Rich Terminal projects (name + path + description). Falls back to deriving bare
    /// projects from the legacy allowed-paths list for nodes configured before projects gained names.</summary>
    public List<BridgeTerminalProject> GetTerminalProjects()
    {
        if (!string.IsNullOrWhiteSpace(TerminalProjectsJson))
        {
            try
            {
                var list = System.Text.Json.JsonSerializer.Deserialize<List<BridgeTerminalProject>>(TerminalProjectsJson);
                if (list != null)
                    return list.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Path)).ToList();
            }
            catch { }
        }
        // Legacy fallback: synthesise a name from the folder basename, no description.
        return GetTerminalAllowedPaths()
            .Select(p => new BridgeTerminalProject(BridgeTerminalProject.DeriveName(p), p, ""))
            .ToList();
    }

    public List<BridgeCogitation> Cogitations { get; set; } = [];
    public List<BridgeServerLink> ServerLinks { get; set; } = [];
}

/// <summary>A Terminal project: a named, described directory the node exposes to the Terminal tool.</summary>
public record BridgeTerminalProject(string Name, string Path, string Description)
{
    /// <summary>Fallback display name for a path with no explicit name — its trailing folder segment.</summary>
    public static string DeriveName(string p)
    {
        var n = System.IO.Path.GetFileName((p ?? "").TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(n) ? (p ?? "") : n;
    }
}

public class BridgeServerLink
{
    public string   Id           { get; set; } = Guid.NewGuid().ToString();
    public string   SoulId       { get; set; } = "";
    public BridgeSoul Soul       { get; set; } = null!;
    public string   ServerSoulId { get; set; } = "";
    public string   ServerUrl    { get; set; } = "";
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;
}

public class BridgeCogitation
{
    public string   Id            { get; set; } = Guid.NewGuid().ToString();
    public string   SoulId        { get; set; } = "";
    public BridgeSoul Soul        { get; set; } = null!;
    public string   Title         { get; set; } = "New Cogitation";
    public string?  AriaAvatarKey { get; set; }
    public string?  SubAgentId    { get; set; }
    public int?     FolderId      { get; set; }
    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt     { get; set; } = DateTime.UtcNow;

    public List<BridgeMessage> Messages { get; set; } = [];
}

public class BridgeMessage
{
    public string   Id              { get; set; } = Guid.NewGuid().ToString();
    public string   CogitationId    { get; set; } = "";
    public BridgeCogitation Cogitation { get; set; } = null!;
    public string   Role            { get; set; } = "";
    public string   Content         { get; set; } = "";
    public string?  ThinkingContent { get; set; }
    public string?  SectionsJson    { get; set; }   // serialized MessageSection[] for tool activity / diff cards
    public string?  ImageBase64     { get; set; }   // set only on a "screenshot" message
    public string?  ImageMediaType  { get; set; }
    public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;
}

public class BridgeContact
{
    public string   Id              { get; set; } = Guid.NewGuid().ToString();
    public string   Name            { get; set; } = "";
    public string   PublicKey       { get; set; } = "";
    public string?  AvatarSpriteKey { get; set; }
    public DateTime AddedAt         { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Node-authoritative override of the appsettings.json-configured OAuth app credentials, entered on
/// the bridge status page. When a row is present for a provider its fields take precedence over the
/// corresponding <c>Auth:Microsoft</c> / <c>Auth:Google</c> appsettings.json values (see
/// <see cref="Aria.Bridge.Services.Auth.BridgeOAuthConfig.ResolveAsync"/>); absent fields fall back to
/// the appsettings.json default. Secrets are encrypted at rest and never sent back to any client.
/// </summary>
public class BridgeOAuthAppConfig
{
    public string  Provider        { get; set; } = "";   // "microsoft" or "google" — primary key
    public string? TenantId        { get; set; }          // Microsoft only
    public string? ClientId        { get; set; }          // Microsoft only (Google's client id lives inside CredentialsJson)
    [Encrypted]
    public string? ClientSecret    { get; set; }          // Microsoft only
    [Encrypted]
    public string? CredentialsJson { get; set; }          // Google only — the raw downloaded OAuth client JSON
}

public class BridgeOAuthToken
{
    public string   Id           { get; set; } = Guid.NewGuid().ToString();
    public string   SoulId       { get; set; } = "";
    public string   Provider     { get; set; } = "";   // "microsoft" or "google"
    [Encrypted]
    public string   AccessToken  { get; set; } = "";
    [Encrypted]
    public string?  RefreshToken { get; set; }
    public DateTime? ExpiresAt   { get; set; }
    public string?  Email        { get; set; }
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt    { get; set; } = DateTime.UtcNow;
}

// ── File mutation undo history ───────────────────────────────────────────────

public class FileUndo
{
    public string   Id              { get; set; } = Guid.NewGuid().ToString();
    public string   Path            { get; set; } = "";        // primary path affected
    public string?  DestinationPath { get; set; }                // set only for move_path
    public string?  PreContent      { get; set; }                // null when the file did not exist (create)
    public string   PostHash        { get; set; } = "";
    public string   ToolName        { get; set; } = "";
    public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;
    public DateTime? RevertedAt     { get; set; }
}

// ── Synced server config (server-authoritative mirror) ───────────────────────

public class SyncedSubAgent
{
    public int      Id                   { get; set; }
    public string   GeneratedName        { get; set; } = "";
    public string   ArchetypeName        { get; set; } = "";
    public string   GeneratedPersonality { get; set; } = "";
    public string?  UserDirectives       { get; set; }
    public string   AccentColor          { get; set; } = "#8B0000";
    public string?  ModelSourceName      { get; set; }
    public string?  ModelId              { get; set; }
    public string?  EnabledMcpNamesJson  { get; set; }
    public string?  AvatarSpriteKey      { get; set; }
    public string?  Nickname             { get; set; }
    public DateTime CreatedAt            { get; set; } = DateTime.UtcNow;

    public List<SyncedSubAgentToolState> ToolStates { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? GeneratedName : Nickname;
}

public class SyncedSubAgentToolState
{
    public int    Id          { get; set; }
    public int    SubAgentId  { get; set; }
    public SyncedSubAgent SubAgent { get; set; } = null!;
    public string ToolId      { get; set; } = "";
    public bool   Enabled     { get; set; }
}

public class SyncedToolConfig
{
    public int    Id         { get; set; }
    public string ToolId     { get; set; } = "";
    public bool   Enabled    { get; set; }
    public string? ConfigJson { get; set; }
}

public class SyncedLocalSource
{
    public int    Id          { get; set; }
    public string Name        { get; set; } = "";
    public string Url         { get; set; } = "";
    public string ModelsJson  { get; set; } = "[]";
    public bool   IsBridged   { get; set; }
    public int    SortOrder   { get; set; }
    public string? BridgeNodeId { get; set; }
}

/// <summary>
/// A node-authoritative custom channel (a local/self-hosted LLM source). Authored only on this node
/// via the bridge status page; never synced from the server. Public cloud providers are not stored
/// here — they come from <see cref="Aria.Shared.PublicProviderCatalog"/>. The <see cref="Url"/> here is
/// the sole destination a call for this channel may reach; <c>/llm/proxy</c> never trusts a URL sent by
/// the server.
/// </summary>
public class BridgeChannel
{
    public int    Id         { get; set; }
    public string Name       { get; set; } = "";
    public string Url        { get; set; } = "";
    public string ModelsJson { get; set; } = "[]";
    public bool   IsBridged  { get; set; } = true;
    public int    SortOrder  { get; set; }
}

/// <summary>
/// Node-authoritative Noosphere memory configuration. References existing channel names so extraction and
/// (optional) embeddings run over the same bridged sources used elsewhere — no separate URLs or secrets
/// to maintain. Editable only on this node; the server never authors it.
/// </summary>
public class NoosphereConfig
{
    public int     Id                      { get; set; }
    public string? ExtractionChannelName   { get; set; }
    public string? EmbeddingsChannelName   { get; set; }
    public bool    EmbeddingsEnabled       { get; set; } = true;
    // Free-text model identifier for embeddings, distinct from the channel's own model list — a
    // channel is a URL+key (e.g. one LM Studio instance can serve both a chat model and a separately
    // loaded embedding model), so the embeddings model can't just be "the channel's first model".
    public string? EmbeddingsModel         { get; set; }
    // Same override for extraction: without this, extraction silently always uses whichever model
    // happens to be first in the channel's model list, with no way to pick a different one.
    public string? ExtractionModel         { get; set; }
}

/// <summary>
/// A node-authoritative MCP server. Authored only on this node via the bridge status page; never synced
/// from the server. The server receives only a read-only name list so MCP secrets (env, commands) stay
/// on the bridge.
/// </summary>
public class BridgeMcpServer
{
    public int       Id        { get; set; }
    public string    Name      { get; set; } = "";
    public int       Transport { get; set; }
    public string    Command   { get; set; } = "";
    public string    ArgsJson  { get; set; } = "[]";
    [Encrypted]
    public string?   EnvJson   { get; set; }
    public string?   Url       { get; set; }
    public bool      Enabled   { get; set; } = true;
    public int       SortOrder { get; set; }
}

public class SyncedMcpServer
{
    public int    Id        { get; set; }
    public string Name      { get; set; } = "";
    public int    Transport { get; set; }
    public string Command   { get; set; } = "";
    public string ArgsJson  { get; set; } = "[]";
    public string? EnvJson  { get; set; }
    public string? Url      { get; set; }
    public bool   Enabled   { get; set; }
}

public class SyncedCogitationFolder
{
    public int     Id                 { get; set; }
    public string  Name               { get; set; } = "";
    public string? Color              { get; set; }
    public int     SortOrder          { get; set; }
    public int?    DefaultSubAgentId  { get; set; }
    public string? DefaultProjectPath { get; set; }
    public string? StandingDirective  { get; set; }
}

// ── Hive content (owned by the bridge node that created the collective) ──────

public class BridgeHiveCollective
{
    public string   Id             { get; set; } = "";
    public string   SoulId         { get; set; } = "";
    public string   Objective      { get; set; } = "";
    public string?  ResultSummary  { get; set; }
    public string?  LastFeedback   { get; set; }
    public string?  SynapseMemory  { get; set; }
    public DateTime UpdatedAt      { get; set; } = DateTime.UtcNow;

    public List<BridgeHiveTask>  Tasks  { get; set; } = [];
    public List<BridgeHiveEvent> Events { get; set; } = [];
}

public class BridgeHiveTask
{
    public string   Id                   { get; set; } = "";
    public string   CollectiveId         { get; set; } = "";
    public BridgeHiveCollective Collective { get; set; } = null!;
    public string   Title                { get; set; } = "";
    public string   Instruction          { get; set; } = "";
    public string?  EffectiveInstruction { get; set; }
    public string?  Result               { get; set; }
    public DateTime UpdatedAt            { get; set; } = DateTime.UtcNow;
}

public class BridgeHiveEvent
{
    public string   Id             { get; set; } = "";
    public string   CollectiveId   { get; set; } = "";
    public BridgeHiveCollective Collective { get; set; } = null!;
    public DateTime Timestamp      { get; set; } = DateTime.UtcNow;
    public string   Type           { get; set; } = "";
    public int?     ActorMemberId  { get; set; }
    public int?     TaskId         { get; set; }
    public string   Message        { get; set; } = "";
}

// ── Noosphere: native agent memory (Engrams + entity graph) ──────────────────

public class MemoryIngest
{
    public string   Id        { get; set; } = Guid.NewGuid().ToString();
    public string   SoulId    { get; set; } = "";
    public string   Bank      { get; set; } = "default";
    public string   Content   { get; set; } = "";
    public string   Status    { get; set; } = "pending"; // pending | done | raw | error
    public string?  Error     { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Engram
{
    public string   Id             { get; set; } = Guid.NewGuid().ToString();
    public string   SoulId         { get; set; } = "";
    public string   Bank           { get; set; } = "default";
    public string?  IngestId       { get; set; }
    public string   Content        { get; set; } = "";
    public string?  TimeAnchor     { get; set; }
    public byte[]?  Embedding      { get; set; }        // float32 LE blob
    public string?  EmbeddingModel { get; set; }
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
}

public class MemoryEntity
{
    public string   Id            { get; set; } = Guid.NewGuid().ToString();
    public string   SoulId        { get; set; } = "";
    public string   Bank          { get; set; } = "default";
    public string   Name          { get; set; } = "";
    public string   CanonicalName { get; set; } = "";   // lower(trim(Name))
    public string?  Kind          { get; set; }         // person|place|org|concept|thing|event|other
    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;
}

public class EngramEntity
{
    public string EngramId { get; set; } = "";
    public string EntityId { get; set; } = "";
}

public class EntityLink
{
    public string   Id           { get; set; } = Guid.NewGuid().ToString();
    public string   SoulId       { get; set; } = "";
    public string   Bank         { get; set; } = "default";
    public string   FromEntityId { get; set; } = "";
    public string   ToEntityId   { get; set; } = "";
    public string   Relation     { get; set; } = "";
    public string?  EngramId     { get; set; }
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;
}

// Named entities to lead extraction toward — e.g. a Terminal project name/description, synced from
// Aria.Web (see NoosphereService.SyncAnchorsAsync). Grouped by Source so different origins (today
// just "terminal-project") can be replaced independently without clobbering each other.
public class MemoryAnchor
{
    public string   Id          { get; set; } = Guid.NewGuid().ToString();
    public string   SoulId      { get; set; } = "";
    public string   Bank        { get; set; } = "default";
    public string   Name        { get; set; } = "";
    public string   Description { get; set; } = "";
    public string   Source      { get; set; } = "terminal-project";
    public DateTime UpdatedAt   { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Node-side security audit event (F-8). Records sensitive capability invocations so a human can review
/// what their node was asked to do, whether it was allowed, and when.
/// </summary>
public class BridgeAuditEvent
{
    public int      Id          { get; set; }
    public DateTime Timestamp   { get; set; }
    public string   Category    { get; set; } = "";    // seal, terminal, soul, file, git, ...
    public string   Action      { get; set; } = "";    // e.g. "approved", "exec", "export", "rejected"
    public string?  Capability  { get; set; }            // e.g. "terminal_pty", "soul-export"
    public string?  Detail      { get; set; }            // human-readable detail (truncated)
    public bool     Allowed     { get; set; }
}
