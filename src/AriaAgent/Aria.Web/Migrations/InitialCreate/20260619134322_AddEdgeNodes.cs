using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aria.Web.Migrations.InitialCreate
{
    /// <inheritdoc />
    public partial class AddEdgeNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Timezone",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSeenByUser",
                table: "AgentCronJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TargetCogitationId",
                table: "AgentCronJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentCollectives",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Objective = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    OvermindSubAgentId = table.Column<int>(type: "INTEGER", nullable: true),
                    OvermindSourceName = table.Column<string>(type: "TEXT", nullable: true),
                    OvermindModelId = table.Column<string>(type: "TEXT", nullable: true),
                    MaxRounds = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentRound = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultSummary = table.Column<string>(type: "TEXT", nullable: true),
                    LastFeedback = table.Column<string>(type: "TEXT", nullable: true),
                    SynapseMemory = table.Column<string>(type: "TEXT", nullable: true),
                    RequiresHumanApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanvasZoom = table.Column<double>(type: "REAL", nullable: false),
                    CanvasPanX = table.Column<double>(type: "REAL", nullable: false),
                    CanvasPanY = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCollectives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentCollectives_SubAgents_OvermindSubAgentId",
                        column: x => x.OvermindSubAgentId,
                        principalTable: "SubAgents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AgentCollectives_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserLocalSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    ModelsJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsBridged = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLocalSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserLocalSources_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CollectiveMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CollectiveId = table.Column<int>(type: "INTEGER", nullable: false),
                    SubAgentId = table.Column<int>(type: "INTEGER", nullable: false),
                    RoleLabel = table.Column<string>(type: "TEXT", nullable: true),
                    CanvasX = table.Column<double>(type: "REAL", nullable: false),
                    CanvasY = table.Column<double>(type: "REAL", nullable: false),
                    RequiresHumanApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectiveMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CollectiveMembers_AgentCollectives_CollectiveId",
                        column: x => x.CollectiveId,
                        principalTable: "AgentCollectives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CollectiveMembers_SubAgents_SubAgentId",
                        column: x => x.SubAgentId,
                        principalTable: "SubAgents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CollectiveEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CollectiveId = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorMemberId = table.Column<int>(type: "INTEGER", nullable: true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectiveEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CollectiveEvents_AgentCollectives_CollectiveId",
                        column: x => x.CollectiveId,
                        principalTable: "AgentCollectives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CollectiveEvents_CollectiveMembers_ActorMemberId",
                        column: x => x.ActorMemberId,
                        principalTable: "CollectiveMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CollectiveTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CollectiveId = table.Column<int>(type: "INTEGER", nullable: false),
                    AssignedMemberId = table.Column<int>(type: "INTEGER", nullable: true),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Instruction = table.Column<string>(type: "TEXT", nullable: false),
                    DependsOnJson = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: true),
                    CogitationId = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectiveTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CollectiveTasks_AgentCollectives_CollectiveId",
                        column: x => x.CollectiveId,
                        principalTable: "AgentCollectives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CollectiveTasks_CollectiveMembers_AssignedMemberId",
                        column: x => x.AssignedMemberId,
                        principalTable: "CollectiveMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MemberEdgeNodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MemberId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    NodeType = table.Column<int>(type: "INTEGER", nullable: false),
                    Config = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberEdgeNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemberEdgeNodes_CollectiveMembers_MemberId",
                        column: x => x.MemberId,
                        principalTable: "CollectiveMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCollectives_OvermindSubAgentId",
                table: "AgentCollectives",
                column: "OvermindSubAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCollectives_UserId",
                table: "AgentCollectives",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectiveEvents_ActorMemberId",
                table: "CollectiveEvents",
                column: "ActorMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectiveEvents_CollectiveId",
                table: "CollectiveEvents",
                column: "CollectiveId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectiveMembers_CollectiveId",
                table: "CollectiveMembers",
                column: "CollectiveId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectiveMembers_SubAgentId",
                table: "CollectiveMembers",
                column: "SubAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectiveTasks_AssignedMemberId",
                table: "CollectiveTasks",
                column: "AssignedMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectiveTasks_CollectiveId",
                table: "CollectiveTasks",
                column: "CollectiveId");

            migrationBuilder.CreateIndex(
                name: "IX_MemberEdgeNodes_MemberId",
                table: "MemberEdgeNodes",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_UserLocalSources_UserId_Name",
                table: "UserLocalSources",
                columns: new[] { "UserId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectiveEvents");

            migrationBuilder.DropTable(
                name: "CollectiveTasks");

            migrationBuilder.DropTable(
                name: "MemberEdgeNodes");

            migrationBuilder.DropTable(
                name: "UserLocalSources");

            migrationBuilder.DropTable(
                name: "CollectiveMembers");

            migrationBuilder.DropTable(
                name: "AgentCollectives");

            migrationBuilder.DropColumn(
                name: "Timezone",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsSeenByUser",
                table: "AgentCronJobs");

            migrationBuilder.DropColumn(
                name: "TargetCogitationId",
                table: "AgentCronJobs");
        }
    }
}
