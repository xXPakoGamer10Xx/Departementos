using System.Security.Cryptography;
using System.Text;
using Habita.Application.Abstractions;
using Habita.Application.Leases;
using Habita.Application.Members;
using Habita.Application.Properties;
using Habita.Application.Tenants;
using Habita.Domain.Leases;
using Habita.Domain.Properties;
using Habita.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Imports;

public enum ImportAction { Create, AlreadyImported, Error }

/// <summary>Where the rows go and what is done with them. Everything the person chose is part of the plan, so it is part of its fingerprint too.</summary>
public record ImportOptions(Guid PropertyId, bool CreateMissingUnits, bool ActivateLeases);

public record ImportRowPreview(int Row, string Name, string UnitCode, decimal? MonthlyRent, int? DueDay, DateOnly? StartDate, DateOnly? EndDate, string? GuarantorName, string? Phone,
    ImportAction Action, bool NewTenant, bool NewUnit, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public record ImportPreview(string SheetName, string PlanHash, IReadOnlyList<ImportRowPreview> Rows, IReadOnlyList<string> UnknownColumns,
    int ToCreate, int AlreadyImported, int Errors, int NewTenants, int NewUnits, bool CanConfirm);

public record ImportResult(int LeasesCreated, int TenantsCreated, int UnitsCreated, int LeasesActivated, int AlreadyImported, int SkippedWithErrors);

/// <summary>
/// Brings the landlord's spreadsheet of tenants and contracts into Habita. The file is read, checked and shown as a preview; nothing is saved until it is
/// confirmed, and then everything is saved in one transaction or nothing is. The file itself is never stored. Running the same import again creates nothing new.
/// </summary>
public class ImportService(IAppDbContext db, IDbTransactions tx, IFinanceLocks locks, IAuditWriter audit, ISpreadsheetReader reader, OrgCalendar calendar,
    PropertyService properties, TenantService tenants, LeaseService leases)
{
    public const string TemplateCsv =
        "Nombre,Depto,Renta,Fecha de pago,Inicio,Término,Fiador,Teléfonos,Correo,Depósito,Observaciones\n" +
        "Ana Ejemplo,101,\"$8,500.00\",5,01/07/2026,30/06/2027,Pedro Fiador,55 1234 5678 / 55 8765 4321,ana@example.com,\"$8,500.00\",Renta mensual sin prorrateo\n";

    private sealed class PlanRow
    {
        public required ParsedRow Parsed { get; init; }
        public string? LeaseNotes { get; init; }
        public ImportAction Action { get; set; } = ImportAction.Create;
        public bool NewTenant { get; set; }
        public bool NewUnit { get; set; }
        public Guid? TenantId { get; set; }
        public Guid? UnitId { get; set; }
        public string TenantKey { get; set; } = "";
        public List<string> Errors { get; } = [];
        public List<string> Warnings { get; } = [];
    }

    private sealed record Plan(string SheetName, List<PlanRow> Rows, List<string> Unknown, string Hash);
    private sealed class ImportAbortException(int row, string message) : Exception(message) { public int Row { get; } = row; }

    // ---------- preview ----------

    public async Task<Result<ImportPreview>> PreviewAsync(byte[] file, ImportOptions options, CancellationToken ct)
    {
        var built = await BuildPlanAsync(file, options, ct);
        if (built.Outcome != Outcome.Ok) return new(built.Outcome, Error: built.Error);
        var plan = built.Value!;
        var rows = plan.Rows.Select(r => new ImportRowPreview(r.Parsed.Row, r.Parsed.Name, r.Parsed.UnitCode, r.Parsed.Rent, r.Parsed.DueDay, r.Parsed.Start, r.Parsed.End, r.Parsed.Guarantor,
            r.Parsed.Phone, r.Action, r.NewTenant, r.NewUnit, r.Errors, r.Warnings)).ToList();
        var errors = rows.Count(r => r.Action == ImportAction.Error);
        return new(Outcome.Ok, new ImportPreview(plan.SheetName, plan.Hash, rows, plan.Unknown, rows.Count(r => r.Action == ImportAction.Create), rows.Count(r => r.Action == ImportAction.AlreadyImported),
            errors, rows.Count(r => r.NewTenant), rows.Count(r => r.NewUnit), CanConfirm: errors == 0 && rows.Any(r => r.Action == ImportAction.Create)));
    }

    // ---------- confirm ----------

    public async Task<Result<ImportResult>> ConfirmAsync(byte[] file, ImportOptions options, string? planHash, bool skipErrors, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(planHash)) return new(Outcome.Invalid, Error: "Revisa el archivo con la vista previa antes de importar.");
        try
        {
            return await tx.RunAsync<Result<ImportResult>>(async () =>
            {
                await locks.LockScopeAsync("import", ct); // one import at a time per organization
                // The plan is built again here, after the lock, from the file itself: what is saved is what the database says now, not what a screen remembered.
                var built = await BuildPlanAsync(file, options, ct);
                if (built.Outcome != Outcome.Ok) return new(built.Outcome, Error: built.Error);
                var plan = built.Value!;
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(plan.Hash), Encoding.ASCII.GetBytes(planHash.Trim())))
                    return new(Outcome.Conflict, Error: "El archivo, las opciones o los datos cambiaron desde la vista previa. Vuelve a revisar el archivo antes de importar.");
                var errorRows = plan.Rows.Where(r => r.Action == ImportAction.Error).ToList();
                if (errorRows.Count > 0 && !skipErrors) return new(Outcome.Conflict, Error: $"Hay {errorRows.Count} filas con error. Corrige el archivo, o elige omitirlas.");

                int tenantsCreated = 0, unitsCreated = 0, leasesCreated = 0, activated = 0;
                var unitIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
                var tenantIds = new Dictionary<string, Guid>();
                foreach (var r in plan.Rows.Where(r => r.Action == ImportAction.Create))
                {
                    var p = r.Parsed;
                    Guid unitId;
                    if (r.UnitId is { } existingUnit) unitId = existingUnit;
                    else if (unitIds.TryGetValue(p.UnitCode, out var made)) unitId = made;
                    else
                    {
                        var unit = await properties.CreateUnitAsync(options.PropertyId, new CreateUnitRequest(p.UnitCode, UnitType.Apartment, null, p.Rent!.Value), ct);
                        if (unit.Outcome != Outcome.Ok) throw new ImportAbortException(p.Row, unit.Error ?? "No se pudo crear la unidad.");
                        unitId = unitIds[p.UnitCode] = unit.Value!.Id; unitsCreated++;
                    }

                    Guid tenantId;
                    if (r.TenantId is { } existingTenant) tenantId = existingTenant;
                    else if (tenantIds.TryGetValue(r.TenantKey, out var madeTenant)) tenantId = madeTenant;
                    else
                    {
                        var tenant = await tenants.CreateAsync(new CreateTenantRequest(p.Name, p.Email, p.Phone, p.Notes), ct);
                        if (tenant.Outcome != Outcome.Ok) throw new ImportAbortException(p.Row, tenant.Error ?? "No se pudo crear el inquilino.");
                        tenantId = tenantIds[r.TenantKey] = tenant.Value!.Id; tenantsCreated++;
                    }

                    var lease = await leases.CreateAsync(new CreateLeaseRequest(unitId, tenantId, p.Start!.Value, p.End, p.Rent!.Value, p.DueDay!.Value, p.Deposit, r.LeaseNotes, p.Guarantor, p.GuarantorPhone), ct);
                    if (lease.Outcome != Outcome.Ok) throw new ImportAbortException(p.Row, lease.Error ?? "No se pudo crear el contrato.");
                    leasesCreated++;
                    if (options.ActivateLeases)
                    {
                        var active = await leases.ActivateAsync(lease.Value!.Id, ct);
                        if (active.Outcome != Outcome.Ok) throw new ImportAbortException(p.Row, active.Error ?? "No se pudo activar el contrato.");
                        activated++;
                    }
                }

                var already = plan.Rows.Count(r => r.Action == ImportAction.AlreadyImported);
                audit.Write("import.confirmed", nameof(Property), options.PropertyId,
                    metadata: new { leases = leasesCreated, tenants = tenantsCreated, units = unitsCreated, activated, alreadyImported = already, skippedWithErrors = errorRows.Count }); // counts only, never names
                await db.SaveChangesAsync(ct);
                return new(Outcome.Ok, new ImportResult(leasesCreated, tenantsCreated, unitsCreated, activated, already, errorRows.Count));
            }, ct);
        }
        catch (ImportAbortException e) { return new(Outcome.Conflict, Error: $"No se importó nada. Fila {e.Row}: {e.Message}"); }
    }

    // ---------- the plan: read, check against what already exists, decide each row ----------

    private async Task<Result<Plan>> BuildPlanAsync(byte[] file, ImportOptions options, CancellationToken ct)
    {
        var property = await db.Properties.AsNoTracking().Where(p => p.Id == options.PropertyId).Select(p => new { p.Status }).FirstOrDefaultAsync(ct);
        if (property is null) return new(Outcome.NotFound);
        if (property.Status != PropertyStatus.Active) return new(Outcome.Invalid, Error: "La propiedad está archivada.");

        var read = reader.Read(file);
        if (read.Problem is { } problem) return new(Outcome.Invalid, Error: problem);
        var data = read.Data!;
        var (map, unknown) = ImportParser.MapHeaders(data.Headers);
        var missing = ImportParser.Required.Where(c => !map.ContainsKey(c)).Select(ImportParser.Label).ToList();
        if (missing.Count > 0)
            return new(Outcome.Invalid, Error: $"Faltan columnas obligatorias: {string.Join(", ", missing)}. Encabezados encontrados: {string.Join(", ", data.Headers.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()))}.");
        var sheetRows = data.Rows.Where(r => r.Cells.Any(c => !string.IsNullOrWhiteSpace(c.Text) || c.Number is not null)).ToList();
        if (sheetRows.Count == 0) return new(Outcome.Invalid, Error: "El archivo no tiene filas con datos debajo de los encabezados.");
        if (sheetRows.Count > ImportParser.MaxRows) return new(Outcome.Invalid, Error: $"El archivo tiene {sheetRows.Count} filas; el máximo por importación es {ImportParser.MaxRows}. Divídelo en partes.");

        var today = await calendar.TodayAsync(ct);
        var units = (await db.Units.AsNoTracking().Where(u => u.PropertyId == options.PropertyId).ToListAsync(ct)).ToDictionary(u => u.Code, StringComparer.OrdinalIgnoreCase);
        var tenantRows = await db.Tenants.AsNoTracking().Select(t => new { t.Id, t.FullName, t.Status }).ToListAsync(ct);
        var tenantsByKey = tenantRows.GroupBy(t => ImportParser.NameKey(t.FullName)).ToDictionary(g => g.Key, g => g.ToList());
        var tenantNames = tenantRows.ToDictionary(t => t.Id, t => t.FullName);
        var unitIds = units.Values.Select(u => u.Id).ToList();
        var existingLeases = await db.Leases.AsNoTracking().Where(l => unitIds.Contains(l.UnitId) && l.Status != LeaseStatus.Cancelled)
            .Select(l => new { l.UnitId, l.TenantId, l.StartDate, l.EndDate, l.Status }).ToListAsync(ct);

        var rows = new List<PlanRow>();
        var plannedUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var plannedTenants = new HashSet<string>();
        var seen = new Dictionary<string, int>(); var plannedByUnit = new Dictionary<string, List<(DateOnly Start, DateOnly End, int Row)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheetRow in sheetRows)
        {
            var parsed = ImportParser.ParseRow(sheetRow, map);
            var row = new PlanRow { Parsed = parsed, LeaseNotes = ImportParser.LeaseNotes(sheetRow, map) };
            rows.Add(row);
            row.Errors.AddRange(parsed.Errors); row.Warnings.AddRange(parsed.Warnings);
            if (row.Errors.Count == 0)
            {
                if (Lease.ValidateTerms(parsed.Start!.Value, parsed.End, parsed.Rent!.Value, parsed.DueDay!.Value, parsed.Deposit, row.LeaseNotes) is { } termsError) row.Errors.Add(termsError);
                if (Lease.ValidateGuarantor(parsed.Guarantor, parsed.GuarantorPhone) is { } guarantorError) row.Errors.Add(guarantorError);
                if (parsed.Notes is { Length: > 2000 }) row.Errors.Add("Las notas del inquilino superan 2000 caracteres.");
            }
            if (row.Errors.Count > 0) { row.Action = ImportAction.Error; continue; }

            var start = parsed.Start!.Value; var end = parsed.End ?? DateOnly.MaxValue;
            row.TenantKey = ImportParser.NameKey(parsed.Name);

            // unit
            units.TryGetValue(parsed.UnitCode, out var unit);
            if (unit is { Status: UnitStatus.Archived }) row.Errors.Add($"La unidad {parsed.UnitCode} está archivada.");
            else if (unit is null)
            {
                if (!options.CreateMissingUnits) row.Errors.Add($"La unidad {parsed.UnitCode} no existe en esta propiedad.");
                else if (plannedUnits.Add(parsed.UnitCode)) row.NewUnit = true;
            }
            else row.UnitId = unit.Id;

            // tenant: the same person is recognised by name, ignoring case, accents and spacing
            tenantsByKey.TryGetValue(row.TenantKey, out var sameName);
            var active = (sameName ?? []).Where(t => t.Status == TenantStatus.Active).ToList();
            if (active.Count > 1) row.Errors.Add($"Hay {active.Count} inquilinos activos llamados «{parsed.Name}». Deja uno solo antes de importar.");
            else if (active.Count == 1) row.TenantId = active[0].Id;
            else if ((sameName ?? []).Count > 0) row.Errors.Add($"Ya existe un inquilino archivado llamado «{parsed.Name}». Restáuralo o cambia el nombre en el archivo.");
            else if (plannedTenants.Add(row.TenantKey)) row.NewTenant = true;

            if (row.Errors.Count > 0) { row.Action = ImportAction.Error; continue; }

            // already imported: this tenant already has this unit from this start date
            if (row.UnitId is { } uid && row.TenantId is { } tid && existingLeases.Any(l => l.UnitId == uid && l.TenantId == tid && l.StartDate == start))
            {
                row.Action = ImportAction.AlreadyImported; row.Warnings.Add("Este contrato ya existe: no se vuelve a crear.");
                continue;
            }
            var fileKey = $"{row.TenantKey}|{parsed.UnitCode.ToUpperInvariant()}|{start:O}";
            if (seen.TryGetValue(fileKey, out var firstRow)) { row.Errors.Add($"Repite la fila {firstRow}: mismo inquilino, departamento e inicio."); row.Action = ImportAction.Error; continue; }
            seen[fileKey] = parsed.Row;

            // overlap: a unit has one contract in force at a time (only matters when the contracts are activated)
            if (options.ActivateLeases)
            {
                var clash = row.UnitId is { } clashUnit ? existingLeases.FirstOrDefault(l => l.UnitId == clashUnit && (l.Status is LeaseStatus.Active or LeaseStatus.Ended)
                    && l.StartDate <= end && (l.EndDate ?? DateOnly.MaxValue) >= start) : null;
                if (clash is not null) row.Errors.Add($"La unidad {parsed.UnitCode} ya tiene un contrato de {(tenantNames.GetValueOrDefault(clash.TenantId, "otro inquilino"))} que se solapa con estas fechas.");
                else if (plannedByUnit.TryGetValue(parsed.UnitCode, out var list) && list.FirstOrDefault(x => x.Start <= end && x.End >= start) is { Row: > 0 } other)
                    row.Errors.Add($"Se solapa con la fila {other.Row}: la misma unidad con fechas que se cruzan.");
                if (row.Errors.Count > 0) { row.Action = ImportAction.Error; continue; }
                if (!plannedByUnit.TryGetValue(parsed.UnitCode, out var planned)) plannedByUnit[parsed.UnitCode] = planned = [];
                planned.Add((start, end, parsed.Row));
            }

            if (parsed.End is { } e && e < today) row.Warnings.Add("El contrato ya terminó. Se importará como vigente; termínalo desde su pantalla si corresponde.");
            if (parsed.Rent == 0) row.Warnings.Add("La renta es 0.");
        }

        return new(Outcome.Ok, new Plan(data.SheetName, rows, unknown, Fingerprint(file, options, rows)));
    }

    /// <summary>Identifies exactly this file, these options and what each row will do. Confirming with a fingerprint that no longer matches is refused.</summary>
    private static string Fingerprint(byte[] file, ImportOptions options, List<PlanRow> rows)
    {
        var sb = new StringBuilder().Append(Convert.ToHexString(SHA256.HashData(file))).Append('|').Append(options.PropertyId).Append('|').Append(options.CreateMissingUnits).Append('|').Append(options.ActivateLeases);
        foreach (var r in rows) sb.Append('|').Append(r.Parsed.Row).Append(':').Append(r.Action).Append(':').Append(r.NewTenant ? 'T' : '-').Append(r.NewUnit ? 'U' : '-').Append(':').Append(r.Errors.Count);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
