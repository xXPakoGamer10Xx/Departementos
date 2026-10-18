using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Habita.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentsHito14 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContractTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fields = table.Column<string[]>(type: "text[]", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractTemplates", x => x.Id);
                    table.UniqueConstraint("AK_ContractTemplates_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_ContractTemplates_Retired", "(\"IsActive\") = (\"RetiredAt\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_ContractTemplates_AspNetUsers_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContractTemplates_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContractTemplates_StoredFiles_OrganizationId_FileId",
                        columns: x => new { x.OrganizationId, x.FileId },
                        principalTable: "StoredFiles",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceiptCounters",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastFolio = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiptCounters", x => x.OrganizationId);
                    table.CheckConstraint("CK_ReceiptCounters_Last", "\"LastFolio\" >= 0");
                    table.ForeignKey(
                        name: "FK_ReceiptCounters_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Receipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Folio = table.Column<int>(type: "integer", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Receipts", x => x.Id);
                    table.CheckConstraint("CK_Receipts_Folio", "\"Folio\" > 0");
                    table.ForeignKey(
                        name: "FK_Receipts_Payments_OrganizationId_PaymentId",
                        columns: x => new { x.OrganizationId, x.PaymentId },
                        principalTable: "Payments",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeaseDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocxFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    PdfFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeneratedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseDocuments", x => x.Id);
                    table.CheckConstraint("CK_LeaseDocuments_Version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_LeaseDocuments_AspNetUsers_GeneratedByUserId",
                        column: x => x.GeneratedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseDocuments_ContractTemplates_OrganizationId_TemplateId",
                        columns: x => new { x.OrganizationId, x.TemplateId },
                        principalTable: "ContractTemplates",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseDocuments_Leases_OrganizationId_LeaseId",
                        columns: x => new { x.OrganizationId, x.LeaseId },
                        principalTable: "Leases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseDocuments_StoredFiles_OrganizationId_DocxFileId",
                        columns: x => new { x.OrganizationId, x.DocxFileId },
                        principalTable: "StoredFiles",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeaseDocuments_StoredFiles_OrganizationId_PdfFileId",
                        columns: x => new { x.OrganizationId, x.PdfFileId },
                        principalTable: "StoredFiles",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractTemplates_OrganizationId",
                table: "ContractTemplates",
                column: "OrganizationId",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_ContractTemplates_OrganizationId_FileId",
                table: "ContractTemplates",
                columns: new[] { "OrganizationId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContractTemplates_UploadedByUserId",
                table: "ContractTemplates",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseDocuments_DocxFileId",
                table: "LeaseDocuments",
                column: "DocxFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseDocuments_GeneratedByUserId",
                table: "LeaseDocuments",
                column: "GeneratedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseDocuments_LeaseId_Version",
                table: "LeaseDocuments",
                columns: new[] { "LeaseId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseDocuments_OrganizationId_DocxFileId",
                table: "LeaseDocuments",
                columns: new[] { "OrganizationId", "DocxFileId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseDocuments_OrganizationId_LeaseId",
                table: "LeaseDocuments",
                columns: new[] { "OrganizationId", "LeaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseDocuments_OrganizationId_PdfFileId",
                table: "LeaseDocuments",
                columns: new[] { "OrganizationId", "PdfFileId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseDocuments_OrganizationId_TemplateId",
                table: "LeaseDocuments",
                columns: new[] { "OrganizationId", "TemplateId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeaseDocuments_PdfFileId",
                table: "LeaseDocuments",
                column: "PdfFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_OrganizationId_Folio",
                table: "Receipts",
                columns: new[] { "OrganizationId", "Folio" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_OrganizationId_PaymentId",
                table: "Receipts",
                columns: new[] { "OrganizationId", "PaymentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_PaymentId",
                table: "Receipts",
                column: "PaymentId",
                unique: true);

            // Payments that already exist get their receipt number now, in the order they were recorded, one sequence per organization.
            migrationBuilder.Sql(@"
                INSERT INTO ""Receipts"" (""Id"", ""OrganizationId"", ""PaymentId"", ""Folio"", ""IssuedAt"")
                SELECT gen_random_uuid(), ""OrganizationId"", ""Id"", ROW_NUMBER() OVER (PARTITION BY ""OrganizationId"" ORDER BY ""CreatedAt"", ""Id""), ""CreatedAt""
                FROM ""Payments"";
                INSERT INTO ""ReceiptCounters"" (""OrganizationId"", ""LastFolio"")
                SELECT ""OrganizationId"", MAX(""Folio"") FROM ""Receipts"" GROUP BY ""OrganizationId"";");

            // A receipt number and a generated contract are history: neither can be changed or erased. A template can only be retired (its content is fixed).
            migrationBuilder.Sql(@"
                CREATE FUNCTION documents_immutable() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION '% is append-only', TG_TABLE_NAME USING ERRCODE = '23514'; END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER receipts_immutable_trg BEFORE UPDATE OR DELETE ON ""Receipts"" FOR EACH ROW EXECUTE FUNCTION documents_immutable();
                CREATE TRIGGER lease_documents_immutable_trg BEFORE UPDATE OR DELETE ON ""LeaseDocuments"" FOR EACH ROW EXECUTE FUNCTION documents_immutable();

                CREATE FUNCTION contract_templates_protect() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'ContractTemplates cannot be deleted' USING ERRCODE = '23514'; END IF;
                    IF NEW.""FileId"" <> OLD.""FileId"" OR NEW.""Name"" <> OLD.""Name"" OR NEW.""Fields"" <> OLD.""Fields""
                       OR NEW.""OrganizationId"" <> OLD.""OrganizationId"" OR NEW.""CreatedAt"" <> OLD.""CreatedAt"" THEN
                        RAISE EXCEPTION 'The content of a contract template cannot change' USING ERRCODE = '23514';
                    END IF;
                    IF OLD.""IsActive"" = FALSE AND NEW.""IsActive"" = TRUE THEN
                        RAISE EXCEPTION 'A retired contract template cannot be reactivated' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER contract_templates_protect_trg BEFORE UPDATE OR DELETE ON ""ContractTemplates"" FOR EACH ROW EXECUTE FUNCTION contract_templates_protect();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER contract_templates_protect_trg ON ""ContractTemplates"";
                DROP FUNCTION contract_templates_protect();
                DROP TRIGGER lease_documents_immutable_trg ON ""LeaseDocuments"";
                DROP TRIGGER receipts_immutable_trg ON ""Receipts"";
                DROP FUNCTION documents_immutable();");

            migrationBuilder.DropTable(
                name: "LeaseDocuments");

            migrationBuilder.DropTable(
                name: "ReceiptCounters");

            migrationBuilder.DropTable(
                name: "Receipts");

            migrationBuilder.DropTable(
                name: "ContractTemplates");
        }
    }
}
