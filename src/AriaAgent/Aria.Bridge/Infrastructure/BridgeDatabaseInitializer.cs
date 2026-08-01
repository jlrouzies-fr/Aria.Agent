using Aria.Bridge.Data;
using Aria.Bridge.Services.Logging;
using Aria.Bridge.Services.Noosphere;
using Microsoft.EntityFrameworkCore;

namespace Aria.Bridge.Infrastructure;

public static class BridgeDatabaseInitializer
{
    // The vault lives in per-user app data, NOT next to the executable: reinstalling/updating the
    // bridge replaces the install directory, and a vault stored there was wiped along with it
    // (lost soul + keys on every update). Legacy vaults are migrated on first run.
    public static string DbPath { get; } = ResolveDbPath();

    private static string ResolveDbPath()
    {
        var dir = BridgeDataDir.Resolve();
        var newPath    = Path.Combine(dir, "aria-bridge.db");
        var legacyPath = Path.Combine(AppContext.BaseDirectory, "aria-bridge.db");

        try
        {
            Directory.CreateDirectory(dir);

            // One-time migration: adopt a vault created by an older version next to the exe.
            // Skipped when the data dir is explicitly overridden (fresh isolated vault by design).
            if (BridgeDataDir.Override is null && !File.Exists(newPath) && File.Exists(legacyPath))
            {
                File.Copy(legacyPath, newPath);
                foreach (var suffix in new[] { "-wal", "-shm" })   // SQLite side files, if present
                    if (File.Exists(legacyPath + suffix))
                        File.Copy(legacyPath + suffix, newPath + suffix, overwrite: true);
                BridgeLogger.Log("INFO", $"Migrated local vault {legacyPath} → {newPath}");
            }
            return newPath;
        }
        catch (Exception ex)
        {
            // App-data unavailable (unusual) — keep the old behaviour rather than failing startup.
            BridgeLogger.Log("WARN", $"Could not use app-data vault dir ({ex.Message}) — falling back to {legacyPath}");
            return legacyPath;
        }
    }

    public static async Task InitializeBridgeDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();

        // Ensure local DB exists on first run.
        await db.Database.EnsureCreatedAsync();

        // Keep one EF-managed connection open for the manual schema migrations below.
        // This avoids EF/SQLite state desync when we mix raw DbCommands with ExecuteSqlRawAsync.
        await db.Database.OpenConnectionAsync();

