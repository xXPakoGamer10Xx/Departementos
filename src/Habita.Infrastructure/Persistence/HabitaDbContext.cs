using Habita.Application.Abstractions;
using Habita.Domain.Audit;
using Habita.Domain.Charges;
using Habita.Domain.Collections;
using Habita.Domain.Common;
using Habita.Domain.Deposits;
using Habita.Domain.Documents;
using Habita.Domain.Files;
using Habita.Domain.Leases;
using Habita.Domain.Maintenance;
using Habita.Domain.Notifications;
using Habita.Domain.Organizations;
using Habita.Domain.Payments;
using Habita.Domain.Properties;
using Habita.Domain.Settings;
using Habita.Domain.Tenants;
using Habita.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Habita.Infrastructure.Persistence;

public class HabitaDbContext(DbContextOptions<HabitaDbContext> options, ICurrentOrganization currentOrg)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IAppDbContext
{
    // Evaluated per query: EF parameterizes this member access on the context instance.
    public Guid? CurrentOrganizationId => currentOrg.Id;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Lease> Leases => Set<Lease>();
    public DbSet<RentCharge> RentCharges => Set<RentCharge>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<MaintenanceIssue> MaintenanceIssues => Set<MaintenanceIssue>();
    public DbSet<MaintenanceIssueEvent> MaintenanceIssueEvents => Set<MaintenanceIssueEvent>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<OrganizationSettings> Settings => Set<OrganizationSettings>();
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<AccessCode> AccessCodes => Set<AccessCode>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<PaymentClaim> PaymentClaims => Set<PaymentClaim>();
    public DbSet<PaymentPromise> PaymentPromises => Set<PaymentPromise>();
    public DbSet<CashToken> CashTokens => Set<CashToken>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ContractTemplate> ContractTemplates => Set<ContractTemplate>();
    public DbSet<LeaseDocument> LeaseDocuments => Set<LeaseDocument>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<ReceiptCounter> ReceiptCounters => Set<ReceiptCounter>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<DepositSettlement> DepositSettlements => Set<DepositSettlement>();
    public DbSet<DepositDeduction> DepositDeductions => Set<DepositDeduction>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("btree_gist");
        base.OnModelCreating(b);

        b.Entity<ApplicationUser>().Property(u => u.FullName).HasMaxLength(200);

