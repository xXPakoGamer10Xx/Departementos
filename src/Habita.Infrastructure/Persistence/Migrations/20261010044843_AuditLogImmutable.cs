using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Habita.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditLogImmutable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE FUNCTION audit_logs_immutable() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'AuditLogs is append-only'; END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER audit_logs_no_update_delete
                BEFORE UPDATE OR DELETE ON ""AuditLogs""
                FOR EACH ROW EXECUTE FUNCTION audit_logs_immutable();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER audit_logs_no_update_delete ON ""AuditLogs"";
                DROP FUNCTION audit_logs_immutable();");
        }
    }
}
