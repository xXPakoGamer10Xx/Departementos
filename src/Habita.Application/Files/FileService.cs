using System.Security.Cryptography;
using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Domain.Files;
using Habita.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Files;

/// <summary>Where the bytes live. Implementations must never expose a path to callers or serve files directly.</summary>
public interface IFileStore
{
    Task WriteAsync(Guid organizationId, Guid fileId, ReadOnlyMemory<byte> content, CancellationToken ct);
    Task<Stream?> OpenAsync(Guid organizationId, Guid fileId, CancellationToken ct);
    Task DeleteAsync(Guid organizationId, Guid fileId, CancellationToken ct);
}

public record FileDto(Guid Id, string Name, string ContentType, long SizeBytes, string Sha256, DateTimeOffset CreatedAt);
public record FileContent(FileDto File, Stream Content);

public class FileService(IAppDbContext db, IFileStore store, ICurrentOrganization org, ICurrentUser user, IAuditWriter audit, IClock clock)
{
    /// <summary>Roles that can open any file of the organization (they review proofs and contracts). Everyone else only opens what they uploaded.</summary>
    private static readonly Role[] Reviewers = [Role.Owner, Role.Admin, Role.Finance, Role.Collector];

    public async Task<Result<FileDto>> SaveAsync(Stream input, string? originalName, CancellationToken ct)
    {
        var since = clock.UtcNow.AddHours(-24);
        if (await db.Files.CountAsync(f => f.UploadedByUserId == user.UserId && f.CreatedAt > since, ct) >= StoredFile.MaxPerUserPerDay)
            return new(Outcome.Invalid, Error: $"Llegaste al límite de {StoredFile.MaxPerUserPerDay} archivos en 24 horas. Inténtalo más tarde.");
        // Read at most one byte past the limit so an oversized upload is rejected without buffering it all.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920]; int read;
        while ((read = await input.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > StoredFile.MaxBytes) return new(Outcome.Invalid, Error: "El archivo pesa más de 5 MB.");
        }
        if (buffer.Length == 0) return new(Outcome.Invalid, Error: "El archivo está vacío.");
        var bytes = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);

        var type = FileSniffer.Detect(bytes.Span);
        if (type is null) return new(Outcome.Invalid, Error: "Formato no permitido. Sube una imagen JPG, PNG o WebP, o un PDF.");

        var file = new StoredFile
        {
            OrganizationId = org.Id!.Value, OriginalName = CleanName(originalName, type), ContentType = type,
            SizeBytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes.Span)),
            UploadedByUserId = user.UserId!.Value, CreatedAt = clock.UtcNow,
        };
        await store.WriteAsync(file.OrganizationId, file.Id, bytes, ct);
        try
        {
            db.Files.Add(file);
            audit.Write("file.uploaded", nameof(StoredFile), file.Id, metadata: new { file.ContentType, file.SizeBytes });
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await store.DeleteAsync(file.OrganizationId, file.Id, CancellationToken.None); // no orphan bytes without a row
            throw;
        }
        return new(Outcome.Ok, ToDto(file));
    }

    /// <summary>
    /// Stores a file the SERVER made (a generated contract, say). The type is the server's, not guessed from bytes, because these are not uploads.
    /// The row is added to the caller's unit of work; if saving then fails the caller must call <see cref="DiscardAsync"/> so no bytes are left behind.
    /// </summary>
    public async Task<Result<StoredFile>> StageGeneratedAsync(ReadOnlyMemory<byte> bytes, string name, string contentType, CancellationToken ct)
    {
        if (bytes.Length == 0 || bytes.Length > StoredFile.MaxBytes) return new(Outcome.Invalid, Error: "El documento generado pesa más de 5 MB.");
        var file = new StoredFile
        {
            OrganizationId = org.Id!.Value, OriginalName = CleanName(name, contentType), ContentType = contentType, SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes.Span)), UploadedByUserId = user.UserId!.Value, CreatedAt = clock.UtcNow,
        };
        await store.WriteAsync(file.OrganizationId, file.Id, bytes, ct);
        db.Files.Add(file);
        return new(Outcome.Ok, file);
    }

    public Task DiscardAsync(StoredFile file) => store.DeleteAsync(file.OrganizationId, file.Id, CancellationToken.None);

    /// <summary>Reads a stored file in full, with no per-person check: for server-side use after the caller has checked access to the record that owns it.</summary>
    public async Task<byte[]?> ReadAllAsync(Guid fileId, CancellationToken ct)
    {
        var f = await db.Files.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId, ct);
        if (f is null) return null;
        await using var stream = await store.OpenAsync(f.OrganizationId, f.Id, ct);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public async Task<Result<FileContent>> OpenAsync(Guid id, CancellationToken ct)
    {
        var f = await db.Files.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return new(Outcome.NotFound);
        var mine = f.UploadedByUserId == user.UserId;
        if (!mine && !(org.Role is { } role && Reviewers.Contains(role))) return new(Outcome.NotFound); // do not reveal that it exists
        var stream = await store.OpenAsync(f.OrganizationId, f.Id, ct);
        return stream is null ? new(Outcome.NotFound) : new(Outcome.Ok, new FileContent(ToDto(f), stream));
    }

    /// <summary>Keeps only a safe display name. It is never used as a path, so this is cosmetic, but it also ends up in headers and UI.</summary>
    internal static string CleanName(string? name, string contentType)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/'));
        n = new string(n.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim();
        if (n.Length > 200) n = n[..200];
        return n.Length == 0 || n.All(c => c == '.') ? "archivo" + FileSniffer.Extension(contentType) : n;
    }

    private static FileDto ToDto(StoredFile f) => new(f.Id, f.OriginalName, f.ContentType, f.SizeBytes, f.Sha256, f.CreatedAt);
}

/// <summary>Decides the file type from its first bytes. The client's file name and Content-Type are ignored.</summary>
public static class FileSniffer
{
    public static string? Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (b.Length >= 5 && b[..5].SequenceEqual("%PDF-"u8)) return "application/pdf";
        return null;
    }

    public static string Extension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", "application/pdf" => ".pdf", _ => "",
    };
}
