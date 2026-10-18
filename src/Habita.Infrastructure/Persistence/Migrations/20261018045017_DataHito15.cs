using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Habita.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DataHito15 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GraceDays",
                table: "Settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "GuarantorName",
                table: "Leases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GuarantorPhone",
                table: "Leases",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DepositSettlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    HeldAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DeductionsTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RefundAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SettledOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SettledByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VoidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VoidReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    VoidedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepositSettlements", x => x.Id);
                    table.UniqueConstraint("AK_DepositSettlements_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_DepositSettlements_Amounts", "\"HeldAmount\" > 0 AND \"DeductionsTotal\" >= 0 AND \"RefundAmount\" >= 0 AND \"RefundAmount\" = \"HeldAmount\" - \"DeductionsTotal\"");
                    table.CheckConstraint("CK_DepositSettlements_Void", "(\"VoidedAt\" IS NULL) = (\"VoidReason\" IS NULL) AND (\"VoidedAt\" IS NULL) = (\"VoidedByUserId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_DepositSettlements_AspNetUsers_SettledByUserId",
                        column: x => x.SettledByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DepositSettlements_AspNetUsers_VoidedByUserId",
                        column: x => x.VoidedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DepositSettlements_Leases_OrganizationId_LeaseId",
                        columns: x => new { x.OrganizationId, x.LeaseId },
                        principalTable: "Leases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Condition = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Notes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ReturnCondition = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    ReturnNotes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryItems", x => x.Id);
                    table.CheckConstraint("CK_InventoryItems_Condition", "\"Condition\" IN ('Good','Fair','Poor')");
                    table.CheckConstraint("CK_InventoryItems_Quantity", "\"Quantity\" BETWEEN 1 AND 999");
                    table.CheckConstraint("CK_InventoryItems_ReturnCondition", "\"ReturnCondition\" IS NULL OR \"ReturnCondition\" IN ('Good','Fair','Poor')");
                    table.ForeignKey(
                        name: "FK_InventoryItems_Leases_OrganizationId_LeaseId",
                        columns: x => new { x.OrganizationId, x.LeaseId },
                        principalTable: "Leases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DepositDeductions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SettlementId = table.Column<Guid>(type: "uuid", nullable: false),
                    Concept = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepositDeductions", x => x.Id);
                    table.CheckConstraint("CK_DepositDeductions_Amount", "\"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_DepositDeductions_DepositSettlements_OrganizationId_Settlem~",
                        columns: x => new { x.OrganizationId, x.SettlementId },
                        principalTable: "DepositSettlements",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrganizationSettings_GraceDays",
                table: "Settings",
                sql: "\"GraceDays\" BETWEEN 0 AND 30");

            migrationBuilder.CreateIndex(
                name: "IX_DepositDeductions_OrganizationId_SettlementId",
                table: "DepositDeductions",
                columns: new[] { "OrganizationId", "SettlementId" });

            migrationBuilder.CreateIndex(
                name: "IX_DepositDeductions_SettlementId",
                table: "DepositDeductions",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_DepositSettlements_LeaseId",
                table: "DepositSettlements",
                column: "LeaseId",
                unique: true,
                filter: "\"VoidedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DepositSettlements_OrganizationId_LeaseId",
                table: "DepositSettlements",
                columns: new[] { "OrganizationId", "LeaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_DepositSettlements_SettledByUserId",
                table: "DepositSettlements",
                column: "SettledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DepositSettlements_VoidedByUserId",
                table: "DepositSettlements",
                column: "VoidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_LeaseId_SortOrder",
                table: "InventoryItems",
                columns: new[] { "LeaseId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_OrganizationId_LeaseId",
                table: "InventoryItems",
                columns: new[] { "OrganizationId", "LeaseId" });

            // A deposit settlement can only be voided (with a reason); what it says never changes. Its deductions never change or disappear.
            migrationBuilder.Sql(@"
                CREATE FUNCTION deposit_settlements_protect() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'DepositSettlements cannot be deleted' USING ERRCODE = '23514'; END IF;
                    IF NEW.""LeaseId"" <> OLD.""LeaseId"" OR NEW.""OrganizationId"" <> OLD.""OrganizationId"" OR NEW.""HeldAmount"" <> OLD.""HeldAmount""
                       OR NEW.""DeductionsTotal"" <> OLD.""DeductionsTotal"" OR NEW.""RefundAmount"" <> OLD.""RefundAmount"" OR NEW.""SettledOn"" <> OLD.""SettledOn""
                       OR NEW.""Notes"" IS DISTINCT FROM OLD.""Notes"" OR NEW.""SettledByUserId"" <> OLD.""SettledByUserId"" OR NEW.""CreatedAt"" <> OLD.""CreatedAt"" THEN
                        RAISE EXCEPTION 'The content of a deposit settlement cannot change' USING ERRCODE = '23514';
                    END IF;
                    IF OLD.""VoidedAt"" IS NOT NULL AND (NEW.""VoidedAt"" IS DISTINCT FROM OLD.""VoidedAt"" OR NEW.""VoidReason"" IS DISTINCT FROM OLD.""VoidReason"") THEN
                        RAISE EXCEPTION 'A voided settlement cannot change' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER deposit_settlements_protect_trg BEFORE UPDATE OR DELETE ON ""DepositSettlements"" FOR EACH ROW EXECUTE FUNCTION deposit_settlements_protect();
                CREATE TRIGGER deposit_deductions_immutable_trg BEFORE UPDATE OR DELETE ON ""DepositDeductions"" FOR EACH ROW EXECUTE FUNCTION documents_immutable();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER deposit_deductions_immutable_trg ON ""DepositDeductions"";
                DROP TRIGGER deposit_settlements_protect_trg ON ""DepositSettlements"";
                DROP FUNCTION deposit_settlements_protect();");

            migrationBuilder.DropTable(
                name: "DepositDeductions");

            migrationBuilder.DropTable(
                name: "InventoryItems");

            migrationBuilder.DropTable(
                name: "DepositSettlements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrganizationSettings_GraceDays",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "GraceDays",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "GuarantorName",
                table: "Leases");

            migrationBuilder.DropColumn(
                name: "GuarantorPhone",
                table: "Leases");
        }
    }
}
