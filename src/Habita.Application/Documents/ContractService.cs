using Habita.Application.Abstractions;
using Habita.Application.Files;
using Habita.Application.Members;
using Habita.Domain.Documents;
using Habita.Domain.Files;
using Habita.Domain.Leases;
using Habita.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Documents;

public record TemplateDto(Guid Id, string Name, IReadOnlyList<string> Fields, IReadOnlyList<string> MissingRecommended, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt);
public record LeaseDocumentDto(Guid Id, Guid LeaseId, int Version, string TemplateName, DateTimeOffset GeneratedAt, string? GeneratedByName, long DocxBytes, long PdfBytes, string DocxSha256, string PdfSha256);
public record FieldDto(string Name, string Description, string Example);
public record DownloadFile(byte[] Bytes, string ContentType, string FileName);
public enum DocumentKind { Docx, Pdf }

/// <summary>
/// Contract templates and the contracts generated from them. A template is the landlord's own Word file with {{fields}}. Generating a contract fills it in,
/// converts it to PDF and stores both as a new, frozen version of that lease: generating again never touches an earlier version.
/// </summary>
public class ContractService(IAppDbContext db, FileService files, IContractTemplateEngine engine, IPdfConverter pdf, IDbTransactions tx, IFinanceLocks locks,
    IAuditWriter audit, IClock clock, ICurrentOrganization org, ICurrentUser user, IUserDirectory users, OrgCalendar calendar)
{
    // ---------- template ----------

    public static IReadOnlyList<FieldDto> Fields() => ContractFields.All.Select(f => new FieldDto(f.Name, f.Description, f.Example)).ToList();

    public byte[] Sample() => engine.BuildSample(ContractFields.All);

    public async Task<Result<TemplateDto>> ActiveAsync(CancellationToken ct)
    {
        var t = await db.ContractTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.IsActive, ct);
        return t is null ? new(Outcome.NotFound) : new(Outcome.Ok, ToDto(t));
    }

    public async Task<List<TemplateDto>> HistoryAsync(CancellationToken ct) =>
        (await db.ContractTemplates.AsNoTracking().OrderByDescending(t => t.CreatedAt).Take(20).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<Result<DownloadFile>> TemplateFileAsync(Guid id, CancellationToken ct)
    {
        var t = await db.ContractTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        var bytes = t is null ? null : await files.ReadAllAsync(t.FileId, ct);
        return t is null || bytes is null ? new(Outcome.NotFound) : new(Outcome.Ok, new DownloadFile(bytes, ContractTemplate.DocxContentType, t.Name));
    }

    public async Task<Result<TemplateDto>> UploadTemplateAsync(Stream input, string? originalName, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920]; int read;
        while ((read = await input.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > StoredFile.MaxBytes) return new(Outcome.Invalid, Error: "El archivo pesa más de 5 MB.");
        }
        if (buffer.Length == 0) return new(Outcome.Invalid, Error: "El archivo está vacío.");
        var bytes = buffer.ToArray();

        var scan = engine.Scan(bytes);
        if (scan.Problem is { } problem) return new(Outcome.Invalid, Error: problem);
        if (scan.UnknownFields.Count > 0)
            return new(Outcome.Invalid, Error: $"La plantilla usa campos que no existen: {string.Join(", ", scan.UnknownFields.Select(f => "{{" + f + "}}"))}. Escríbelos igual que en la lista de campos disponibles.");
        if (scan.Fields.Count == 0)
            return new(Outcome.Invalid, Error: "La plantilla no tiene ningún campo (por ejemplo {{nombre_completo}} o {{renta}}). ¿Es el archivo correcto?");

        return await tx.RunAsync<Result<TemplateDto>>(async () =>
        {
            await locks.LockScopeAsync("contract-template", ct); // two uploads at once still leave exactly one active template
            var staged = await files.StageGeneratedAsync(bytes, string.IsNullOrWhiteSpace(originalName) ? "plantilla-contrato.docx" : originalName, ContractTemplate.DocxContentType, ct);
            if (staged.Outcome != Outcome.Ok) return new(staged.Outcome, Error: staged.Error);
            var now = clock.UtcNow;
            foreach (var old in await db.ContractTemplates.Where(t => t.IsActive).ToListAsync(ct)) { old.IsActive = false; old.RetiredAt = now; }
            await db.SaveChangesAsync(ct); // the old one leaves first: the unique index allows a single active template

            var template = new ContractTemplate
            {
                Name = staged.Value!.OriginalName, FileId = staged.Value.Id, Fields = [.. scan.Fields], UploadedByUserId = user.UserId!.Value, CreatedAt = now,
            };
            db.ContractTemplates.Add(template);
            audit.Write("contract_template.uploaded", nameof(ContractTemplate), template.Id, metadata: new { fields = scan.Fields.Count, size = bytes.Length });
            try { await db.SaveChangesAsync(ct); }
            catch { await files.DiscardAsync(staged.Value); throw; }
            return new(Outcome.Ok, ToDto(template));
        }, ct);
    }

    // ---------- contracts ----------

    public async Task<Result<List<LeaseDocumentDto>>> ListAsync(Guid leaseId, CancellationToken ct)
    {
        if (!await db.Leases.AnyAsync(l => l.Id == leaseId, ct)) return new(Outcome.NotFound);
        var rows = await (from d in db.LeaseDocuments.AsNoTracking()
                          join t in db.ContractTemplates on d.TemplateId equals t.Id
                          join dx in db.Files on d.DocxFileId equals dx.Id
                          join pf in db.Files on d.PdfFileId equals pf.Id
                          where d.LeaseId == leaseId orderby d.Version descending
                          select new { d, TemplateName = t.Name, Docx = dx, Pdf = pf }).ToListAsync(ct);
        var names = await users.NamesAsync(rows.Select(r => r.d.GeneratedByUserId), ct);
        return new(Outcome.Ok, rows.Select(r => new LeaseDocumentDto(r.d.Id, r.d.LeaseId, r.d.Version, r.TemplateName, r.d.GeneratedAt, names.GetValueOrDefault(r.d.GeneratedByUserId),
            r.Docx.SizeBytes, r.Pdf.SizeBytes, r.Docx.Sha256, r.Pdf.Sha256)).ToList());
    }

    public async Task<Result<LeaseDocumentDto>> GenerateAsync(Guid leaseId, CancellationToken ct)
    {
        var lease = await db.Leases.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leaseId, ct);
        if (lease is null) return new(Outcome.NotFound);
        if (lease.Status == LeaseStatus.Cancelled) return new(Outcome.Conflict, Error: "Un contrato cancelado no genera documentos.");
        var template = await db.ContractTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.IsActive, ct);
        if (template is null) return new(Outcome.Conflict, Error: "Sube primero la plantilla de contrato en Ajustes.");
        var source = await files.ReadAllAsync(template.FileId, ct);
        if (source is null) return new(Outcome.Conflict, Error: "No se encontró el archivo de la plantilla. Súbela de nuevo.");

        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == lease.TenantId, ct);
        var unit = await db.Units.AsNoTracking().FirstAsync(u => u.Id == lease.UnitId, ct);
        var property = await db.Properties.AsNoTracking().FirstAsync(p => p.Id == unit.PropertyId, ct);
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        var currency = await db.Organizations.AsNoTracking().Where(o => o.Id == org.Id).Select(o => o.Currency).FirstAsync(ct);
        var inventory = await db.InventoryItems.AsNoTracking().Where(i => i.LeaseId == leaseId).OrderBy(i => i.SortOrder).ToListAsync(ct);
        var installments = await db.RentCharges.AsNoTracking().Where(c => c.LeaseId == leaseId && c.Type == Domain.Charges.ChargeType.Deposit && c.Status != Domain.Charges.ChargeStatus.Voided).ToListAsync(ct);
        var values = ContractValues.Build(lease, tenant, unit, property, settings, currency, await calendar.TodayAsync(ct), inventory, installments);

        byte[] filled, pdfBytes;
        try
        {
            filled = engine.Render(source, values);
            pdfBytes = await pdf.FromDocxAsync(filled, ct); // network call: made before the database transaction, never inside it
        }
        catch (PdfUnavailableException e) { return new(Outcome.Unavailable, Error: e.Message); }

        return await tx.RunAsync<Result<LeaseDocumentDto>>(async () =>
        {
            await locks.LockScopeAsync($"lease-doc:{leaseId}", ct); // two people generating at once get versions N and N+1, never the same number
            var version = (await db.LeaseDocuments.Where(d => d.LeaseId == leaseId).MaxAsync(d => (int?)d.Version, ct) ?? 0) + 1;
            var docx = await files.StageGeneratedAsync(filled, $"Contrato {unit.Code} v{version}.docx", ContractTemplate.DocxContentType, ct);
            if (docx.Outcome != Outcome.Ok) return new(docx.Outcome, Error: docx.Error);
            var pdfFile = await files.StageGeneratedAsync(pdfBytes, $"Contrato {unit.Code} v{version}.pdf", "application/pdf", ct);
            if (pdfFile.Outcome != Outcome.Ok) { await files.DiscardAsync(docx.Value!); return new(pdfFile.Outcome, Error: pdfFile.Error); }

            var doc = new LeaseDocument
            {
                LeaseId = leaseId, Version = version, TemplateId = template.Id, DocxFileId = docx.Value!.Id, PdfFileId = pdfFile.Value!.Id,
                GeneratedByUserId = user.UserId!.Value, GeneratedAt = clock.UtcNow,
            };
            db.LeaseDocuments.Add(doc);
            audit.Write("lease_document.generated", nameof(LeaseDocument), doc.Id, metadata: new { leaseId, version, templateId = template.Id, docxSha256 = docx.Value.Sha256, pdfSha256 = pdfFile.Value.Sha256 });
            try { await db.SaveChangesAsync(ct); }
            catch { await files.DiscardAsync(docx.Value); await files.DiscardAsync(pdfFile.Value); throw; }

            var names = await users.NamesAsync([doc.GeneratedByUserId], ct);
            return new(Outcome.Ok, new LeaseDocumentDto(doc.Id, leaseId, version, template.Name, doc.GeneratedAt, names.GetValueOrDefault(doc.GeneratedByUserId),
                docx.Value.SizeBytes, pdfFile.Value.SizeBytes, docx.Value.Sha256, pdfFile.Value.Sha256));
        }, ct);
    }

    public async Task<Result<DownloadFile>> DownloadAsync(Guid leaseId, Guid documentId, DocumentKind kind, CancellationToken ct)
    {
        var d = await db.LeaseDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == documentId && x.LeaseId == leaseId, ct);
        return d is null ? new(Outcome.NotFound) : await ReadAsync(d, kind, ct);
    }

    /// <summary>The newest contract PDF of the tenant's own lease in force. Never anyone else's.</summary>
    public async Task<Result<DownloadFile>> LatestPdfForTenantAsync(CancellationToken ct)
    {
        if (org.Role != Role.Tenant || org.TenantId is not { } tid) return new(Outcome.Forbidden);
        var today = await calendar.TodayAsync(ct);
        var doc = await (from d in db.LeaseDocuments.AsNoTracking()
                         join l in db.Leases on d.LeaseId equals l.Id
                         where l.TenantId == tid && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.Ended) && l.StartDate <= today && (l.EndDate == null || l.EndDate >= today)
                         orderby l.StartDate descending, d.Version descending select d).FirstOrDefaultAsync(ct);
        return doc is null ? new(Outcome.NotFound) : await ReadAsync(doc, DocumentKind.Pdf, ct);
    }

    private async Task<Result<DownloadFile>> ReadAsync(LeaseDocument d, DocumentKind kind, CancellationToken ct)
    {
        var fileId = kind == DocumentKind.Pdf ? d.PdfFileId : d.DocxFileId;
        var meta = await db.Files.AsNoTracking().FirstAsync(f => f.Id == fileId, ct);
        var bytes = await files.ReadAllAsync(fileId, ct);
        return bytes is null ? new(Outcome.NotFound) : new(Outcome.Ok, new DownloadFile(bytes, meta.ContentType, meta.OriginalName));
    }

    private static TemplateDto ToDto(ContractTemplate t) =>
        new(t.Id, t.Name, t.Fields, ContractFields.Recommended.Where(r => !t.Fields.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList(), t.IsActive, t.CreatedAt, t.RetiredAt);
}
