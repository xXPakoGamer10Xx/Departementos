using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Habita.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LeaseNoOverlapConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Two leases in force (Active or Ended) cannot cover the same day of the same unit. A NULL EndDate is open-ended.
            // Enforced by PostgreSQL so concurrent activations cannot both succeed. Requires btree_gist (created in the first migration).
            migrationBuilder.Sql(@"
                ALTER TABLE ""Leases"" ADD CONSTRAINT ""EX_Leases_NoOverlap""
                EXCLUDE USING gist (""UnitId"" WITH =, daterange(""StartDate"", ""EndDate"", '[]') WITH &&)
                WHERE (""Status"" IN ('Active', 'Ended'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Leases"" DROP CONSTRAINT ""EX_Leases_NoOverlap"";");
        }
    }
}
