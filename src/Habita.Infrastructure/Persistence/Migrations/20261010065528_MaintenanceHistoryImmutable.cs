using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Habita.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceHistoryImmutable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The status/comment history is append-only: nobody (not even the application) can rewrite or erase it.
            migrationBuilder.Sql(@"
                CREATE FUNCTION maintenance_issue_events_immutable() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'MaintenanceIssueEvents is append-only' USING ERRCODE = '23514'; END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER maintenance_issue_events_no_update_delete
                BEFORE UPDATE OR DELETE ON ""MaintenanceIssueEvents""
                FOR EACH ROW EXECUTE FUNCTION maintenance_issue_events_immutable();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER maintenance_issue_events_no_update_delete ON ""MaintenanceIssueEvents"";
                DROP FUNCTION maintenance_issue_events_immutable();");
        }
    }
}
