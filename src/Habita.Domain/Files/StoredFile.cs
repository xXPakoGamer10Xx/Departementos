using Habita.Domain.Common;

namespace Habita.Domain.Files;

/// <summary>Metadata of a private uploaded file (payment proof, contract...). The bytes live in the file store, never in a public folder.</summary>
public class StoredFile : IOrganizationOwned
{
    public const long MaxBytes = 5 * 1024 * 1024;
    /// <summary>Files one person can add in 24 hours (uploads and documents the server makes for them). A tenant needs a handful; this only stops a login from filling the disk.</summary>
    public const int MaxPerUserPerDay = 200;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    /// <summary>Name shown to people. Never used to build a path.</summary>
    public string OriginalName { get; set; } = "";
    /// <summary>Decided by looking at the bytes, not by trusting the client.</summary>
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public Guid UploadedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
