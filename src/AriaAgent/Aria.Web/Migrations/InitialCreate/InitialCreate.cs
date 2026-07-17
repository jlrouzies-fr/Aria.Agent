using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aria.Web.Migrations.InitialCreate
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModelFormatCaches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EndpointUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    ThinkingFormat = table.Column<string>(type: "TEXT", nullable: false),
                    ToolCallFormat = table.Column<string>(type: "TEXT", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelFormatCaches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserOAuthTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    AccessToken = table.Column<string>(type: "TEXT", nullable: false),
                    RefreshToken = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserOAuthTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    LastModelSource = table.Column<string>(type: "TEXT", nullable: true),
                    AvatarSpriteKey = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserVoxSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    TranscriptionChannelName = table.Column<string>(type: "TEXT", nullable: true),
                    FixingChannelName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserVoxSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WargameFactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    Race = table.Column<int>(type: "INTEGER", nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", nullable: true),
                    CompactedContext = table.Column<string>(type: "TEXT", nullable: true),
                    TurnCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsAlive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Wood = table.Column<int>(type: "INTEGER", nullable: false),
                    Stone = table.Column<int>(type: "INTEGER", nullable: false),
                    Food = table.Column<int>(type: "INTEGER", nullable: false),
                    Gold = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WargameFactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WargameMaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    Seed = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentTurn = table.Column<int>(type: "INTEGER", nullable: false),
                    IsRunning = table.Column<bool>(type: "INTEGER", nullable: false),
                    TurnIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WargameMaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Skills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MarkdownContent = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Skills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Skills_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubAgents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    GeneratedName = table.Column<string>(type: "TEXT", nullable: false),
                    ArchetypeName = table.Column<string>(type: "TEXT", nullable: false),
                    GeneratedPersonality = table.Column<string>(type: "TEXT", nullable: false),
                    UserDirectives = table.Column<string>(type: "TEXT", nullable: true),
                    AccentColor = table.Column<string>(type: "TEXT", nullable: false),
                    ModelSourceName = table.Column<string>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", nullable: true),
                    EnabledMcpNamesJson = table.Column<string>(type: "TEXT", nullable: true),
                    AvatarSpriteKey = table.Column<string>(type: "TEXT", nullable: true),
                    Nickname = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubAgents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubAgents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserLlmApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderName = table.Column<string>(type: "TEXT", nullable: false),
                    KeyB64 = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLlmApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserLlmApiKeys_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserMcpServers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Transport = table.Column<int>(type: "INTEGER", nullable: false),
                    Command = table.Column<string>(type: "TEXT", nullable: false),
                    ArgsJson = table.Column<string>(type: "TEXT", nullable: false),
                    EnvJson = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserMcpServers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserMcpServers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSourcePreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSourcePreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSourcePreferences_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserToolConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserToolConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserToolConfigs_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WargameBuildings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FactionId = table.Column<int>(type: "INTEGER", nullable: false),
                    X = table.Column<int>(type: "INTEGER", nullable: false),
                    Y = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    BuiltTurn = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WargameBuildings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WargameBuildings_WargameFactions_FactionId",
                        column: x => x.FactionId,
                        principalTable: "WargameFactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WargameTurnLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FactionId = table.Column<int>(type: "INTEGER", nullable: false),
                    TurnNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionJson = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WargameTurnLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WargameTurnLogs_WargameFactions_FactionId",
                        column: x => x.FactionId,
                        principalTable: "WargameFactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WargameUnits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FactionId = table.Column<int>(type: "INTEGER", nullable: false),
                    X = table.Column<int>(type: "INTEGER", nullable: false),
                    Y = table.Column<int>(type: "INTEGER", nullable: false),
                    Health = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxHealth = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WargameUnits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WargameUnits_WargameFactions_FactionId",
                        column: x => x.FactionId,
                        principalTable: "WargameFactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WargameTiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    X = table.Column<int>(type: "INTEGER", nullable: false),
                    Y = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerFactionId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WargameTiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WargameTiles_WargameFactions_OwnerFactionId",
                        column: x => x.OwnerFactionId,
                        principalTable: "WargameFactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WargameTiles_WargameMaps_MapId",
                        column: x => x.MapId,
                        principalTable: "WargameMaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentCronJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    SubAgentId = table.Column<int>(type: "INTEGER", nullable: true),
                    CogitationId = table.Column<int>(type: "INTEGER", nullable: true),
                    TaskPrompt = table.Column<string>(type: "TEXT", nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", nullable: true),
                    ScheduledDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ScheduledHour = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResultSummary = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCronJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentCronJobs_SubAgents_SubAgentId",
                        column: x => x.SubAgentId,
                        principalTable: "SubAgents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AgentCronJobs_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Cogitations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubAgentId = table.Column<int>(type: "INTEGER", nullable: true),
                    AriaAvatarKey = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cogitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cogitations_SubAgents_SubAgentId",
                        column: x => x.SubAgentId,
                        principalTable: "SubAgents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Cogitations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubAgentSkills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubAgentId = table.Column<int>(type: "INTEGER", nullable: false),
                    SkillId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubAgentSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubAgentSkills_Skills_SkillId",
                        column: x => x.SkillId,
                        principalTable: "Skills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SubAgentSkills_SubAgents_SubAgentId",
                        column: x => x.SubAgentId,
                        principalTable: "SubAgents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubAgentToolStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubAgentId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubAgentToolStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubAgentToolStates_SubAgents_SubAgentId",
                        column: x => x.SubAgentId,
                        principalTable: "SubAgents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CogitationMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CogitationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    ThinkingContent = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CogitationMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CogitationMessages_Cogitations_CogitationId",
                        column: x => x.CogitationId,
                        principalTable: "Cogitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCronJobs_SubAgentId",
                table: "AgentCronJobs",
                column: "SubAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCronJobs_UserId",
                table: "AgentCronJobs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CogitationMessages_CogitationId",
                table: "CogitationMessages",
                column: "CogitationId");

            migrationBuilder.CreateIndex(
                name: "IX_Cogitations_SubAgentId",
                table: "Cogitations",
                column: "SubAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_Cogitations_UserId",
                table: "Cogitations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelFormatCaches_EndpointUrl_ModelId",
                table: "ModelFormatCaches",
                columns: new[] { "EndpointUrl", "ModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Skills_UserId",
                table: "Skills",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SubAgents_UserId",
                table: "SubAgents",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SubAgentSkills_SkillId",
                table: "SubAgentSkills",
                column: "SkillId");

            migrationBuilder.CreateIndex(
                name: "IX_SubAgentSkills_SubAgentId_SkillId",
                table: "SubAgentSkills",
                columns: new[] { "SubAgentId", "SkillId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubAgentToolStates_SubAgentId_ToolId",
                table: "SubAgentToolStates",
                columns: new[] { "SubAgentId", "ToolId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserLlmApiKeys_UserId_ProviderName",
                table: "UserLlmApiKeys",
                columns: new[] { "UserId", "ProviderName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserMcpServers_UserId",
                table: "UserMcpServers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserOAuthTokens_UserId_Provider",
                table: "UserOAuthTokens",
                columns: new[] { "UserId", "Provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Name",
                table: "Users",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSourcePreferences_UserId_SourceName",
                table: "UserSourcePreferences",
                columns: new[] { "UserId", "SourceName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserToolConfigs_UserId_ToolId",
                table: "UserToolConfigs",
                columns: new[] { "UserId", "ToolId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserVoxSettings_UserId",
                table: "UserVoxSettings",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WargameBuildings_FactionId",
                table: "WargameBuildings",
                column: "FactionId");

            migrationBuilder.CreateIndex(
                name: "IX_WargameTiles_MapId",
                table: "WargameTiles",
                column: "MapId");

            migrationBuilder.CreateIndex(
                name: "IX_WargameTiles_OwnerFactionId",
                table: "WargameTiles",
                column: "OwnerFactionId");

            migrationBuilder.CreateIndex(
                name: "IX_WargameTurnLogs_FactionId",
                table: "WargameTurnLogs",
                column: "FactionId");

            migrationBuilder.CreateIndex(
                name: "IX_WargameUnits_FactionId",
                table: "WargameUnits",
                column: "FactionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentCronJobs");

            migrationBuilder.DropTable(
                name: "CogitationMessages");

            migrationBuilder.DropTable(
                name: "ModelFormatCaches");

            migrationBuilder.DropTable(
                name: "SubAgentSkills");

            migrationBuilder.DropTable(
                name: "SubAgentToolStates");

            migrationBuilder.DropTable(
                name: "UserLlmApiKeys");

            migrationBuilder.DropTable(
                name: "UserMcpServers");

            migrationBuilder.DropTable(
                name: "UserOAuthTokens");

            migrationBuilder.DropTable(
                name: "UserSourcePreferences");

            migrationBuilder.DropTable(
                name: "UserToolConfigs");

            migrationBuilder.DropTable(
                name: "UserVoxSettings");

            migrationBuilder.DropTable(
                name: "WargameBuildings");

            migrationBuilder.DropTable(
                name: "WargameTiles");

            migrationBuilder.DropTable(
                name: "WargameTurnLogs");

            migrationBuilder.DropTable(
                name: "WargameUnits");

            migrationBuilder.DropTable(
                name: "Cogitations");

            migrationBuilder.DropTable(
                name: "Skills");

            migrationBuilder.DropTable(
                name: "WargameMaps");

            migrationBuilder.DropTable(
                name: "WargameFactions");

            migrationBuilder.DropTable(
                name: "SubAgents");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