        // Add tables introduced after initial schema — safe to re-run.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS Contacts (
                Id TEXT PRIMARY KEY NOT NULL,
                Name TEXT NOT NULL DEFAULT '',
                PublicKey TEXT NOT NULL DEFAULT '',
                AvatarSpriteKey TEXT,
                AddedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
        """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS FileUndo (
                Id TEXT PRIMARY KEY NOT NULL,
                Path TEXT NOT NULL,
                DestinationPath TEXT,
                PreContent TEXT,
                PostHash TEXT NOT NULL,
                ToolName TEXT NOT NULL,
                Checkpoint TEXT,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                RevertedAt TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_FileUndo_CreatedAt ON FileUndo (CreatedAt);
        """);
        // IX_FileUndo_Checkpoint is created further below, after the ALTER TABLE migration that adds
        // the Checkpoint column to pre-existing databases (creating the index here would fail on
        // those, since CREATE TABLE IF NOT EXISTS is a no-op when the table already exists).

        // Cloud-provider API keys, held locally so the server never stores them.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS LlmKeys (
                Provider TEXT PRIMARY KEY NOT NULL,
                KeyB64   TEXT NOT NULL
            );
        """);

        // OAuth tokens for Microsoft/Google integrations — owned by the bridge, never the server.
        // Align older EF-created "OAuthTokens" tables with the canonical "BridgeOAuthTokens" name
        // used by the manual schema bootstrap below.
        var migrationConn = db.Database.GetDbConnection();
        await migrationConn.OpenAsync();
        await using (var cmd = migrationConn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='OAuthTokens'";
            var oauthTokensExists = (long)(await cmd.ExecuteScalarAsync())! > 0;
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='BridgeOAuthTokens'";
            var bridgeOAuthTokensExists = (long)(await cmd.ExecuteScalarAsync())! > 0;

            if (oauthTokensExists)
            {
                if (bridgeOAuthTokensExists)
                {
                    // Both exist: the canonical one from the manual bootstrap is redundant
                    // (EF already created OAuthTokens). Drop it before renaming.
                    cmd.CommandText = "DROP TABLE BridgeOAuthTokens;";
                    await cmd.ExecuteNonQueryAsync();
                }
                cmd.CommandText = "ALTER TABLE OAuthTokens RENAME TO BridgeOAuthTokens;";
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await migrationConn.CloseAsync();

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS BridgeOAuthTokens (
                Id TEXT PRIMARY KEY NOT NULL,
                SoulId TEXT NOT NULL,
                Provider TEXT NOT NULL,
                AccessToken TEXT NOT NULL,
                RefreshToken TEXT,
                ExpiresAt TEXT,
                Email TEXT,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(SoulId, Provider)
            );
            CREATE INDEX IF NOT EXISTS IX_BridgeOAuthTokens_SoulId_Provider ON BridgeOAuthTokens (SoulId, Provider);
        """);

        // Synced server config (server-authoritative mirror for Aria.Console).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS SyncedSubAgents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GeneratedName TEXT NOT NULL,
                ArchetypeName TEXT NOT NULL DEFAULT '',
                GeneratedPersonality TEXT NOT NULL,
                UserDirectives TEXT,
                AccentColor TEXT NOT NULL DEFAULT '#8B0000',
                ModelSourceName TEXT,
                ModelId TEXT,
                EnabledMcpNamesJson TEXT,
                AvatarSpriteKey TEXT,
                Nickname TEXT,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS SyncedSubAgentToolStates (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SubAgentId INTEGER NOT NULL,
                ToolId TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1,
                UNIQUE(SubAgentId, ToolId),
                FOREIGN KEY (SubAgentId) REFERENCES SyncedSubAgents(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SyncedToolConfigs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ToolId TEXT NOT NULL UNIQUE,
                Enabled INTEGER NOT NULL DEFAULT 0,
                ConfigJson TEXT
            );

            CREATE TABLE IF NOT EXISTS SyncedLocalSources (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                Url TEXT NOT NULL DEFAULT '',
                ModelsJson TEXT NOT NULL DEFAULT '[]',
                IsBridged INTEGER NOT NULL DEFAULT 0,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                BridgeNodeId TEXT
            );

            CREATE TABLE IF NOT EXISTS SyncedMcpServers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                Transport INTEGER NOT NULL DEFAULT 0,
                Command TEXT NOT NULL DEFAULT '',
                ArgsJson TEXT NOT NULL DEFAULT '[]',
                EnvJson TEXT,
                Url TEXT,
                Enabled INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS Channels (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                Url TEXT NOT NULL DEFAULT '',
                ModelsJson TEXT NOT NULL DEFAULT '[]',
                IsBridged INTEGER NOT NULL DEFAULT 1,
                SortOrder INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS NoosphereConfig (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ExtractionChannelName TEXT,
                EmbeddingsChannelName TEXT,
                EmbeddingsEnabled INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS BridgeMcpServers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                Transport INTEGER NOT NULL DEFAULT 0,
                Command TEXT NOT NULL DEFAULT '',
                ArgsJson TEXT NOT NULL DEFAULT '[]',
                EnvJson TEXT,
                Url TEXT,
                Enabled INTEGER NOT NULL DEFAULT 1,
                SortOrder INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS SyncedCogitationFolders (
                Id INTEGER PRIMARY KEY NOT NULL,
                Name TEXT NOT NULL,
                Color TEXT,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                DefaultSubAgentId INTEGER,
                DefaultProjectPath TEXT,
                StandingDirective TEXT
            );
        """);

        // Hive content (server-collective data owned by this bridge node).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS HiveCollectives (
                Id TEXT PRIMARY KEY NOT NULL,
                SoulId TEXT NOT NULL,
                Objective TEXT NOT NULL,
                ResultSummary TEXT,
                LastFeedback TEXT,
                SynapseMemory TEXT,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_HiveCollectives_SoulId_UpdatedAt ON HiveCollectives (SoulId, UpdatedAt);

            CREATE TABLE IF NOT EXISTS HiveTasks (
                Id TEXT PRIMARY KEY NOT NULL,
                CollectiveId TEXT NOT NULL,
                Title TEXT NOT NULL,
                Instruction TEXT NOT NULL,
                EffectiveInstruction TEXT,
                Result TEXT,
                UpdatedAt TEXT NOT NULL,
                FOREIGN KEY (CollectiveId) REFERENCES HiveCollectives (Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_HiveTasks_CollectiveId_UpdatedAt ON HiveTasks (CollectiveId, UpdatedAt);

            CREATE TABLE IF NOT EXISTS HiveEvents (
                Id TEXT PRIMARY KEY NOT NULL,
                CollectiveId TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                Type TEXT NOT NULL,
                ActorMemberId INTEGER,
                TaskId INTEGER,
                Message TEXT NOT NULL,
                FOREIGN KEY (CollectiveId) REFERENCES HiveCollectives (Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_HiveEvents_CollectiveId_Timestamp ON HiveEvents (CollectiveId, Timestamp);
        """);

        // Add columns introduced after initial schema if not already present (check first to avoid EF error logging).
        var dbConn = db.Database.GetDbConnection();
        foreach (var col in new[]
        {
            ("ServerUrl",            "TEXT"),
            ("NodePublicKeyBase64",  "TEXT"),
            ("NodePrivateKeyBase64", "TEXT"),
            ("NodeId",               "TEXT"),
            ("NodeLabel",            "TEXT"),
            ("SoulKeyPinnedAt",      "TEXT"),
            ("DataKeyBase64",        "TEXT"),
            ("TerminalEnabled",            "INTEGER NOT NULL DEFAULT 0"),
            ("TerminalAllowedPathsJson",   "TEXT"),
            ("TerminalBlockedCommandsJson","TEXT"),
            ("TerminalProjectsJson",       "TEXT"),
            ("PtyEnabled",                 "INTEGER NOT NULL DEFAULT 0"),
            ("PtyEnabledUntil",            "TEXT"),
            ("PtySealMinutes",             "INTEGER NOT NULL DEFAULT 10"),
        })
        {
            await using var chk = dbConn.CreateCommand();
            chk.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('Souls') WHERE name='{col.Item1}'";
            if ((long)(await chk.ExecuteScalarAsync())! == 0)
            {
                chk.CommandText = $"ALTER TABLE Souls ADD COLUMN {col.Item1} {col.Item2};";
                await chk.ExecuteNonQueryAsync();
            }
        }

        // Split the legacy master Terminal switch into three independent capabilities. Seed each
        // ONCE (only on the run that adds its column) from TerminalEnabled so an upgrade preserves
        // the prior behaviour; thereafter the node owner toggles each capability on its own.
        foreach (var col in new[] { "ProjectsEnabled", "QuickExecEnabled" })
        {
            await using var chk = dbConn.CreateCommand();
            chk.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('Souls') WHERE name='{col}'";
            if ((long)(await chk.ExecuteScalarAsync())! == 0)
            {
                chk.CommandText = $"ALTER TABLE Souls ADD COLUMN {col} INTEGER NOT NULL DEFAULT 0;";
                await chk.ExecuteNonQueryAsync();
                chk.CommandText = $"UPDATE Souls SET {col} = TerminalEnabled;";
                await chk.ExecuteNonQueryAsync();
            }
        }

        // Inline screenshot images: a "screenshot" message carries the captured image so it survives
        // a refresh and renders in the transcript (EnsureCreated adds these on fresh DBs; existing
        // ones need the ALTER).
        foreach (var col in new[] { ("ImageBase64", "TEXT"), ("ImageMediaType", "TEXT") })
        {
            await using var chk = dbConn.CreateCommand();
            chk.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name='{col.Item1}'";
            if ((long)(await chk.ExecuteScalarAsync())! == 0)
            {
                chk.CommandText = $"ALTER TABLE Messages ADD COLUMN {col.Item1} {col.Item2};";
                await chk.ExecuteNonQueryAsync();
            }
        }

        // Tool activity sections (diff cards, etc.) — serialized so they survive a reload.
        foreach (var col in new[] { ("SectionsJson", "TEXT") })
        {
            await using var chk = dbConn.CreateCommand();
            chk.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name='{col.Item1}'";
            if ((long)(await chk.ExecuteScalarAsync())! == 0)
            {
                chk.CommandText = $"ALTER TABLE Messages ADD COLUMN {col.Item1} {col.Item2};";
                await chk.ExecuteNonQueryAsync();
            }
        }

        // Cogitation folders: server-side dossier metadata mirrored for Aria.Console.
        foreach (var col in new[] { ("FolderId", "INTEGER") })
        {
            await using var chk = dbConn.CreateCommand();
            chk.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('Cogitations') WHERE name='{col.Item1}'";
            if ((long)(await chk.ExecuteScalarAsync())! == 0)
            {
                chk.CommandText = $"ALTER TABLE Cogitations ADD COLUMN {col.Item1} {col.Item2};";
                await chk.ExecuteNonQueryAsync();
            }
        }

        // Embeddings/extraction model: free-text, independent of the channel's own chat-model list.
        // Builtin*: opt-in on-node models (see docs/ideas/noosphere-builtin-models-plan.md).
        foreach (var col in new[]
                 {
                     ("EmbeddingsModel", "TEXT"),
                     ("ExtractionModel", "TEXT"),
                     ("BuiltinEnabled", "INTEGER NOT NULL DEFAULT 0"),
                     ("BuiltinLicenseAcceptedAt", "TEXT"),
                     ("BuiltinExtractModelId", "TEXT")
                 })
        {
            await using var chk = dbConn.CreateCommand();
            chk.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('NoosphereConfig') WHERE name='{col.Item1}'";
            if ((long)(await chk.ExecuteScalarAsync())! == 0)
            {
                chk.CommandText = $"ALTER TABLE NoosphereConfig ADD COLUMN {col.Item1} {col.Item2};";
                await chk.ExecuteNonQueryAsync();
            }
        }

        // Context-window discovery: per-channel user override for model context budgets.
        foreach (var col in new[] { ("ContextWindow", "INTEGER") })
        {
            await using var chk = dbConn.CreateCommand();
            chk.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('Channels') WHERE name='{col.Item1}'";
            if ((long)(await chk.ExecuteScalarAsync())! == 0)
            {
                chk.CommandText = $"ALTER TABLE Channels ADD COLUMN {col.Item1} {col.Item2};";
                await chk.ExecuteNonQueryAsync();
            }
        }

        // Turn checkpoints for /rewind: tag FileUndo rows with the cogitation-run id that caused them.
        foreach (var col in new[] { ("Checkpoint", "TEXT") })
        {
            await using var chk = dbConn.CreateCommand();
            chk.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('FileUndo') WHERE name='{col.Item1}'";
            if ((long)(await chk.ExecuteScalarAsync())! == 0)
            {
                chk.CommandText = $"ALTER TABLE FileUndo ADD COLUMN {col.Item1} {col.Item2};";
                await chk.ExecuteNonQueryAsync();
            }
        }
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_FileUndo_Checkpoint ON FileUndo (Checkpoint);");

        // Server link history: one active link remains mirrored on Souls.ServerUrl/ServerSoulId,
        // but multiple saved links can be stored for quick switching.
        await db.Database.OpenConnectionAsync();
        await using (var linksCmd = dbConn.CreateCommand())
        {
            linksCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS ServerLinks (
                    Id TEXT PRIMARY KEY NOT NULL,
                    SoulId TEXT NOT NULL,
                    ServerSoulId TEXT NOT NULL,
                    ServerUrl TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                    UNIQUE(SoulId, ServerUrl),
                    FOREIGN KEY (SoulId) REFERENCES Souls(Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_ServerLinks_SoulId ON ServerLinks (SoulId);

                INSERT OR IGNORE INTO ServerLinks (Id, SoulId, ServerSoulId, ServerUrl, CreatedAt)
                SELECT lower(hex(randomblob(16))), Id, COALESCE(ServerSoulId, ''), COALESCE(ServerUrl, ''), datetime('now')
                FROM Souls
                WHERE COALESCE(ServerUrl, '') <> '';
            """;
            await linksCmd.ExecuteNonQueryAsync();
        }

        // Noosphere: native agent memory (Engrams + entity graph).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS MemoryIngests (
                Id TEXT PRIMARY KEY NOT NULL,
                SoulId TEXT NOT NULL,
                Bank TEXT NOT NULL DEFAULT 'default',
                Content TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'pending',
                Error TEXT,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS IX_MemoryIngests_Status ON MemoryIngests (Status);

            CREATE TABLE IF NOT EXISTS Engrams (
                Id TEXT PRIMARY KEY NOT NULL,
                SoulId TEXT NOT NULL,
                Bank TEXT NOT NULL DEFAULT 'default',
                IngestId TEXT,
                Content TEXT NOT NULL,
                TimeAnchor TEXT,
                Embedding BLOB,
                EmbeddingModel TEXT,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS IX_Engrams_SoulId_Bank_CreatedAt ON Engrams (SoulId, Bank, CreatedAt);

            CREATE TABLE IF NOT EXISTS MemoryEntities (
                Id TEXT PRIMARY KEY NOT NULL,
                SoulId TEXT NOT NULL,
                Bank TEXT NOT NULL DEFAULT 'default',
                Name TEXT NOT NULL,
                CanonicalName TEXT NOT NULL,
                Kind TEXT,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(SoulId, Bank, CanonicalName)
            );

            CREATE TABLE IF NOT EXISTS EngramEntities (
                EngramId TEXT NOT NULL,
                EntityId TEXT NOT NULL,
                PRIMARY KEY (EngramId, EntityId),
                FOREIGN KEY (EngramId) REFERENCES Engrams(Id) ON DELETE CASCADE,
                FOREIGN KEY (EntityId) REFERENCES MemoryEntities(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_EngramEntities_EntityId ON EngramEntities (EntityId);

            CREATE TABLE IF NOT EXISTS EntityLinks (
                Id TEXT PRIMARY KEY NOT NULL,
                SoulId TEXT NOT NULL,
                Bank TEXT NOT NULL DEFAULT 'default',
                FromEntityId TEXT NOT NULL,
                ToEntityId TEXT NOT NULL,
                Relation TEXT NOT NULL,
                EngramId TEXT,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (FromEntityId) REFERENCES MemoryEntities(Id),
                FOREIGN KEY (ToEntityId) REFERENCES MemoryEntities(Id)
            );
            CREATE INDEX IF NOT EXISTS IX_EntityLinks_From ON EntityLinks (FromEntityId);
            CREATE INDEX IF NOT EXISTS IX_EntityLinks_To ON EntityLinks (ToEntityId);

            CREATE TABLE IF NOT EXISTS MemoryAnchors (
                Id TEXT PRIMARY KEY NOT NULL,
                SoulId TEXT NOT NULL,
                Bank TEXT NOT NULL DEFAULT 'default',
                Name TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',
                Source TEXT NOT NULL DEFAULT 'terminal-project',
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(SoulId, Bank, Source, Name)
            );
        """);

        // Layer B (§4): node-approved grants for sensitive server-relayed operations.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ContextGrants (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ContextId TEXT NOT NULL,
                GrantType TEXT NOT NULL DEFAULT 'context',
                ExpiryUnix INTEGER NOT NULL,
                SignatureBase64 TEXT,
                Revoked INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS IX_ContextGrants_ContextId ON ContextGrants (ContextId);
        """);

        // Revocation tombstones: node-signed revocations that replicate alongside grants, so a
        // revoke on one node kills the grant on every sibling (kills only the revoked instance —
        // a later re-approval with a longer expiry is not blocked).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ContextGrantTombstones (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ContextId TEXT NOT NULL,
                GrantExpiryUnix INTEGER NOT NULL,
                SignatureBase64 TEXT,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS IX_ContextGrantTombstones_ContextId ON ContextGrantTombstones (ContextId);
        """);

        // Layer B Phase 2: locally-verified sibling node public keys.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS TrustedSiblingKeys (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId TEXT NOT NULL,
                NodeId TEXT NOT NULL,
                NodePublicKeyBase64 TEXT NOT NULL,
                CertifiedByPublicKeyBase64 TEXT NOT NULL,
                CertifiedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS IX_TrustedSiblingKeys_UserId_NodeId ON TrustedSiblingKeys (UserId, NodeId);
        """);

        // Node-local key/value settings (Layer B enforcement toggle, etc.). Local-only.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY NOT NULL,
                Value TEXT NOT NULL DEFAULT ''
            );
        """);

        // Node-authoritative OAuth app credential overrides (Microsoft tenant/client id/secret, Google
        // OAuth client JSON), entered on the bridge status page in place of appsettings.json.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS OAuthAppConfig (
                Provider TEXT PRIMARY KEY NOT NULL,
                TenantId TEXT,
                ClientId TEXT,
                ClientSecret TEXT,
                CredentialsJson TEXT
            );
        """);

        // F-8: node-side security audit trail for sensitive capability invocations.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS AuditEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL DEFAULT (datetime('now')),
                Category TEXT NOT NULL,
                Action TEXT NOT NULL,
                Capability TEXT,
                Detail TEXT,
                Allowed INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS IX_AuditEvents_Timestamp ON AuditEvents (Timestamp);
            CREATE INDEX IF NOT EXISTS IX_AuditEvents_Category_Timestamp ON AuditEvents (Category, Timestamp);
        """);

        // FTS5 keyword index over Engrams — kept in sync via triggers. Not modeled in EF; if the
        // bundled SQLite lacks FTS5 (unexpected), probe falls back to a LIKE scan.
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE VIRTUAL TABLE IF NOT EXISTS EngramsFts USING fts5(
                    EngramId UNINDEXED, Content, tokenize='porter unicode61'
                );
                CREATE TRIGGER IF NOT EXISTS trg_engrams_ai AFTER INSERT ON Engrams BEGIN
                    INSERT INTO EngramsFts (EngramId, Content) VALUES (new.Id, new.Content);
                END;
                CREATE TRIGGER IF NOT EXISTS trg_engrams_ad AFTER DELETE ON Engrams BEGIN
                    DELETE FROM EngramsFts WHERE EngramId = old.Id;
                END;
                CREATE TRIGGER IF NOT EXISTS trg_engrams_au AFTER UPDATE OF Content ON Engrams BEGIN
                    DELETE FROM EngramsFts WHERE EngramId = old.Id;
                    INSERT INTO EngramsFts (EngramId, Content) VALUES (new.Id, new.Content);
                END;
            """);
            NoosphereCapabilities.FtsAvailable = true;
        }
        catch (Exception ex)
        {
            NoosphereCapabilities.FtsAvailable = false;
            BridgeLogger.Log("WARN", $"FTS5 unavailable in local SQLite build — Noosphere keyword probe will fall back to LIKE scans ({ex.Message})");
        }

        // Layer B: load the persisted enforcement toggle into the in-memory cache (defaults ON when
        // no row exists yet). Done while the connection is still open.
        await Aria.Bridge.Infrastructure.ContextGrantStore.LoadEnforcementAsync(db);

        db.Database.CloseConnection();

        // F-7: one-time migration — load and save every entity that carries encrypted properties so
        // plaintext values are re-persisted through the EF value converter. Skipped on later runs.
        await MigrateEncryptedColumnsAsync(app);

        // F-10: one-time migration for cloud LLM API keys stored in the raw-SQL LlmKeys table.
        // Legacy plaintext values are encrypted under the bridge vault DEK on first run.
        await MigrateLlmKeysAsync(app);

        BridgeLogger.Log("INFO", $"Local vault: {DbPath}");
    }

    private static readonly string EncryptionMigrationMarkerPath = Path.Combine(
        BridgeDataDir.Resolve(), ".vault-encryption-migrated");

    private static readonly string LlmKeyEncryptionMigrationMarkerPath = Path.Combine(
        BridgeDataDir.Resolve(), ".llm-keys-encryption-migrated");

    private static async Task MigrateEncryptedColumnsAsync(WebApplication app)
    {
        if (File.Exists(EncryptionMigrationMarkerPath)) return;

        try
        {
            using var scope = app.Services.CreateScope();
            var vault = scope.ServiceProvider.GetService<Aria.Bridge.Services.Vault.VaultEncryption>();
            if (vault is null) return;

            await using var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
            var souls = await db.Souls.ToListAsync();
            var tokens = await db.OAuthTokens.ToListAsync();

            if (souls.Count > 0 || tokens.Count > 0)
            {
                // Touching the entities is enough: EF tracks them and SaveChanges writes encrypted values.
                db.Souls.UpdateRange(souls);
                db.OAuthTokens.UpdateRange(tokens);
                await db.SaveChangesAsync();
                BridgeLogger.Log("INFO", $"Vault encryption migration complete ({souls.Count} souls, {tokens.Count} tokens)");
            }

            File.WriteAllText(EncryptionMigrationMarkerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            BridgeLogger.Log("ERROR", $"Vault encryption migration failed: {ex.Message}");
            throw;
        }
    }

    private static async Task MigrateLlmKeysAsync(WebApplication app)
    {
        if (File.Exists(LlmKeyEncryptionMigrationMarkerPath)) return;

        try
        {
            using var scope = app.Services.CreateScope();
            var vault = scope.ServiceProvider.GetService<Aria.Bridge.Services.Vault.VaultEncryption>();
            if (vault is null) return;

            await using var db = scope.ServiceProvider.GetRequiredService<BridgeDbContext>();
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            var rows = new List<(string Provider, string KeyB64)>();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Provider, KeyB64 FROM LlmKeys;";
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    rows.Add((r.GetString(0), r.GetString(1)));
            }
            finally { await conn.CloseAsync(); }

            if (rows.Count > 0)
            {
                await conn.OpenAsync();
                try
                {
                    foreach (var (provider, keyB64) in rows)
                    {
                        // Decrypt returns plaintext for legacy values; Encrypt stores it under the DEK.
                        var plaintext = vault.Decrypt(keyB64) ?? keyB64;
                        var encrypted = vault.Encrypt(plaintext);

                        await using var cmd = conn.CreateCommand();
                        cmd.CommandText = "UPDATE LlmKeys SET KeyB64 = @k WHERE Provider = @p;";
                        var pParam = cmd.CreateParameter();
                        pParam.ParameterName = "@p";
                        pParam.Value = provider;
                        cmd.Parameters.Add(pParam);

                        var kParam = cmd.CreateParameter();
                        kParam.ParameterName = "@k";
                        kParam.Value = encrypted;
                        cmd.Parameters.Add(kParam);

                        await cmd.ExecuteNonQueryAsync();
                    }
                }
                finally { await conn.CloseAsync(); }

                BridgeLogger.Log("INFO", $"LLM key encryption migration complete ({rows.Count} keys)");
            }

            File.WriteAllText(LlmKeyEncryptionMigrationMarkerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            BridgeLogger.Log("ERROR", $"LLM key encryption migration failed: {ex.Message}");
            throw;
        }
    }
}
