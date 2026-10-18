using Habita.Domain.Common;

namespace Habita.Domain.Documents;

/// <summary>The organization's contract in Word, with {{fields}}. Uploading a new one retires the previous; old ones are kept, because generated contracts point to the one they used.</summary>
public class ContractTemplate : IOrganizationOwned
{
    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public Guid FileId { get; set; }
    /// <summary>Distinct field names the template uses, as found when it was uploaded.</summary>
    public string[] Fields { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public Guid UploadedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}

/// <summary>
/// One generated contract: the Word file and its PDF, frozen. Generating again makes version 2; version 1 stays exactly as it was signed.
/// The row and both files never change (a trigger enforces it).
/// </summary>
public class LeaseDocument : IOrganizationOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid LeaseId { get; set; }
    public int Version { get; set; }
    public Guid TemplateId { get; set; }
    public Guid DocxFileId { get; set; }
    public Guid PdfFileId { get; set; }
    public Guid GeneratedByUserId { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}

/// <summary>The consecutive number of a payment receipt inside an organization. Assigned in the same transaction as the payment, so there are no gaps.</summary>
public class Receipt : IOrganizationOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid PaymentId { get; set; }
    public int Folio { get; set; }
    public DateTimeOffset IssuedAt { get; set; }

    public static string Format(int folio) => $"R-{folio:D6}";
}

/// <summary>One row per organization: the last folio handed out. Taking the next one locks the row until the transaction ends, which is what makes folios unique and consecutive.</summary>
public class ReceiptCounter : IOrganizationOwned
{
    public Guid OrganizationId { get; set; }
    public int LastFolio { get; set; }
}
