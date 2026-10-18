using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Habita.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRentCharges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RentCharges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PeriodYear = table.Column<int>(type: "integer", nullable: true),
                    PeriodMonth = table.Column<int>(type: "integer", nullable: true),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VoidReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    VoidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentCharges", x => x.Id);
                    table.UniqueConstraint("AK_RentCharges_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_RentCharges_Amount_Positive", "\"Amount\" > 0");
                    table.CheckConstraint("CK_RentCharges_PeriodPair", "(\"PeriodYear\" IS NULL) = (\"PeriodMonth\" IS NULL)");
                    table.CheckConstraint("CK_RentCharges_RentHasPeriod", "\"Type\" <> 'Rent' OR (\"PeriodYear\" IS NOT NULL AND \"PeriodMonth\" BETWEEN 1 AND 12)");
                    table.CheckConstraint("CK_RentCharges_Status", "\"Status\" IN ('Open','PartiallyPaid','Paid','Voided')");
                    table.ForeignKey(
                        name: "FK_RentCharges_Leases_OrganizationId_LeaseId",
                        columns: x => new { x.OrganizationId, x.LeaseId },
                        principalTable: "Leases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RentCharges_LeaseId_PeriodYear_PeriodMonth",
                table: "RentCharges",
                columns: new[] { "LeaseId", "PeriodYear", "PeriodMonth" },
                unique: true,
                filter: "\"Type\" = 'Rent' AND \"Status\" <> 'Voided'");

            migrationBuilder.CreateIndex(
                name: "IX_RentCharges_OrganizationId_DueDate",
                table: "RentCharges",
                columns: new[] { "OrganizationId", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_RentCharges_OrganizationId_LeaseId",
                table: "RentCharges",
                columns: new[] { "OrganizationId", "LeaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_RentCharges_OrganizationId_Status",
                table: "RentCharges",
                columns: new[] { "OrganizationId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RentCharges");
        }
    }
}
