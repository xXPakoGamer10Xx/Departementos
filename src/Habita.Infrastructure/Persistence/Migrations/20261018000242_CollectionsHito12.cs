using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Habita.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CollectionsHito12 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropertyId = table.Column<Guid>(type: "uuid", nullable: true),
                    HolderName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BankName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Clabe = table.Column<string>(type: "character varying(18)", maxLength: 18, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAccounts", x => x.Id);
                    table.CheckConstraint("CK_BankAccounts_Clabe", "\"Clabe\" ~ '^[0-9]{18}$'");
                    table.CheckConstraint("CK_BankAccounts_DefaultIsGeneral", "NOT \"IsDefault\" OR (\"PropertyId\" IS NULL AND \"Status\" = 'Active')");
                    table.CheckConstraint("CK_BankAccounts_Status", "\"Status\" IN ('Active','Archived')");
                    table.ForeignKey(
                        name: "FK_BankAccounts_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BankAccounts_Properties_OrganizationId_PropertyId",
                        columns: x => new { x.OrganizationId, x.PropertyId },
                        principalTable: "Properties",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CashTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UsedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashTokens", x => x.Id);
                    table.CheckConstraint("CK_CashTokens_Used", "(\"UsedAt\" IS NULL) = (\"PaymentId\" IS NULL) AND (\"UsedAt\" IS NULL) = (\"UsedByUserId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_CashTokens_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashTokens_AspNetUsers_UsedByUserId",
                        column: x => x.UsedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashTokens_Payments_OrganizationId_PaymentId",
                        columns: x => new { x.OrganizationId, x.PaymentId },
                        principalTable: "Payments",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashTokens_Tenants_OrganizationId_TenantId",
                        columns: x => new { x.OrganizationId, x.TenantId },
                        principalTable: "Tenants",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TenantNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SubmittedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewComment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentClaims", x => x.Id);
                    table.CheckConstraint("CK_PaymentClaims_Amount_Positive", "\"Amount\" > 0");
                    table.CheckConstraint("CK_PaymentClaims_Method", "\"Method\" IN ('Cash','BankTransfer','Other')");
                    table.CheckConstraint("CK_PaymentClaims_PaymentLink", "(\"Status\" = 'Confirmed') = (\"PaymentId\" IS NOT NULL)");
                    table.CheckConstraint("CK_PaymentClaims_RejectionComment", "\"Status\" <> 'Rejected' OR \"ReviewComment\" IS NOT NULL");
                    table.CheckConstraint("CK_PaymentClaims_Review", "\"Status\" IN ('Pending','Cancelled') OR (\"ReviewedAt\" IS NOT NULL AND \"ReviewedByUserId\" IS NOT NULL)");
                    table.CheckConstraint("CK_PaymentClaims_Status", "\"Status\" IN ('Pending','Confirmed','Rejected','Cancelled')");
                    table.ForeignKey(
                        name: "FK_PaymentClaims_AspNetUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentClaims_AspNetUsers_SubmittedByUserId",
                        column: x => x.SubmittedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentClaims_Payments_OrganizationId_PaymentId",
                        columns: x => new { x.OrganizationId, x.PaymentId },
                        principalTable: "Payments",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentClaims_StoredFiles_OrganizationId_FileId",
                        columns: x => new { x.OrganizationId, x.FileId },
                        principalTable: "StoredFiles",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentClaims_Tenants_OrganizationId_TenantId",
                        columns: x => new { x.OrganizationId, x.TenantId },
                        principalTable: "Tenants",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentPromises",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChargeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PromisedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentPromises", x => x.Id);
                    table.CheckConstraint("CK_PaymentPromises_CancelPair", "(\"CancelledAt\" IS NULL) = (\"CancelReason\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_PaymentPromises_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentPromises_RentCharges_OrganizationId_ChargeId",
                        columns: x => new { x.OrganizationId, x.ChargeId },
                        principalTable: "RentCharges",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaymentPromises_Tenants_OrganizationId_TenantId",
                        columns: x => new { x.OrganizationId, x.TenantId },
                        principalTable: "Tenants",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_OrganizationId",
                table: "BankAccounts",
                column: "OrganizationId",
                unique: true,
                filter: "\"IsDefault\" AND \"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_OrganizationId_PropertyId",
                table: "BankAccounts",
                columns: new[] { "OrganizationId", "PropertyId" },
                unique: true,
                filter: "\"PropertyId\" IS NOT NULL AND \"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_CashTokens_CodeHash",
                table: "CashTokens",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashTokens_CreatedByUserId",
                table: "CashTokens",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CashTokens_OrganizationId_PaymentId",
                table: "CashTokens",
                columns: new[] { "OrganizationId", "PaymentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CashTokens_OrganizationId_TenantId",
                table: "CashTokens",
                columns: new[] { "OrganizationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_CashTokens_PaymentId",
                table: "CashTokens",
                column: "PaymentId",
                unique: true,
                filter: "\"PaymentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CashTokens_TenantId",
                table: "CashTokens",
                column: "TenantId",
                unique: true,
                filter: "\"UsedAt\" IS NULL AND \"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CashTokens_TenantId_CreatedAt",
                table: "CashTokens",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CashTokens_UsedByUserId",
                table: "CashTokens",
                column: "UsedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentClaims_FileId",
                table: "PaymentClaims",
                column: "FileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentClaims_OrganizationId_FileId",
                table: "PaymentClaims",
                columns: new[] { "OrganizationId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentClaims_OrganizationId_PaymentId",
                table: "PaymentClaims",
                columns: new[] { "OrganizationId", "PaymentId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentClaims_OrganizationId_Status_CreatedAt",
                table: "PaymentClaims",
                columns: new[] { "OrganizationId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentClaims_OrganizationId_TenantId",
                table: "PaymentClaims",
                columns: new[] { "OrganizationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentClaims_PaymentId",
                table: "PaymentClaims",
                column: "PaymentId",
                unique: true,
                filter: "\"PaymentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentClaims_ReviewedByUserId",
                table: "PaymentClaims",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentClaims_SubmittedByUserId",
                table: "PaymentClaims",
                column: "SubmittedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentClaims_TenantId_CreatedAt",
                table: "PaymentClaims",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentPromises_ChargeId",
                table: "PaymentPromises",
                column: "ChargeId",
                unique: true,
                filter: "\"CancelledAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentPromises_CreatedByUserId",
                table: "PaymentPromises",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentPromises_OrganizationId_ChargeId",
                table: "PaymentPromises",
                columns: new[] { "OrganizationId", "ChargeId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentPromises_OrganizationId_TenantId",
                table: "PaymentPromises",
                columns: new[] { "OrganizationId", "TenantId" });

            // A payment report that has been confirmed, rejected or cancelled is history: it can never change or disappear.
            // While pending only its status can move, so the amount and the proof the staff are looking at cannot be swapped.
            migrationBuilder.Sql(@"
                CREATE FUNCTION payment_claims_protect() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'PaymentClaims cannot be deleted' USING ERRCODE = '23514'; END IF;
                    IF OLD.""Status"" <> 'Pending' THEN RAISE EXCEPTION 'A reviewed payment claim cannot change' USING ERRCODE = '23514'; END IF;
                    IF NEW.""Amount"" <> OLD.""Amount"" OR NEW.""PaidOn"" <> OLD.""PaidOn"" OR NEW.""FileId"" <> OLD.""FileId""
                       OR NEW.""TenantId"" <> OLD.""TenantId"" OR NEW.""Method"" <> OLD.""Method"" THEN
                        RAISE EXCEPTION 'The content of a payment claim cannot change' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER payment_claims_protect_trg BEFORE UPDATE OR DELETE ON ""PaymentClaims""
                FOR EACH ROW EXECUTE FUNCTION payment_claims_protect();

                CREATE FUNCTION cash_tokens_protect() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'CashTokens cannot be deleted' USING ERRCODE = '23514'; END IF;
                    IF NEW.""CodeHash"" <> OLD.""CodeHash"" OR NEW.""TenantId"" <> OLD.""TenantId"" OR NEW.""ExpiresAt"" <> OLD.""ExpiresAt"" THEN
                        RAISE EXCEPTION 'The content of a cash token cannot change' USING ERRCODE = '23514';
                    END IF;
                    IF (OLD.""UsedAt"" IS NOT NULL AND NEW.""UsedAt"" IS DISTINCT FROM OLD.""UsedAt"")
                       OR (OLD.""RevokedAt"" IS NOT NULL AND NEW.""RevokedAt"" IS DISTINCT FROM OLD.""RevokedAt"")
                       OR (OLD.""PaymentId"" IS NOT NULL AND NEW.""PaymentId"" IS DISTINCT FROM OLD.""PaymentId"") THEN
                        RAISE EXCEPTION 'A used or revoked cash token cannot change' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER cash_tokens_protect_trg BEFORE UPDATE OR DELETE ON ""CashTokens""
                FOR EACH ROW EXECUTE FUNCTION cash_tokens_protect();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER payment_claims_protect_trg ON ""PaymentClaims"";
                DROP FUNCTION payment_claims_protect();
                DROP TRIGGER cash_tokens_protect_trg ON ""CashTokens"";
                DROP FUNCTION cash_tokens_protect();");

            migrationBuilder.DropTable(
                name: "BankAccounts");

            migrationBuilder.DropTable(
                name: "CashTokens");

            migrationBuilder.DropTable(
                name: "PaymentClaims");

            migrationBuilder.DropTable(
                name: "PaymentPromises");
        }
    }
}
