using Habita.Domain.Audit;
using Habita.Domain.Charges;
using Habita.Domain.Collections;
using Habita.Domain.Leases;
using Habita.Domain.Maintenance;
using Habita.Domain.Payments;
using Habita.Domain.Deposits;
using Habita.Domain.Documents;
using Habita.Domain.Files;
using Habita.Domain.Notifications;
using Habita.Domain.Organizations;
using Habita.Domain.Settings;
using Habita.Domain.Properties;
using Habita.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Abstractions;

public interface ICurrentUser
{
    Guid? UserId { get; }
}

public interface ICurrentOrganization
{
    Guid? Id { get; }
    Role? Role { get; }
    OrganizationStatus? Status { get; }
    /// <summary>For a Tenant login: the one tenant record it may see. Null for staff.</summary>
    Guid? TenantId { get; }
    void Set(Guid id, Role role, OrganizationStatus status, Guid? tenantId = null);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

public interface IAuditWriter
{
    /// <summary>Stages an audit entry in the current unit of work (saved with the caller's SaveChanges).</summary>
    void Write(string action, string entityType, Guid entityId, string? reason = null, object? metadata = null);
}

public interface IAppDbContext
{
    DbSet<Organization> Organizations { get; }
    DbSet<Membership> Memberships { get; }
    DbSet<Invitation> Invitations { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Property> Properties { get; }
    DbSet<Unit> Units { get; }
    DbSet<Tenant> Tenants { get; }
    DbSet<Lease> Leases { get; }
    DbSet<RentCharge> RentCharges { get; }
    DbSet<Payment> Payments { get; }
    DbSet<MaintenanceIssue> MaintenanceIssues { get; }
    DbSet<MaintenanceIssueEvent> MaintenanceIssueEvents { get; }
    DbSet<PaymentAllocation> PaymentAllocations { get; }
    DbSet<OrganizationSettings> Settings { get; }
    DbSet<StoredFile> Files { get; }
    DbSet<AccessCode> AccessCodes { get; }
    DbSet<BankAccount> BankAccounts { get; }
    DbSet<PaymentClaim> PaymentClaims { get; }
    DbSet<PaymentPromise> PaymentPromises { get; }
    DbSet<CashToken> CashTokens { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<ContractTemplate> ContractTemplates { get; }
    DbSet<LeaseDocument> LeaseDocuments { get; }
    DbSet<Receipt> Receipts { get; }
    DbSet<InventoryItem> InventoryItems { get; }
    DbSet<DepositSettlement> DepositSettlements { get; }
    DbSet<DepositDeduction> DepositDeductions { get; }
    DbSet<NotificationPreference> NotificationPreferences { get; }
    DbSet<PushSubscription> PushSubscriptions { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Runs the work in one database transaction (nested calls join the outer one).</summary>
public interface IDbTransactions
{
    Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct);
}

public interface IChargeStore
{
    /// <summary>
    /// Inserts monthly rent charges, silently skipping any that already exist for the same lease and period
    /// (the database's partial unique index decides, so concurrent runs cannot duplicate). Returns the ids actually inserted.
    /// </summary>
    Task<IReadOnlyList<Guid>> InsertRentChargesIgnoringDuplicatesAsync(IReadOnlyList<RentCharge> charges, CancellationToken ct);
}

/// <summary>
/// Row locks that serialize money movements. Always acquire in this order inside one transaction:
/// the named scope, then the claim or cash token being settled, then the idempotency key, then the payment, then the charges sorted by id.
/// A fixed order prevents deadlocks.
/// </summary>
public interface IFinanceLocks
{
    /// <summary>An advisory lock on a named critical section of this organization (for example "bank-accounts"), held until the transaction ends.</summary>
    Task LockScopeAsync(string scope, CancellationToken ct);
    /// <summary>Takes the same kind of lock without waiting: false means another process holds it, so the caller should skip the work.</summary>
    Task<bool> TryLockScopeAsync(string scope, CancellationToken ct);
    Task LockClaimAsync(Guid claimId, CancellationToken ct);
    Task LockCashTokenAsync(Guid tokenId, CancellationToken ct);
    Task LockIdempotencyKeyAsync(string key, CancellationToken ct);
    Task LockPaymentAsync(Guid paymentId, CancellationToken ct);
    Task LockChargesAsync(IReadOnlyCollection<Guid> chargeIds, CancellationToken ct);
}

/// <summary>Display names for users, limited to members of the current organization (never leaks users of other organizations).</summary>
public interface IUserDirectory
{
    Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> userIds, CancellationToken ct);
    /// <summary>Login emails of the members of the current organization. Users with no email are left out.</summary>
    Task<Dictionary<Guid, string>> EmailsAsync(IEnumerable<Guid> userIds, CancellationToken ct);
}

/// <summary>
/// Storage operations on notifications that need SQL the model cannot express: idempotent inserts and row locks that let several workers share the queue.
/// </summary>
public interface INotificationStore
{
    /// <summary>Inserts the notifications, silently skipping any whose (user, dedup key) already exists. Returns how many were really inserted.</summary>
    Task<int> InsertIgnoringDuplicatesAsync(IReadOnlyList<Notification> items, CancellationToken ct);
    /// <summary>Locks (FOR UPDATE SKIP LOCKED) up to <paramref name="limit"/> undelivered notifications of the current organization that are due for a try. Needs an open transaction.</summary>
    Task<List<Notification>> LockPendingAsync(DateTimeOffset now, int limit, CancellationToken ct);
    /// <summary>Organizations that have something to deliver. The only query that looks across organizations.</summary>
    Task<List<Guid>> OrganizationsWithPendingAsync(DateTimeOffset now, CancellationToken ct);
}

public enum PushOutcome { Sent, Gone, Failed }
public record PushTarget(string Endpoint, string P256dh, string Auth);
public record PushMessage(string Title, string Body, string Url, string Tag);

public interface IPushSender
{
    /// <summary>False when no VAPID keys are configured: push is then simply off.</summary>
    bool Enabled { get; }
    string? PublicKey { get; }
    /// <summary>Hosts a subscription endpoint may point to.</summary>
    IReadOnlyList<string> AllowedHosts { get; }
    Task<PushOutcome> SendAsync(PushTarget target, PushMessage message, CancellationToken ct);
}

/// <summary>Turns an in-app path into the absolute link used in emails.</summary>
public interface IWebLinks
{
    string Absolute(string path);
}

/// <summary>What a Word template contains, as found when it is uploaded. <see cref="Problem"/> is set when the file must be refused.</summary>
public record TemplateScan(IReadOnlyList<string> Fields, IReadOnlyList<string> UnknownFields, string? Problem);

/// <summary>Reads and fills Word templates. Fields are written {{like_this}}; a field may be split across several runs of formatting by Word, and is still found.</summary>
public interface IContractTemplateEngine
{
    /// <summary>Checks that the bytes are a safe .docx (no macros, no outside links, no zip bombs) and lists the fields it uses.</summary>
    TemplateScan Scan(byte[] docx);
    /// <summary>Returns the template with every field replaced. A paragraph that held fields and ends up empty is removed, with its heading if the heading is left alone.</summary>
    byte[] Render(byte[] docx, IReadOnlyDictionary<string, string> values);
    /// <summary>An example template that uses every field, so a landlord can see what is available.</summary>
    byte[] BuildSample(IReadOnlyList<ContractFields.Field> fields);
}

/// <summary>The PDF service (Gotenberg: LibreOffice for Word files, Chromium for HTML). It runs on the internal network only.</summary>
public interface IPdfConverter
{
    Task<byte[]> FromDocxAsync(byte[] docx, CancellationToken ct);
    Task<byte[]> FromHtmlAsync(string html, CancellationToken ct);
}

/// <summary>The PDF service is not configured or not answering. The caller reports it as temporarily unavailable.</summary>
public class PdfUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Hands out the next receipt number of the current organization. Must run inside the payment's transaction.</summary>
public interface IReceiptFolios
{
    Task<int> NextAsync(CancellationToken ct);
}

/// <summary>One cell of a spreadsheet. <see cref="Number"/> is set when the cell holds a number; <see cref="IsDate"/> when Excel formats that number as a date.</summary>
public readonly record struct SheetCell(string? Raw, double? Number = null, bool IsDate = false)
{
    /// <summary>Never null, even for an empty cell (<c>default</c>).</summary>
    public string Text => Raw ?? "";
}
public record SheetRow(int RowNumber, IReadOnlyList<SheetCell> Cells);
public record SpreadsheetData(string SheetName, IReadOnlyList<string> Headers, IReadOnlyList<SheetRow> Rows);
public record SpreadsheetResult(SpreadsheetData? Data, string? Problem);

/// <summary>Reads an .xlsx or .csv file into rows. The file is untrusted: the reader refuses macros, outside links, zip bombs and files that are too big.</summary>
public interface ISpreadsheetReader
{
    SpreadsheetResult Read(byte[] bytes);
}
