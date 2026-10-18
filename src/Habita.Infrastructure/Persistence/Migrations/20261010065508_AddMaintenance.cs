using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Habita.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Units_PropertyId_Id",
                table: "Units",
                columns: new[] { "PropertyId", "Id" });

            // xmin is PostgreSQL's built-in system column used as the concurrency token: it must not be created.
            migrationBuilder.CreateTable(
                name: "MaintenanceIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceIssues", x => x.Id);
                    table.UniqueConstraint("AK_MaintenanceIssues_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_MaintenanceIssues_Priority", "\"Priority\" IN ('Low','Normal','High','Urgent')");
                    table.CheckConstraint("CK_MaintenanceIssues_Resolution", "(\"Status\" = 'Resolved' AND \"ResolvedAt\" IS NOT NULL AND \"ResolvedByUserId\" IS NOT NULL) OR (\"Status\" IN ('Open','InProgress','Waiting') AND \"ResolvedAt\" IS NULL AND \"ResolvedByUserId\" IS NULL) OR \"Status\" = 'Closed'");
                    table.CheckConstraint("CK_MaintenanceIssues_Status", "\"Status\" IN ('Open','InProgress','Waiting','Resolved','Closed')");
                    table.ForeignKey(
                        name: "FK_MaintenanceIssues_AspNetUsers_AssignedToUserId",
                        column: x => x.AssignedToUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceIssues_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceIssues_AspNetUsers_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceIssues_Properties_OrganizationId_PropertyId",
                        columns: x => new { x.OrganizationId, x.PropertyId },
                        principalTable: "Properties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaintenanceIssues_Units_PropertyId_UnitId",
                        columns: x => new { x.PropertyId, x.UnitId },
                        principalTable: "Units",
                        principalColumns: new[] { "PropertyId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceIssueEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceIssueEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceIssueEvents_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceIssueEvents_MaintenanceIssues_OrganizationId_Iss~",
                        columns: x => new { x.OrganizationId, x.IssueId },
                        principalTable: "MaintenanceIssues",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceIssueEvents_ActorUserId",
                table: "MaintenanceIssueEvents",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceIssueEvents_IssueId_CreatedAt",
                table: "MaintenanceIssueEvents",
                columns: new[] { "IssueId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceIssueEvents_OrganizationId_IssueId",
                table: "MaintenanceIssueEvents",
                columns: new[] { "OrganizationId", "IssueId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceIssues_AssignedToUserId",
                table: "MaintenanceIssues",
                column: "AssignedToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceIssues_CreatedByUserId",
                table: "MaintenanceIssues",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceIssues_OrganizationId_PropertyId",
                table: "MaintenanceIssues",
                columns: new[] { "OrganizationId", "PropertyId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceIssues_OrganizationId_Status",
                table: "MaintenanceIssues",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceIssues_PropertyId",
                table: "MaintenanceIssues",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceIssues_PropertyId_UnitId",
                table: "MaintenanceIssues",
                columns: new[] { "PropertyId", "UnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceIssues_ResolvedByUserId",
                table: "MaintenanceIssues",
                column: "ResolvedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceIssueEvents");

            migrationBuilder.DropTable(
                name: "MaintenanceIssues");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Units_PropertyId_Id",
                table: "Units");
        }
    }
}
