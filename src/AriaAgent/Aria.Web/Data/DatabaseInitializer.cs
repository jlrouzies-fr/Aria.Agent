using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Data;

public static class DatabaseInitializer
{
    public static async Task<WebApplication> EnsureAriaDatabaseAsync(this WebApplication app)
    {
        // Ensure DB is created on startup (no migrations needed for this project)
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var ctx = await db.CreateDbContextAsync();
            await ctx.Database.EnsureCreatedAsync();
            var conn = ctx.Database.GetDbConnection();
            await conn.OpenAsync();
            foreach (var sql in new[]
            {
                "ALTER TABLE WargameFactions ADD COLUMN Race      INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE WargameUnits    ADD COLUMN MaxHealth INTEGER NOT NULL DEFAULT 3;",
                "ALTER TABLE WargameFactions ADD COLUMN Wood  INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE WargameFactions ADD COLUMN Stone INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE WargameFactions ADD COLUMN Food  INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE WargameFactions ADD COLUMN Gold  INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE Users           ADD COLUMN Email TEXT;",
                "CREATE TABLE IF NOT EXISTS UserVoxSettings (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER NOT NULL UNIQUE, FixingChannelName TEXT);",
                "ALTER TABLE UserVoxSettings ADD COLUMN TranscriptionChannelName TEXT;",
                "CREATE TABLE IF NOT EXISTS SubAgents (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER NOT NULL, GeneratedName TEXT NOT NULL, ArchetypeName TEXT NOT NULL DEFAULT '', GeneratedPersonality TEXT NOT NULL, UserDirectives TEXT, AccentColor TEXT NOT NULL DEFAULT '#8B0000', ModelSourceName TEXT, ModelId TEXT, EnabledMcpNamesJson TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE);",
                "CREATE TABLE IF NOT EXISTS SubAgentToolStates (Id INTEGER PRIMARY KEY AUTOINCREMENT, SubAgentId INTEGER NOT NULL, ToolId TEXT NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1, UNIQUE(SubAgentId, ToolId), FOREIGN KEY (SubAgentId) REFERENCES SubAgents(Id) ON DELETE CASCADE);",
                "ALTER TABLE Cogitations ADD COLUMN SubAgentId INTEGER REFERENCES SubAgents(Id) ON DELETE SET NULL;",
                "ALTER TABLE SubAgents ADD COLUMN AvatarSpriteKey TEXT;",
                "ALTER TABLE SubAgents ADD COLUMN Nickname TEXT;",
                "CREATE TABLE IF NOT EXISTS Skills (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER NOT NULL, Name TEXT NOT NULL, MarkdownContent TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE);",
                "CREATE TABLE IF NOT EXISTS SubAgentSkills (Id INTEGER PRIMARY KEY AUTOINCREMENT, SubAgentId INTEGER NOT NULL, SkillId INTEGER NOT NULL, UNIQUE(SubAgentId, SkillId), FOREIGN KEY (SubAgentId) REFERENCES SubAgents(Id) ON DELETE CASCADE, FOREIGN KEY (SkillId) REFERENCES Skills(Id) ON DELETE CASCADE);",
                "ALTER TABLE Users ADD COLUMN AvatarSpriteKey TEXT;",
                "ALTER TABLE Cogitations ADD COLUMN AriaAvatarKey TEXT;",
                "ALTER TABLE UserMcpServers ADD COLUMN Transport INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE UserMcpServers ADD COLUMN Url TEXT;",
                "CREATE TABLE IF NOT EXISTS AgentCronJobs (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER NOT NULL, SubAgentId INTEGER, CogitationId INTEGER, TaskPrompt TEXT NOT NULL DEFAULT '', SourceName TEXT, ModelId TEXT, ScheduledDate TEXT NOT NULL, ScheduledHour INTEGER NOT NULL, Status INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), StartedAt TEXT, CompletedAt TEXT, ResultSummary TEXT, ErrorMessage TEXT, FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE, FOREIGN KEY (SubAgentId) REFERENCES SubAgents(Id) ON DELETE SET NULL);",
                "ALTER TABLE AgentCronJobs ADD COLUMN IsSeenByUser INTEGER NOT NULL DEFAULT 1;",
                "ALTER TABLE AgentCronJobs ADD COLUMN TargetCogitationId INTEGER;",
                "ALTER TABLE Users ADD COLUMN Timezone TEXT;",
                "CREATE TABLE IF NOT EXISTS AgentCollectives (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER NOT NULL, Name TEXT NOT NULL, Objective TEXT NOT NULL DEFAULT '', Status INTEGER NOT NULL DEFAULT 0, OvermindSubAgentId INTEGER, OvermindSourceName TEXT, OvermindModelId TEXT, MaxRounds INTEGER NOT NULL DEFAULT 6, CurrentRound INTEGER NOT NULL DEFAULT 0, ResultSummary TEXT, LastFeedback TEXT, CanvasZoom REAL NOT NULL DEFAULT 1, CanvasPanX REAL NOT NULL DEFAULT 0, CanvasPanY REAL NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')), CompletedAt TEXT, FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE, FOREIGN KEY (OvermindSubAgentId) REFERENCES SubAgents(Id) ON DELETE SET NULL);",
                "CREATE TABLE IF NOT EXISTS CollectiveMembers (Id INTEGER PRIMARY KEY AUTOINCREMENT, CollectiveId INTEGER NOT NULL, SubAgentId INTEGER NOT NULL, RoleLabel TEXT, CanvasX REAL NOT NULL DEFAULT 0, CanvasY REAL NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), FOREIGN KEY (CollectiveId) REFERENCES AgentCollectives(Id) ON DELETE CASCADE, FOREIGN KEY (SubAgentId) REFERENCES SubAgents(Id) ON DELETE CASCADE);",
                "CREATE TABLE IF NOT EXISTS CollectiveTasks (Id INTEGER PRIMARY KEY AUTOINCREMENT, CollectiveId INTEGER NOT NULL, AssignedMemberId INTEGER, Round INTEGER NOT NULL DEFAULT 0, Title TEXT NOT NULL DEFAULT '', Instruction TEXT NOT NULL DEFAULT '', DependsOnJson TEXT, Status INTEGER NOT NULL DEFAULT 0, Result TEXT, CogitationId INTEGER, ErrorMessage TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), StartedAt TEXT, CompletedAt TEXT, FOREIGN KEY (CollectiveId) REFERENCES AgentCollectives(Id) ON DELETE CASCADE, FOREIGN KEY (AssignedMemberId) REFERENCES CollectiveMembers(Id) ON DELETE SET NULL);",
                "CREATE TABLE IF NOT EXISTS CollectiveEvents (Id INTEGER PRIMARY KEY AUTOINCREMENT, CollectiveId INTEGER NOT NULL, Timestamp TEXT NOT NULL DEFAULT (datetime('now')), Type INTEGER NOT NULL, ActorMemberId INTEGER, TaskId INTEGER, Message TEXT NOT NULL DEFAULT '', FOREIGN KEY (CollectiveId) REFERENCES AgentCollectives(Id) ON DELETE CASCADE, FOREIGN KEY (ActorMemberId) REFERENCES CollectiveMembers(Id) ON DELETE SET NULL);",
                "ALTER TABLE AgentCollectives ADD COLUMN SynapseMemory TEXT;",
                "ALTER TABLE AgentCollectives ADD COLUMN OriginNodeId TEXT;",
                "ALTER TABLE AgentCollectives ADD COLUMN RequiresHumanApproval INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE CollectiveMembers ADD COLUMN RequiresHumanApproval INTEGER NOT NULL DEFAULT 0;",
                "ALTER TABLE Users ADD COLUMN PublicKey TEXT;",
                "ALTER TABLE Users ADD COLUMN KeepTelemetryExpanded INTEGER NOT NULL DEFAULT 0;",
                // ── Bridge remote nodes (Phase 3): per-soul node allow-list + channel→node pin ──
                "CREATE TABLE IF NOT EXISTS SoulNodeKeys (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER NOT NULL, NodeId TEXT NOT NULL, NodePublicKeyBase64 TEXT NOT NULL, Label TEXT, Platform TEXT, EnrolledByNodeId TEXT, IsPrimary INTEGER NOT NULL DEFAULT 0, Revoked INTEGER NOT NULL DEFAULT 0, EnrolledAt TEXT NOT NULL DEFAULT (datetime('now')), RevokedAt TEXT, LastSeenAt TEXT NOT NULL DEFAULT (datetime('now')), UNIQUE(UserId, NodeId), FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE);",
                // ── Bridge data ownership (Phase 1): which node holds the chat content ──
                "ALTER TABLE Cogitations ADD COLUMN OriginNodeId TEXT;",
                // ── Vigil device pinning: which bridge node executes the vigil ──
                "ALTER TABLE AgentCronJobs ADD COLUMN BridgeNodeId TEXT;",
                // ── Bridge remote nodes (Phase 6): E2E data-sync DEK delivery ──
                "ALTER TABLE SoulNodeKeys ADD COLUMN WrappedDek TEXT;",
                // ── Layer B Phase 2: verifiable enrollment certificate for co-equal approval ──
                "ALTER TABLE SoulNodeKeys ADD COLUMN EnrollmentCertB64 TEXT;",
                "ALTER TABLE SoulNodeKeys ADD COLUMN ApproverPublicKeyBase64 TEXT;",
                "ALTER TABLE SoulNodeKeys ADD COLUMN EnrollmentExpiryUnix INTEGER;",
                // ── Bridge remote nodes (Phase 6): encrypted multi-master sync relay (§11.3). Server stores
                //    CipherBlob opaquely; never decrypts. ──
                "CREATE TABLE IF NOT EXISTS SyncRecords (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER NOT NULL, EntityType TEXT NOT NULL, EntityId TEXT NOT NULL, UpdatedAt TEXT NOT NULL, Deleted INTEGER NOT NULL DEFAULT 0, LastWriterNodeId TEXT NOT NULL DEFAULT '', CipherBlob TEXT NOT NULL DEFAULT '', UNIQUE(UserId, EntityType, EntityId));",
                "CREATE INDEX IF NOT EXISTS IX_SyncRecords_User_Updated ON SyncRecords(UserId, UpdatedAt);",
                "CREATE TABLE IF NOT EXISTS UiAccessKnocks (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, IpAddressProtected TEXT NOT NULL, ExpiresAt TEXT NOT NULL, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')));",
                "CREATE INDEX IF NOT EXISTS IX_UiAccessKnocks_UserId ON UiAccessKnocks(UserId);",
                "CREATE INDEX IF NOT EXISTS IX_UiAccessKnocks_ExpiresAt ON UiAccessKnocks(ExpiresAt);",
                // ── Layer A device trust: a node-signed grant that a browser device was approved (§3). ──
                "CREATE TABLE IF NOT EXISTS TrustedDevices (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, DeviceId TEXT NOT NULL, Label TEXT, LastIp TEXT, SignatureBase64 TEXT NOT NULL, ExpiryUnix INTEGER NOT NULL, ApprovedByNodeId TEXT, Revoked INTEGER NOT NULL DEFAULT 0, RevokedAt TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UNIQUE(UserId, DeviceId));",
                "CREATE INDEX IF NOT EXISTS IX_TrustedDevices_DeviceId ON TrustedDevices(DeviceId);",
                // ── Human-confirmed format detections: an ambiguous probe (no thinking / unknown tools)
                //    is saved only when a human accepts it in the modal, and is then never re-probed. ──
                "ALTER TABLE ModelFormatCaches ADD COLUMN Confirmed INTEGER NOT NULL DEFAULT 0;",
                // ── Wargame factions now carry the owning user so the singleton WargameService can
                //    resolve that user's local/bridged LLM sources (not just the public cloud catalog). ──
                "ALTER TABLE WargameFactions ADD COLUMN UserId TEXT NOT NULL DEFAULT '';",
                // ── Noosphere: native bridge memory replaces the external Hindsight integration.
                //    Preserve each user's enabled/disabled state under the new tool id. ──
                "UPDATE UserToolConfigs SET ToolId = 'memory' WHERE ToolId = 'hindsight' AND NOT EXISTS (SELECT 1 FROM UserToolConfigs t2 WHERE t2.UserId = UserToolConfigs.UserId AND t2.ToolId = 'memory');",
                "DELETE FROM UserToolConfigs WHERE ToolId = 'hindsight';",
                "UPDATE SubAgentToolStates SET ToolId = 'memory' WHERE ToolId = 'hindsight' AND NOT EXISTS (SELECT 1 FROM SubAgentToolStates t2 WHERE t2.SubAgentId = SubAgentToolStates.SubAgentId AND t2.ToolId = 'memory');",
                "DELETE FROM SubAgentToolStates WHERE ToolId = 'hindsight';",
                // ── Hive cogitation branding: link a cogitation back to the collective that ran it,
                //    so Chat can show the Hive's name/avatar/accent instead of the Overmind's own. ──
                "ALTER TABLE Cogitations ADD COLUMN CollectiveId INTEGER REFERENCES AgentCollectives(Id) ON DELETE SET NULL;",
                // ── Cogitation folders ("Dossiers"): user-created groups with optional context defaults. ──
                "CREATE TABLE IF NOT EXISTS CogitationFolders (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId INTEGER NOT NULL, Name TEXT NOT NULL, Color TEXT, SortOrder INTEGER NOT NULL DEFAULT 0, DefaultSubAgentId INTEGER, DefaultProjectPath TEXT, StandingDirective TEXT, FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE);",
                "ALTER TABLE Cogitations ADD COLUMN FolderId INTEGER REFERENCES CogitationFolders(Id) ON DELETE SET NULL;",
                "CREATE INDEX IF NOT EXISTS IX_CogitationFolders_UserId ON CogitationFolders(UserId);",
                "CREATE INDEX IF NOT EXISTS IX_Cogitations_FolderId ON Cogitations(FolderId);",
                "CREATE INDEX IF NOT EXISTS IX_Cogitations_UserId_FolderId_UpdatedAt ON Cogitations(UserId, FolderId, UpdatedAt);",
                "ALTER TABLE Cogitations ADD COLUMN SuggestedFilingDismissed INTEGER NOT NULL DEFAULT 0;",
                // ── Vision capability probe: cached per source/model like Thinking/ToolCall formats,
                //    so the screenshot tool knows whether to hand the image to the model or text-only. ──
                "ALTER TABLE ModelFormatCaches ADD COLUMN VisionSupport TEXT NOT NULL DEFAULT 'Unknown';",
                // ── Inline screenshots: a "screenshot" message stores the captured image so it persists
                //    across refresh and renders in the transcript (bytes are never replayed to the model). ──
                "ALTER TABLE CogitationMessages ADD COLUMN ImageBase64 TEXT;",
                "ALTER TABLE CogitationMessages ADD COLUMN ImageMediaType TEXT;",
                // ── Tool activity sections (diff cards, etc.) — serialized so they survive a reload.
                "ALTER TABLE CogitationMessages ADD COLUMN SectionsJson TEXT;",
            })
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                try { await cmd.ExecuteNonQueryAsync(); } catch { /* column already exists */ }
            }
            await conn.CloseAsync();
        }

        return app;
    }
}
