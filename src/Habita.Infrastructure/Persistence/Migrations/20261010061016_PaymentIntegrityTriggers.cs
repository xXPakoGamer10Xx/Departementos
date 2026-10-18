using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Habita.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentIntegrityTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defense in depth for money. The application already validates and locks; these make the database refuse
            // an invalid state even if some code path (or a person with SQL access) tries to create one.

            // 1. An active allocation can never push a payment or a charge past its amount, must belong to the same tenant,
            //    and needs a confirmed payment and a non-voided charge. The FOR UPDATE locks (payment, then charge: the same order the
            //    application uses) make concurrent inserts wait for each other, so each one sees the other's committed total.
            migrationBuilder.Sql(@"
                CREATE FUNCTION payment_allocations_check_limits() RETURNS trigger AS $$
                DECLARE
                    pay RECORD; ch RECORD; pay_alloc numeric; ch_alloc numeric;
                BEGIN
                    IF NEW.""ReversedAt"" IS NOT NULL THEN RETURN NEW; END IF;

                    SELECT ""Amount"", ""Status"", ""TenantId"" INTO pay FROM ""Payments"" WHERE ""Id"" = NEW.""PaymentId"" FOR UPDATE;
                    IF pay.""Status"" <> 'Confirmed' THEN
                        RAISE EXCEPTION 'allocations require a confirmed payment' USING ERRCODE = '23514';
                    END IF;

                    SELECT c.""Amount"", c.""Status"", l.""TenantId"" INTO ch
                      FROM ""RentCharges"" c JOIN ""Leases"" l ON l.""Id"" = c.""LeaseId""
                     WHERE c.""Id"" = NEW.""RentChargeId"" FOR UPDATE OF c;
                    IF ch.""Status"" = 'Voided' THEN
                        RAISE EXCEPTION 'cannot allocate to a voided charge' USING ERRCODE = '23514';
                    END IF;
                    IF ch.""TenantId"" <> pay.""TenantId"" THEN
                        RAISE EXCEPTION 'a payment can only be allocated to charges of the same tenant' USING ERRCODE = '23514';
                    END IF;

                    SELECT COALESCE(SUM(""Amount""), 0) INTO pay_alloc FROM ""PaymentAllocations"" WHERE ""PaymentId"" = NEW.""PaymentId"" AND ""ReversedAt"" IS NULL;
                    IF pay_alloc > pay.""Amount"" THEN
                        RAISE EXCEPTION 'allocations exceed the payment amount' USING ERRCODE = '23514';
                    END IF;

                    SELECT COALESCE(SUM(""Amount""), 0) INTO ch_alloc FROM ""PaymentAllocations"" WHERE ""RentChargeId"" = NEW.""RentChargeId"" AND ""ReversedAt"" IS NULL;
                    IF ch_alloc > ch.""Amount"" THEN
                        RAISE EXCEPTION 'allocations exceed the charge amount' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER payment_allocations_limits
                AFTER INSERT OR UPDATE ON ""PaymentAllocations""
                FOR EACH ROW EXECUTE FUNCTION payment_allocations_check_limits();");

            // 2. Confirmed payments are never edited or deleted: only the reversal fields may change, once.
            migrationBuilder.Sql(@"
                CREATE FUNCTION payments_protect() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'payments cannot be deleted; reverse them instead' USING ERRCODE = '23514';
                    END IF;
                    IF NEW.""Amount"" <> OLD.""Amount"" OR NEW.""TenantId"" <> OLD.""TenantId"" OR NEW.""OrganizationId"" <> OLD.""OrganizationId""
                       OR NEW.""ReceivedOn"" <> OLD.""ReceivedOn"" OR NEW.""Method"" <> OLD.""Method""
                       OR NEW.""Reference"" IS DISTINCT FROM OLD.""Reference"" OR NEW.""Notes"" IS DISTINCT FROM OLD.""Notes""
                       OR NEW.""IdempotencyKey"" IS DISTINCT FROM OLD.""IdempotencyKey"" OR NEW.""CreatedByUserId"" <> OLD.""CreatedByUserId"" THEN
                        RAISE EXCEPTION 'a confirmed payment cannot be edited; reverse it and register another' USING ERRCODE = '23514';
                    END IF;
                    IF OLD.""Status"" = 'Reversed' AND (NEW.""Status"" <> 'Reversed' OR NEW.""ReversedAt"" IS DISTINCT FROM OLD.""ReversedAt"") THEN
                        RAISE EXCEPTION 'a reversed payment cannot be changed' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER payments_protect_trg
                BEFORE UPDATE OR DELETE ON ""Payments""
                FOR EACH ROW EXECUTE FUNCTION payments_protect();");

            // 3. Allocations are append-only except for a single reversal.
            migrationBuilder.Sql(@"
                CREATE FUNCTION payment_allocations_protect() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'allocations cannot be deleted; reverse them instead' USING ERRCODE = '23514';
                    END IF;
                    IF NEW.""Amount"" <> OLD.""Amount"" OR NEW.""PaymentId"" <> OLD.""PaymentId"" OR NEW.""RentChargeId"" <> OLD.""RentChargeId""
                       OR NEW.""OrganizationId"" <> OLD.""OrganizationId"" OR NEW.""CreatedAt"" <> OLD.""CreatedAt"" THEN
                        RAISE EXCEPTION 'allocations are immutable except for reversal' USING ERRCODE = '23514';
                    END IF;
                    IF OLD.""ReversedAt"" IS NOT NULL AND NEW.""ReversedAt"" IS DISTINCT FROM OLD.""ReversedAt"" THEN
                        RAISE EXCEPTION 'a reversed allocation cannot be changed' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER payment_allocations_protect_trg
                BEFORE UPDATE OR DELETE ON ""PaymentAllocations""
                FOR EACH ROW EXECUTE FUNCTION payment_allocations_protect();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER payment_allocations_protect_trg ON ""PaymentAllocations"";
                DROP FUNCTION payment_allocations_protect();
                DROP TRIGGER payments_protect_trg ON ""Payments"";
                DROP FUNCTION payments_protect();
                DROP TRIGGER payment_allocations_limits ON ""PaymentAllocations"";
                DROP FUNCTION payment_allocations_check_limits();");
        }
    }
}