        b.Entity<Organization>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(100);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Membership>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.UserId }).IsUnique();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            // A tenant login points at exactly one tenant record of the SAME organization; staff never do. One login per tenant record.
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TenantId })
                .HasPrincipalKey(t => new { t.OrganizationId, t.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OrganizationId, x.TenantId }).IsUnique().HasFilter("\"TenantId\" IS NOT NULL");
            e.ToTable(t => t.HasCheckConstraint("CK_Memberships_TenantLink", "(\"Role\" = 'Tenant') = (\"TenantId\" IS NOT NULL)"));
        });

        b.Entity<Invitation>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<AccessCode>(e =>
        {
            e.HasIndex(x => x.CodeHash).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TenantId })
                .HasPrincipalKey(t => new { t.OrganizationId, t.Id }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.ToTable(t => t.HasCheckConstraint("CK_AccessCodes_RoleTenant", "(\"Role\" IN ('Collector','Tenant')) AND ((\"Role\" = 'Tenant') = (\"TenantId\" IS NOT NULL))"));
        });

        b.Entity<OrganizationSettings>(e =>
        {
            e.HasKey(x => x.OrganizationId);
            e.HasOne<Organization>().WithOne().HasForeignKey<OrganizationSettings>(x => x.OrganizationId);
            e.Property(x => x.LandlordName).HasMaxLength(200);
            e.Property(x => x.LandlordAddress).HasMaxLength(500);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_OrganizationSettings_ReminderHour", "\"ReminderHour\" BETWEEN 0 AND 23");
                t.HasCheckConstraint("CK_OrganizationSettings_GraceDays", "\"GraceDays\" BETWEEN 0 AND 30");
            });
        });

        b.Entity<StoredFile>(e =>
        {
            e.HasAlternateKey(x => new { x.OrganizationId, x.Id }); // target of composite FKs from the records that own a file
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.Property(x => x.OriginalName).HasMaxLength(200);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.ToTable("StoredFiles", t => t.HasCheckConstraint("CK_StoredFiles_Size", "\"SizeBytes\" > 0 AND \"SizeBytes\" <= 5242880"));
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.OccurredAt });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.Property(x => x.Metadata).HasColumnType("jsonb");
        });

        b.Entity<Property>(e =>
        {
            e.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Unit>(e =>
        {
            e.HasAlternateKey(x => new { x.OrganizationId, x.Id }); // target of composite FKs from leases
            e.HasAlternateKey(x => new { x.PropertyId, x.Id }); // target of the FK that keeps a maintenance issue's unit inside its property
            // Composite FK: a unit can only reference a property of its own organization, enforced by PostgreSQL.
            e.HasOne<Property>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PropertyId })
                .HasPrincipalKey(p => new { p.OrganizationId, p.Id });
            e.HasIndex(x => new { x.PropertyId, x.Code }).IsUnique();
            e.Property(x => x.Code).HasMaxLength(30);
            e.Property(x => x.BaseRent).HasPrecision(18, 2);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Units_BaseRent_NonNegative", "\"BaseRent\" >= 0");
                t.HasCheckConstraint("CK_Units_Bedrooms_NonNegative", "\"Bedrooms\" IS NULL OR \"Bedrooms\" >= 0");
            });
        });

        b.Entity<Tenant>(e =>
        {
            e.HasAlternateKey(x => new { x.OrganizationId, x.Id }); // target of composite FKs from leases and payments
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.Property(x => x.FullName).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Phone).HasMaxLength(30);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Lease>(e =>
        {
            e.HasOne<Unit>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.UnitId }).HasPrincipalKey(u => new { u.OrganizationId, u.Id });
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TenantId }).HasPrincipalKey(t => new { t.OrganizationId, t.Id });
            e.HasAlternateKey(x => new { x.OrganizationId, x.Id }); // target of composite FKs from charges
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.HasIndex(x => x.UnitId);
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.MonthlyRent).HasPrecision(18, 2);
            e.Property(x => x.DepositAmount).HasPrecision(18, 2);
            e.Property(x => x.TermsNotes).HasMaxLength(4000);
            e.Property(x => x.ClosureReason).HasMaxLength(1000);
            e.Property(x => x.GuarantorName).HasMaxLength(200);
            e.Property(x => x.GuarantorPhone).HasMaxLength(30);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Leases_DueDay", "\"DueDay\" BETWEEN 1 AND 28");
                t.HasCheckConstraint("CK_Leases_MonthlyRent_NonNegative", "\"MonthlyRent\" >= 0");
                t.HasCheckConstraint("CK_Leases_Deposit_NonNegative", "\"DepositAmount\" IS NULL OR \"DepositAmount\" >= 0");
                t.HasCheckConstraint("CK_Leases_Dates", "\"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\"");
            });
        });

        b.Entity<RentCharge>(e =>
        {
            e.HasOne<Lease>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LeaseId }).HasPrincipalKey(l => new { l.OrganizationId, l.Id });
            e.HasAlternateKey(x => new { x.OrganizationId, x.Id }); // target of composite FKs from payment allocations
            // A lease has at most one live rent charge per period; voiding frees the slot so it can be regenerated correctly.
            e.HasIndex(x => new { x.LeaseId, x.PeriodYear, x.PeriodMonth }).IsUnique().HasFilter("\"Type\" = 'Rent' AND \"Status\" <> 'Voided'");
            e.HasIndex(x => new { x.OrganizationId, x.DueDate });
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.VoidReason).HasMaxLength(1000);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_RentCharges_Amount_Positive", "\"Amount\" > 0");
                t.HasCheckConstraint("CK_RentCharges_Status", "\"Status\" IN ('Open','PartiallyPaid','Paid','Voided')"); // Overdue is derived, never stored
                t.HasCheckConstraint("CK_RentCharges_RentHasPeriod", "\"Type\" <> 'Rent' OR (\"PeriodYear\" IS NOT NULL AND \"PeriodMonth\" BETWEEN 1 AND 12)");
                t.HasCheckConstraint("CK_RentCharges_PeriodPair", "(\"PeriodYear\" IS NULL) = (\"PeriodMonth\" IS NULL)");
            });
        });

        b.Entity<Payment>(e =>
        {
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TenantId }).HasPrincipalKey(t => new { t.OrganizationId, t.Id });
            e.HasAlternateKey(x => new { x.OrganizationId, x.Id }); // target of composite FKs from allocations
            // A retried request carrying the same Idempotency-Key can never create a second payment.
            e.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            e.HasIndex(x => new { x.OrganizationId, x.ReceivedOn });
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.Property(x => x.ReversalReason).HasMaxLength(1000);
            e.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Payments_Amount_Positive", "\"Amount\" > 0");
                t.HasCheckConstraint("CK_Payments_Method", "\"Method\" IN ('Cash','BankTransfer','Card','Other')");
                t.HasCheckConstraint("CK_Payments_Status", "\"Status\" IN ('Confirmed','Reversed')");
                t.HasCheckConstraint("CK_Payments_ReversalConsistent", "(\"Status\" = 'Reversed') = (\"ReversedAt\" IS NOT NULL)");
            });
        });

        b.Entity<PaymentAllocation>(e =>
        {
            e.HasOne<Payment>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PaymentId }).HasPrincipalKey(p => new { p.OrganizationId, p.Id });
            e.HasOne<RentCharge>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RentChargeId }).HasPrincipalKey(c => new { c.OrganizationId, c.Id });
            e.HasIndex(x => x.PaymentId);
            e.HasIndex(x => x.RentChargeId).HasFilter("\"ReversedAt\" IS NULL");
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.ReversalReason).HasMaxLength(1000);
            e.ToTable(t => t.HasCheckConstraint("CK_PaymentAllocations_Amount_Positive", "\"Amount\" > 0"));
        });

        b.Entity<MaintenanceIssue>(e =>
        {
            e.HasOne<Property>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PropertyId }).HasPrincipalKey(p => new { p.OrganizationId, p.Id });
            // MATCH SIMPLE: skipped when UnitId is null; otherwise the unit must belong to exactly this property.
            e.HasOne<Unit>().WithMany().HasForeignKey(x => new { x.PropertyId, x.UnitId }).HasPrincipalKey(u => new { u.PropertyId, u.Id });
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AssignedToUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ResolvedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            // MATCH SIMPLE: skipped for issues not reported by a tenant; otherwise the tenant must belong to the same organization.
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ReportedByTenantId }).HasPrincipalKey(t => new { t.OrganizationId, t.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OrganizationId, x.ReportedByTenantId });
            e.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            e.HasIndex(x => new { x.OrganizationId, x.Status });
            e.HasIndex(x => x.PropertyId);
            e.HasIndex(x => x.AssignedToUserId);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            // Optimistic concurrency: two people changing the same issue at once cannot silently overwrite each other.
            e.Property<uint>("xmin").IsRowVersion(); // Npgsql maps this to PostgreSQL's xmin system column
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_MaintenanceIssues_Priority", "\"Priority\" IN ('Low','Normal','High','Urgent')");
                t.HasCheckConstraint("CK_MaintenanceIssues_Status", "\"Status\" IN ('Open','InProgress','Waiting','Resolved','Closed')");
                // Resolved needs who and when; an unresolved issue carries neither.
                t.HasCheckConstraint("CK_MaintenanceIssues_Resolution",
                    "(\"Status\" = 'Resolved' AND \"ResolvedAt\" IS NOT NULL AND \"ResolvedByUserId\" IS NOT NULL) OR (\"Status\" IN ('Open','InProgress','Waiting') AND \"ResolvedAt\" IS NULL AND \"ResolvedByUserId\" IS NULL) OR \"Status\" = 'Closed'");
            });
        });

        b.Entity<MaintenanceIssueEvent>(e =>
        {
            e.HasOne<MaintenanceIssue>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.IssueId }).HasPrincipalKey(i => new { i.OrganizationId, i.Id });
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.IssueId, x.CreatedAt });
            e.Property(x => x.Body).HasMaxLength(2000);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<BankAccount>(e =>
        {
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.HasOne<Property>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PropertyId })
                .HasPrincipalKey(p => new { p.OrganizationId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            // One default among the active general accounts, and one active account per property: PostgreSQL decides, not the application.
            e.HasIndex(x => x.OrganizationId).IsUnique().HasFilter("\"IsDefault\" AND \"Status\" = 'Active'");
            e.HasIndex(x => new { x.OrganizationId, x.PropertyId }).IsUnique().HasFilter("\"PropertyId\" IS NOT NULL AND \"Status\" = 'Active'");
            e.Property(x => x.HolderName).HasMaxLength(200);
            e.Property(x => x.BankName).HasMaxLength(100);
            e.Property(x => x.Clabe).HasMaxLength(18);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_BankAccounts_Clabe", "\"Clabe\" ~ '^[0-9]{18}$'");
                t.HasCheckConstraint("CK_BankAccounts_Status", "\"Status\" IN ('Active','Archived')");
                t.HasCheckConstraint("CK_BankAccounts_DefaultIsGeneral", "NOT \"IsDefault\" OR (\"PropertyId\" IS NULL AND \"Status\" = 'Active')");
            });
        });

        b.Entity<PaymentClaim>(e =>
        {
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TenantId }).HasPrincipalKey(t => new { t.OrganizationId, t.Id });
            e.HasOne<StoredFile>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.FileId }).HasPrincipalKey(f => new { f.OrganizationId, f.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Payment>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PaymentId }).HasPrincipalKey(p => new { p.OrganizationId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.SubmittedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ReviewedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.FileId).IsUnique(); // a proof backs one report only
            e.HasIndex(x => x.PaymentId).IsUnique().HasFilter("\"PaymentId\" IS NOT NULL"); // a payment comes from one report only
            e.HasIndex(x => new { x.OrganizationId, x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.TenantNote).HasMaxLength(500);
            e.Property(x => x.ReviewComment).HasMaxLength(500);
            e.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_PaymentClaims_Amount_Positive", "\"Amount\" > 0");
                t.HasCheckConstraint("CK_PaymentClaims_Status", "\"Status\" IN ('Pending','Confirmed','Rejected','Cancelled')");
                t.HasCheckConstraint("CK_PaymentClaims_Method", "\"Method\" IN ('Cash','BankTransfer','Other')");
                t.HasCheckConstraint("CK_PaymentClaims_PaymentLink", "(\"Status\" = 'Confirmed') = (\"PaymentId\" IS NOT NULL)");
                t.HasCheckConstraint("CK_PaymentClaims_Review", "\"Status\" IN ('Pending','Cancelled') OR (\"ReviewedAt\" IS NOT NULL AND \"ReviewedByUserId\" IS NOT NULL)");
                t.HasCheckConstraint("CK_PaymentClaims_RejectionComment", "\"Status\" <> 'Rejected' OR \"ReviewComment\" IS NOT NULL");
            });
        });

        b.Entity<PaymentPromise>(e =>
        {
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TenantId }).HasPrincipalKey(t => new { t.OrganizationId, t.Id });
            e.HasOne<RentCharge>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ChargeId }).HasPrincipalKey(c => new { c.OrganizationId, c.Id });
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ChargeId).IsUnique().HasFilter("\"CancelledAt\" IS NULL"); // one open promise per charge
            e.HasIndex(x => new { x.OrganizationId, x.TenantId });
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.CancelReason).HasMaxLength(500);
            e.ToTable(t => t.HasCheckConstraint("CK_PaymentPromises_CancelPair", "(\"CancelledAt\" IS NULL) = (\"CancelReason\" IS NULL)"));
        });

        b.Entity<CashToken>(e =>
        {
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TenantId }).HasPrincipalKey(t => new { t.OrganizationId, t.Id });
            e.HasOne<Payment>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PaymentId }).HasPrincipalKey(p => new { p.OrganizationId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UsedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.CodeHash).IsUnique();
            e.HasIndex(x => x.PaymentId).IsUnique().HasFilter("\"PaymentId\" IS NOT NULL"); // a payment can come from one token only
            e.HasIndex(x => x.TenantId).IsUnique().HasFilter("\"UsedAt\" IS NULL AND \"RevokedAt\" IS NULL"); // one live token per tenant
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.ToTable(t => t.HasCheckConstraint("CK_CashTokens_Used", "(\"UsedAt\" IS NULL) = (\"PaymentId\" IS NULL) AND (\"UsedAt\" IS NULL) = (\"UsedByUserId\" IS NULL)"));
        });

        b.Entity<Notification>(e =>
        {
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            // The same reminder is stored once per person, however many times the daily run repeats: PostgreSQL decides, not the application.
            e.HasIndex(x => new { x.OrganizationId, x.UserId, x.DedupKey }).IsUnique().HasFilter("\"DedupKey\" IS NOT NULL");
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => x.UserId).HasFilter("\"ReadAt\" IS NULL"); // the unread badge
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt }).HasFilter("\"DeliveredAt\" IS NULL"); // the delivery queue
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Body).HasMaxLength(500);
            e.Property(x => x.Url).HasMaxLength(200);
            e.Property(x => x.DedupKey).HasMaxLength(200);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Notifications_Url", "\"Url\" LIKE '/%' AND \"Url\" NOT LIKE '//%' AND POSITION('\\' IN \"Url\") = 0");
                t.HasCheckConstraint("CK_Notifications_Attempts", "\"DeliveryAttempts\" >= 0");
            });
        });

        b.Entity<ContractTemplate>(e =>
        {
            e.HasAlternateKey(x => new { x.OrganizationId, x.Id }); // target of the composite key from generated contracts
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.HasOne<StoredFile>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.FileId }).HasPrincipalKey(f => new { f.OrganizationId, f.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.OrganizationId).IsUnique().HasFilter("\"IsActive\""); // one active template per organization, decided by PostgreSQL
            e.Property(x => x.Name).HasMaxLength(200);
            e.ToTable(t => t.HasCheckConstraint("CK_ContractTemplates_Retired", "(\"IsActive\") = (\"RetiredAt\" IS NULL)"));
        });

        b.Entity<LeaseDocument>(e =>
        {
            e.HasOne<Lease>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LeaseId }).HasPrincipalKey(l => new { l.OrganizationId, l.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ContractTemplate>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TemplateId }).HasPrincipalKey(t => new { t.OrganizationId, t.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StoredFile>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DocxFileId }).HasPrincipalKey(f => new { f.OrganizationId, f.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StoredFile>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PdfFileId }).HasPrincipalKey(f => new { f.OrganizationId, f.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.GeneratedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.LeaseId, x.Version }).IsUnique(); // version numbers are never repeated, even if two people generate at once
            e.HasIndex(x => x.DocxFileId).IsUnique();
            e.HasIndex(x => x.PdfFileId).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_LeaseDocuments_Version", "\"Version\" > 0"));
        });

        b.Entity<Receipt>(e =>
        {
            e.HasOne<Payment>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PaymentId }).HasPrincipalKey(p => new { p.OrganizationId, p.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.PaymentId).IsUnique();                          // a payment has exactly one receipt
            e.HasIndex(x => new { x.OrganizationId, x.Folio }).IsUnique();    // a number is never given twice inside an organization
            e.ToTable(t => t.HasCheckConstraint("CK_Receipts_Folio", "\"Folio\" > 0"));
        });

        b.Entity<ReceiptCounter>(e =>
        {
            e.HasKey(x => x.OrganizationId);
            e.HasOne<Organization>().WithOne().HasForeignKey<ReceiptCounter>(x => x.OrganizationId);
            e.ToTable(t => t.HasCheckConstraint("CK_ReceiptCounters_Last", "\"LastFolio\" >= 0"));
        });

        b.Entity<InventoryItem>(e =>
        {
            e.HasOne<Lease>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LeaseId }).HasPrincipalKey(l => new { l.OrganizationId, l.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.LeaseId, x.SortOrder });
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(300);
            e.Property(x => x.ReturnNotes).HasMaxLength(300);
            e.Property(x => x.Condition).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.ReturnCondition).HasConversion<string>().HasMaxLength(10);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_InventoryItems_Quantity", "\"Quantity\" BETWEEN 1 AND 999");
                t.HasCheckConstraint("CK_InventoryItems_Condition", "\"Condition\" IN ('Good','Fair','Poor')");
                t.HasCheckConstraint("CK_InventoryItems_ReturnCondition", "\"ReturnCondition\" IS NULL OR \"ReturnCondition\" IN ('Good','Fair','Poor')");
            });
        });

        b.Entity<DepositSettlement>(e =>
        {
            e.HasAlternateKey(x => new { x.OrganizationId, x.Id }); // target of the composite key from its deductions
            e.HasOne<Lease>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LeaseId }).HasPrincipalKey(l => new { l.OrganizationId, l.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.SettledByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.VoidedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.LeaseId).IsUnique().HasFilter("\"VoidedAt\" IS NULL"); // one live settlement per lease, decided by PostgreSQL
            e.Property(x => x.HeldAmount).HasPrecision(18, 2);
            e.Property(x => x.DeductionsTotal).HasPrecision(18, 2);
            e.Property(x => x.RefundAmount).HasPrecision(18, 2);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.VoidReason).HasMaxLength(1000);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_DepositSettlements_Amounts", "\"HeldAmount\" > 0 AND \"DeductionsTotal\" >= 0 AND \"RefundAmount\" >= 0 AND \"RefundAmount\" = \"HeldAmount\" - \"DeductionsTotal\"");
                t.HasCheckConstraint("CK_DepositSettlements_Void", "(\"VoidedAt\" IS NULL) = (\"VoidReason\" IS NULL) AND (\"VoidedAt\" IS NULL) = (\"VoidedByUserId\" IS NULL)");
            });
        });

        b.Entity<DepositDeduction>(e =>
        {
            e.HasOne<DepositSettlement>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SettlementId }).HasPrincipalKey(s => new { s.OrganizationId, s.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SettlementId);
            e.Property(x => x.Concept).HasMaxLength(200);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.ToTable(t => t.HasCheckConstraint("CK_DepositDeductions_Amount", "\"Amount\" > 0"));
        });

        b.Entity<NotificationPreference>(e =>
        {
            e.HasKey(x => x.UserId);
            e.HasOne<ApplicationUser>().WithOne().HasForeignKey<NotificationPreference>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PushSubscription>(e =>
        {
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Endpoint).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Endpoint).HasMaxLength(1000);
            e.Property(x => x.P256dh).HasMaxLength(200);
            e.Property(x => x.Auth).HasMaxLength(100);
        });

        // Tenant isolation: every organization-owned entity is filtered by the authenticated organization.
        b.Entity<Membership>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<Invitation>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<AuditLog>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<Property>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<Unit>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<Tenant>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<Lease>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<RentCharge>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<Payment>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<MaintenanceIssue>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<MaintenanceIssueEvent>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<PaymentAllocation>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<OrganizationSettings>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<StoredFile>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<AccessCode>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<BankAccount>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<PaymentClaim>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<PaymentPromise>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<CashToken>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<Notification>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<ContractTemplate>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<LeaseDocument>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<Receipt>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<ReceiptCounter>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<InventoryItem>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<DepositSettlement>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
        b.Entity<DepositDeduction>().HasQueryFilter(x => x.OrganizationId == CurrentOrganizationId);
    }
}
