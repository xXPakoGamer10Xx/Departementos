using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Habita.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantPortal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReportedByTenantId",
                table: "MaintenanceIssues",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TenantVisible",
                table: "MaintenanceIssueEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceIssues_OrganizationId_ReportedByTenantId",
                table: "MaintenanceIssues",
                columns: new[] { "OrganizationId", "ReportedByTenantId" });

            migrationBuilder.AddForeignKey(
                name: "FK_MaintenanceIssues_Tenants_OrganizationId_ReportedByTenantId",
                table: "MaintenanceIssues",
                columns: new[] { "OrganizationId", "ReportedByTenantId" },
                principalTable: "Tenants",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaintenanceIssues_Tenants_OrganizationId_ReportedByTenantId",
                table: "MaintenanceIssues");

            migrationBuilder.DropIndex(
                name: "IX_MaintenanceIssues_OrganizationId_ReportedByTenantId",
                table: "MaintenanceIssues");

            migrationBuilder.DropColumn(
                name: "ReportedByTenantId",
                table: "MaintenanceIssues");

            migrationBuilder.DropColumn(
                name: "TenantVisible",
                table: "MaintenanceIssueEvents");
        }
    }
}
