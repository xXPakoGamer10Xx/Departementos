using Habita.Application.Abstractions;
using Habita.Application.Audit;
using Habita.Application.Members;
using Habita.Domain.Charges;
using Habita.Domain.Leases;
using Habita.Domain.Maintenance;
using Habita.Domain.Organizations;
using Habita.Domain.Payments;
using Habita.Domain.Properties;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Reports;

public record MoneyCount(int Count, decimal Total);
/// <summary>Overdue balance grouped by how many days late: 1-30, 31-60, 61-90 and 91+.</summary>
public record AgingBucket(string Label, int FromDays, int? ToDays, int Count, decimal Balance);
public record MonthRow(int Year, int Month, decimal RentCharged, decimal OtherCharged, decimal Received, decimal Reversed, int ChargesCount, int PaymentsCount);

/// <summary>
/// Everything is computed on the server from the organization's own data. Period figures use the stated range;
/// "as of" figures (pending, overdue, credit) describe the state on the organization's local date.
/// </summary>
public record FinancialSummaryDto(
    DateOnly From, DateOnly To, string Currency, DateOnly AsOf,
    MoneyCount PaymentsReceived, MoneyCount PaymentsReversed,
    MoneyCount RentChargesGenerated, MoneyCount OtherChargesGenerated, MoneyCount ChargesVoided,
    decimal PendingBalance, int OpenCharges, int OverdueCharges, decimal OverdueBalance, int TenantsWithOverdue, decimal UnassignedCredit,
    IReadOnlyList<MonthRow>? ByMonth, IReadOnlyList<AgingBucket> OverdueAging,
    decimal DepositsHeld, decimal DepositsReceived, decimal DepositsReturned, decimal DepositsRetained);

public record OccupancyDto(int TotalUnits, int Occupied, int Maintenance, int Available, decimal OccupancyRate);
public record IssuesByPriorityDto(int Urgent, int High, int Normal, int Low, int Total);
public record ActivityDto(Guid Id, string Action, string Label, string EntityType, Guid EntityId, string? ActorName, DateTimeOffset OccurredAt);

public record DashboardDto(int Year, int Month, DateOnly From, DateOnly To, string Currency, string TimeZone, DateOnly AsOf,
    FinancialSummaryDto? Finance, OccupancyDto Occupancy, IssuesByPriorityDto Issues, IReadOnlyList<ActivityDto> Activity);

public record ExportSet(string FileName, string[] Header, IAsyncEnumerable<object?[]> Rows);

public class ReportService(IAppDbContext db, ICurrentOrganization org, IAuditWriter audit, OrgCalendar calendar, IUserDirectory directory)
{
    public const int MaxSummaryMonths = 36;
    public const int MaxByMonthRows = 24;
    public const int MaxExportDays = 1100;
    private const int ExportPageSize = 500;

    private bool CanSeeFinance => org.Role is Role.Owner or Role.Admin or Role.Finance or Role.ReadOnly;
    private static int Index(int year, int month) => year * 12 + month - 1;

    // ---------- financial summary ----------

    public async Task<Result<FinancialSummaryDto>> SummaryAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (RangeError(from, to, MaxSummaryMonths * 31) is { } error) return new(Outcome.Invalid, Error: error);
        return new(Outcome.Ok, await BuildSummaryAsync(from, to, monthsFrom: from, includeMonths: true, ct));
    }

    /// <param name="monthsFrom">Start of the by-month series (the dashboard shows the six months ending at the selected one, the report the chosen range).</param>
    private async Task<FinancialSummaryDto> BuildSummaryAsync(DateOnly from, DateOnly to, DateOnly monthsFrom, bool includeMonths, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var cutoff = await calendar.OverdueBeforeAsync(ct); // overdue = due before today minus the grace days
        var currency = await CurrencyAsync(ct);
        var fromIdx = Index(from.Year, from.Month); var toIdx = Index(to.Year, to.Month);

        // Income = confirmed payments by their reception date. Reversed payments are shown apart, never netted into income.
        var inRange = db.Payments.AsNoTracking().Where(p => p.ReceivedOn >= from && p.ReceivedOn <= to);
        var received = await Money(inRange.Where(p => p.Status == PaymentStatus.Confirmed).Select(p => p.Amount), ct);
        // A deposit is the tenant's money held in custody, not income: what was applied to deposit installments is taken out of "received" and reported apart.
        var depositAllocations = from a in db.PaymentAllocations.AsNoTracking()
                                 join p in db.Payments on a.PaymentId equals p.Id join c in db.RentCharges on a.RentChargeId equals c.Id
                                 where a.ReversedAt == null && p.Status == PaymentStatus.Confirmed && c.Type == ChargeType.Deposit
                                 select new { a.Amount, p.ReceivedOn };
        var depositReceived = await depositAllocations.Where(x => x.ReceivedOn >= from && x.ReceivedOn <= to).SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
        received = received with { Total = received.Total - depositReceived };
        var reversed = await Money(inRange.Where(p => p.Status == PaymentStatus.Reversed).Select(p => p.Amount), ct);

        // A charge belongs to the month of its period, or to the month it falls due when it has no period.
        var charges = db.RentCharges.AsNoTracking().Where(c =>
            (c.PeriodYear != null ? c.PeriodYear.GetValueOrDefault() * 12 + c.PeriodMonth.GetValueOrDefault() - 1 : c.DueDate.Year * 12 + c.DueDate.Month - 1) >= fromIdx &&
            (c.PeriodYear != null ? c.PeriodYear.GetValueOrDefault() * 12 + c.PeriodMonth.GetValueOrDefault() - 1 : c.DueDate.Year * 12 + c.DueDate.Month - 1) <= toIdx);
        var rent = await Money(charges.Where(c => c.Type == ChargeType.Rent && c.Status != ChargeStatus.Voided).Select(c => c.Amount), ct);
        var other = await Money(charges.Where(c => c.Type == ChargeType.Other && c.Status != ChargeStatus.Voided).Select(c => c.Amount), ct);
        var voided = await Money(charges.Where(c => c.Status == ChargeStatus.Voided).Select(c => c.Amount), ct);

        // State as of today: total charged minus what is actively applied. A voided charge never has applications.
        var liveCharges = db.RentCharges.AsNoTracking().Where(c => c.Status != ChargeStatus.Voided);
        var chargedTotal = await liveCharges.SumAsync(c => (decimal?)c.Amount, ct) ?? 0m;
        var appliedTotal = await db.PaymentAllocations.AsNoTracking().Where(a => a.ReversedAt == null).SumAsync(a => (decimal?)a.Amount, ct) ?? 0m;
        var pending = Math.Max(0m, chargedTotal - appliedTotal);
        var openCount = await liveCharges.CountAsync(c => c.Status != ChargeStatus.Paid, ct);

        var overdue = await (from c in db.RentCharges.AsNoTracking()
                             join l in db.Leases on c.LeaseId equals l.Id
                             where (c.Status == ChargeStatus.Open || c.Status == ChargeStatus.PartiallyPaid) && c.DueDate < cutoff
                             select new { c.Amount, c.DueDate, l.TenantId, Applied = db.PaymentAllocations.Where(a => a.RentChargeId == c.Id && a.ReversedAt == null).Sum(a => (decimal?)a.Amount) ?? 0m }).ToListAsync(ct);

        // Credit = payments received and not applied (each payment's applications never exceed its amount, so the totals subtract cleanly).
        var confirmedTotal = await db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatus.Confirmed).SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
        var appliedOnConfirmed = await (from a in db.PaymentAllocations.AsNoTracking() join p in db.Payments on a.PaymentId equals p.Id
                                        where a.ReversedAt == null && p.Status == PaymentStatus.Confirmed select (decimal?)a.Amount).SumAsync(ct) ?? 0m;

        List<MonthRow>? months = null;
        var seriesFrom = Index(monthsFrom.Year, monthsFrom.Month);
        if (includeMonths && toIdx - seriesFrom + 1 <= MaxByMonthRows) months = await ByMonthAsync(monthsFrom, to, seriesFrom, toIdx, ct);

        // Days late = the organization's local today minus the due date. Each overdue charge falls in exactly one bucket.
        AgingBucket Bucket(string label, int lo, int? hi)
        {
            var rows = overdue.Where(o => { var late = today.DayNumber - o.DueDate.DayNumber; return late >= lo && (hi is null || late <= hi); }).ToList();
            return new(label, lo, hi, rows.Count, rows.Sum(o => Math.Max(0m, o.Amount - o.Applied)));
        }
        var aging = new List<AgingBucket> { Bucket("1–30 días", 1, 30), Bucket("31–60 días", 31, 60), Bucket("61–90 días", 61, 90), Bucket("Más de 90 días", 91, null) };

        // Custody now = deposit installments collected minus what live settlements already closed. Period figures use the settlement date.
        var depositCollected = await depositAllocations.SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
        var liveSettlements = db.DepositSettlements.AsNoTracking().Where(s => s.VoidedAt == null);
        var settledHeld = await liveSettlements.SumAsync(s => (decimal?)s.HeldAmount, ct) ?? 0m;
        var inPeriod = liveSettlements.Where(s => s.SettledOn >= from && s.SettledOn <= to);
        var returned = await inPeriod.SumAsync(s => (decimal?)s.RefundAmount, ct) ?? 0m;
        var retained = await inPeriod.SumAsync(s => (decimal?)s.DeductionsTotal, ct) ?? 0m;

        return new(from, to, currency, today, received, reversed, rent, other, voided, pending, openCount, overdue.Count,
            overdue.Sum(o => Math.Max(0m, o.Amount - o.Applied)), overdue.Select(o => o.TenantId).Distinct().Count(), Math.Max(0m, confirmedTotal - appliedOnConfirmed), months, aging,
            Math.Max(0m, depositCollected - settledHeld), depositReceived, returned, retained);
    }

    private async Task<List<MonthRow>> ByMonthAsync(DateOnly from, DateOnly to, int fromIdx, int toIdx, CancellationToken ct)
    {
        var chargeRows = await db.RentCharges.AsNoTracking().Where(c => c.Status != ChargeStatus.Voided)
            .Select(c => new { Idx = c.PeriodYear != null ? c.PeriodYear.GetValueOrDefault() * 12 + c.PeriodMonth.GetValueOrDefault() - 1 : c.DueDate.Year * 12 + c.DueDate.Month - 1, c.Type, c.Amount })
            .Where(x => x.Idx >= fromIdx && x.Idx <= toIdx)
            .GroupBy(x => new { x.Idx, x.Type }).Select(g => new { g.Key.Idx, g.Key.Type, Total = g.Sum(x => x.Amount), Count = g.Count() }).ToListAsync(ct);
        var paymentRows = await db.Payments.AsNoTracking().Where(p => p.ReceivedOn >= from && p.ReceivedOn <= to)
            .GroupBy(p => new { Idx = p.ReceivedOn.Year * 12 + p.ReceivedOn.Month - 1, p.Status })
            .Select(g => new { g.Key.Idx, g.Key.Status, Total = g.Sum(p => p.Amount), Count = g.Count() }).ToListAsync(ct);

        DateOnly rangeStart = from, rangeEnd = to; // "from" is a keyword inside a query expression
        var depositRows = await (from a in db.PaymentAllocations.AsNoTracking()
                                 join p in db.Payments on a.PaymentId equals p.Id join c in db.RentCharges on a.RentChargeId equals c.Id
                                 where a.ReversedAt == null && p.Status == PaymentStatus.Confirmed && c.Type == ChargeType.Deposit && p.ReceivedOn >= rangeStart && p.ReceivedOn <= rangeEnd
                                 group a by p.ReceivedOn.Year * 12 + p.ReceivedOn.Month - 1 into g select new { Idx = g.Key, Total = g.Sum(x => x.Amount) }).ToListAsync(ct);

        var rows = new List<MonthRow>();
        for (var idx = fromIdx; idx <= toIdx; idx++)
        {
            var c = chargeRows.Where(x => x.Idx == idx).ToList();
            var p = paymentRows.Where(x => x.Idx == idx).ToList();
            rows.Add(new(idx / 12, idx % 12 + 1,
                c.Where(x => x.Type == ChargeType.Rent).Sum(x => x.Total), c.Where(x => x.Type == ChargeType.Other).Sum(x => x.Total),
                p.Where(x => x.Status == PaymentStatus.Confirmed).Sum(x => x.Total) - depositRows.Where(x => x.Idx == idx).Sum(x => x.Total), p.Where(x => x.Status == PaymentStatus.Reversed).Sum(x => x.Total),
                c.Sum(x => x.Count), p.Where(x => x.Status == PaymentStatus.Confirmed).Sum(x => x.Count)));
        }
        return rows;
    }

    private static async Task<MoneyCount> Money(IQueryable<decimal> amounts, CancellationToken ct) =>
        new(await amounts.CountAsync(ct), await amounts.SumAsync(a => (decimal?)a, ct) ?? 0m);

    // ---------- dashboard ----------

    public async Task<Result<DashboardDto>> DashboardAsync(int? year, int? month, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var y = year ?? today.Year; var m = month ?? today.Month;
        if (RentCharge.ValidatePeriod(y, m) is { } error) return new(Outcome.Invalid, Error: error);
        var (from, to) = RentCharge.PeriodRange(y, m);

        // Finance for the selected month, plus the six months ending there for the trend chart. Roles without finance access get none of it.
        var finance = CanSeeFinance ? await BuildSummaryAsync(from, to, monthsFrom: from.AddMonths(-5), includeMonths: true, ct) : null;

        // Occupancy: active, non-archived units. Occupied is derived from leases in force today; Maintenance units are counted apart.
        var activeUnits = db.Units.AsNoTracking().Where(u => u.Status != UnitStatus.Archived);
        var total = await activeUnits.CountAsync(ct);
        var maintenance = await activeUnits.CountAsync(u => u.Status == UnitStatus.Maintenance, ct);
        var occupied = await activeUnits.CountAsync(u => u.Status == UnitStatus.Available && db.Leases.Any(l => l.UnitId == u.Id
            && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.Ended) && l.StartDate <= today && (l.EndDate == null || l.EndDate >= today)), ct);
        var occupancy = new OccupancyDto(total, occupied, maintenance, total - occupied - maintenance, total == 0 ? 0m : Math.Round((decimal)occupied / total, 4));

        var open = await db.MaintenanceIssues.AsNoTracking().Where(i => i.Status == IssueStatus.Open || i.Status == IssueStatus.InProgress || i.Status == IssueStatus.Waiting)
            .GroupBy(i => i.Priority).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(IssuePriority p) => open.FirstOrDefault(x => x.Key == p)?.Count ?? 0;
        var issues = new IssuesByPriorityDto(Of(IssuePriority.Urgent), Of(IssuePriority.High), Of(IssuePriority.Normal), Of(IssuePriority.Low), open.Sum(x => x.Count));

        return new(Outcome.Ok, new DashboardDto(y, m, from, to, await CurrencyAsync(ct), await TimeZoneAsync(ct), today, finance, occupancy, issues, await ActivityAsync(ct)));
    }

    // Only safe fields leave the audit log: a human label, the entity to link to, who and when. Never the metadata.
    private async Task<IReadOnlyList<ActivityDto>> ActivityAsync(CancellationToken ct)
    {
        var rows = await db.AuditLogs.AsNoTracking().OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).Take(200)
            .Select(a => new { a.Id, a.Action, a.EntityType, a.EntityId, a.ActorUserId, a.OccurredAt }).ToListAsync(ct);
        var visible = rows.Where(r => ActivityVisible(r.Action)).Take(10).ToList();
        var names = await directory.NamesAsync(visible.Select(r => r.ActorUserId), ct);
        return visible.Select(r => new ActivityDto(r.Id, r.Action, AuditLabels.Label(r.Action), r.EntityType, r.EntityId, names.GetValueOrDefault(r.ActorUserId), r.OccurredAt)).ToList();
    }

    private bool ActivityVisible(string action) => org.Role switch
    {
        Role.Owner or Role.Admin => true,
        Role.Finance or Role.ReadOnly => !(action.StartsWith("membership.") || action.StartsWith("invitation.") || action.StartsWith("access_code.") || action.StartsWith("settings.") || action.StartsWith("organization.") || action.StartsWith("report.")),
        Role.Maintenance => action.StartsWith("issue."),
        _ => false,
    };

    // ---------- CSV exports ----------

    public async Task<Result<ExportSet>> ExportChargesAsync(DateOnly from, DateOnly to, ChargeStatus? status, CancellationToken ct)
    {
        if (RangeError(from, to, MaxExportDays) is { } error) return new(Outcome.Invalid, Error: error);
        await AuditExportAsync("charges", from, to, status?.ToString(), ct);
        var today = await calendar.TodayAsync(ct);
        var grace = await calendar.GraceDaysAsync(ct);
        string[] header = ["Periodo", "Inquilino", "Propiedad", "Unidad", "Concepto", "Tipo", "Vence", "Importe", "Pagado", "Saldo", "Estado", "Motivo de anulación"];
        return new(Outcome.Ok, new ExportSet($"cargos-{from:yyyy-MM-dd}-a-{to:yyyy-MM-dd}.csv", header, ChargeRows(from, to, status, today, grace, ct)));
    }

    private async IAsyncEnumerable<object?[]> ChargeRows(DateOnly from, DateOnly to, ChargeStatus? status, DateOnly today, int grace, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var fromIdx = Index(from.Year, from.Month); var toIdx = Index(to.Year, to.Month);
        var cutoff = RentCharge.OverdueBefore(today, grace);
        for (var page = 0; ; page++)
        {
            var q = from c in db.RentCharges.AsNoTracking()
                    join l in db.Leases on c.LeaseId equals l.Id join u in db.Units on l.UnitId equals u.Id
                    join p in db.Properties on u.PropertyId equals p.Id join t in db.Tenants on l.TenantId equals t.Id
                    where (c.PeriodYear != null ? c.PeriodYear.GetValueOrDefault() * 12 + c.PeriodMonth.GetValueOrDefault() - 1 : c.DueDate.Year * 12 + c.DueDate.Month - 1) >= fromIdx
                       && (c.PeriodYear != null ? c.PeriodYear.GetValueOrDefault() * 12 + c.PeriodMonth.GetValueOrDefault() - 1 : c.DueDate.Year * 12 + c.DueDate.Month - 1) <= toIdx
                    select new { c, UnitCode = u.Code, PropertyName = p.Name, Tenant = t.FullName,
                        Applied = db.PaymentAllocations.Where(a => a.RentChargeId == c.Id && a.ReversedAt == null).Sum(a => (decimal?)a.Amount) ?? 0m };
            if (status is { } s)
                q = s switch
                {
                    ChargeStatus.Overdue => q.Where(x => (x.c.Status == ChargeStatus.Open || x.c.Status == ChargeStatus.PartiallyPaid) && x.c.DueDate < cutoff),
                    ChargeStatus.Open or ChargeStatus.PartiallyPaid => q.Where(x => x.c.Status == s && x.c.DueDate >= cutoff),
                    _ => q.Where(x => x.c.Status == s),
                };
            var rows = await q.OrderBy(x => x.c.DueDate).ThenBy(x => x.Tenant).ThenBy(x => x.c.Id).Skip(page * ExportPageSize).Take(ExportPageSize).ToListAsync(ct);
            foreach (var r in rows)
            {
                var voided = r.c.Status == ChargeStatus.Voided;
                yield return [r.c.PeriodYear is { } y ? $"{y}-{r.c.PeriodMonth:00}" : $"{r.c.DueDate:yyyy-MM}", r.Tenant, r.PropertyName, r.UnitCode, r.c.Description,
                    r.c.Type == ChargeType.Rent ? "Renta" : r.c.Type == ChargeType.Deposit ? "Depósito" : "Otro", r.c.DueDate, r.c.Amount, r.Applied, RentCharge.Balance(r.c.Amount, r.Applied, voided),
                    StatusLabel(RentCharge.Effective(r.c.Status, r.c.DueDate, today, grace)), r.c.VoidReason];
            }
            if (rows.Count < ExportPageSize) yield break;
        }
    }

    public async Task<Result<ExportSet>> ExportPaymentsAsync(DateOnly from, DateOnly to, PaymentStatus? status, CancellationToken ct)
    {
        if (RangeError(from, to, MaxExportDays) is { } error) return new(Outcome.Invalid, Error: error);
        await AuditExportAsync("payments", from, to, status?.ToString(), ct);
        string[] header = ["Fecha de recepción", "Inquilino", "Método", "Referencia", "Importe", "Aplicado", "Saldo a favor", "Estado", "Motivo del reverso"];
        return new(Outcome.Ok, new ExportSet($"pagos-{from:yyyy-MM-dd}-a-{to:yyyy-MM-dd}.csv", header, PaymentRows(from, to, status, ct)));
    }

    private async IAsyncEnumerable<object?[]> PaymentRows(DateOnly start, DateOnly end, PaymentStatus? status, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var page = 0; ; page++)
        {
            var q = from p in db.Payments.AsNoTracking() join t in db.Tenants on p.TenantId equals t.Id where p.ReceivedOn >= start && p.ReceivedOn <= end
                    select new { p, Tenant = t.FullName, Applied = db.PaymentAllocations.Where(a => a.PaymentId == p.Id && a.ReversedAt == null).Sum(a => (decimal?)a.Amount) ?? 0m };
            if (status is { } s) q = q.Where(x => x.p.Status == s);
            var rows = await q.OrderBy(x => x.p.ReceivedOn).ThenBy(x => x.p.CreatedAt).ThenBy(x => x.p.Id).Skip(page * ExportPageSize).Take(ExportPageSize).ToListAsync(ct);
            foreach (var r in rows)
                yield return [r.p.ReceivedOn, r.Tenant, MethodLabel(r.p.Method), r.p.Reference, r.p.Amount, r.Applied,
                    r.p.Status == PaymentStatus.Confirmed ? Payment.Unassigned(r.p.Amount, r.Applied) : 0m, r.p.Status == PaymentStatus.Confirmed ? "Confirmado" : "Revertido", r.p.ReversalReason];
            if (rows.Count < ExportPageSize) yield break;
        }
    }

    public async Task<Result<ExportSet>> ExportBalancesAsync(CancellationToken ct)
    {
        await AuditExportAsync("balances", null, null, null, ct);
        var today = await calendar.TodayAsync(ct);
        var grace = await calendar.GraceDaysAsync(ct);
        string[] header = ["Inquilino", "Propiedad", "Unidad", "Concepto", "Vence", "Importe", "Pagado", "Saldo", "Estado", "Días de atraso"];
        return new(Outcome.Ok, new ExportSet($"saldos-pendientes-{today:yyyy-MM-dd}.csv", header, BalanceRows(today, grace, ct)));
    }

    private async IAsyncEnumerable<object?[]> BalanceRows(DateOnly today, int grace, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var page = 0; ; page++)
        {
            var rows = await (from c in db.RentCharges.AsNoTracking()
                              join l in db.Leases on c.LeaseId equals l.Id join u in db.Units on l.UnitId equals u.Id
                              join p in db.Properties on u.PropertyId equals p.Id join t in db.Tenants on l.TenantId equals t.Id
                              where c.Status == ChargeStatus.Open || c.Status == ChargeStatus.PartiallyPaid
                              orderby t.FullName, c.DueDate, c.Id
                              select new { c, UnitCode = u.Code, PropertyName = p.Name, Tenant = t.FullName,
                                  Applied = db.PaymentAllocations.Where(a => a.RentChargeId == c.Id && a.ReversedAt == null).Sum(a => (decimal?)a.Amount) ?? 0m })
                .Skip(page * ExportPageSize).Take(ExportPageSize).ToListAsync(ct);
            foreach (var r in rows)
            {
                var late = today.DayNumber - r.c.DueDate.DayNumber;
                yield return [r.Tenant, r.PropertyName, r.UnitCode, r.c.Description, r.c.DueDate, r.c.Amount, r.Applied, RentCharge.Balance(r.c.Amount, r.Applied, false),
                    late > grace ? "Vencido" : StatusLabel(r.c.Status), Math.Max(0, late)];
            }
            if (rows.Count < ExportPageSize) yield break;
        }
    }

    // ---------- helpers ----------

    private static string StatusLabel(ChargeStatus s) => s switch
    {
        ChargeStatus.Open => "Pendiente", ChargeStatus.PartiallyPaid => "Pago parcial", ChargeStatus.Paid => "Pagado", ChargeStatus.Overdue => "Vencido", _ => "Anulado",
    };

    private static string MethodLabel(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash => "Efectivo", PaymentMethod.BankTransfer => "Transferencia", PaymentMethod.Card => "Tarjeta", _ => "Otro",
    };

    /// <summary>Exports contain financial data, so each one is recorded: who, which report, which filters.</summary>
    private async Task AuditExportAsync(string report, DateOnly? from, DateOnly? to, string? status, CancellationToken ct)
    {
        audit.Write("report.exported", "Report", Guid.NewGuid(), metadata: new { report, from, to, status });
        await db.SaveChangesAsync(ct);
    }

    private static string? RangeError(DateOnly from, DateOnly to, int maxDays) =>
        from > to ? "La fecha inicial no puede ser posterior a la final."
        : from.Year < 2000 || to.Year > 2100 ? "Las fechas deben estar entre 2000 y 2100."
        : to.DayNumber - from.DayNumber + 1 > maxDays ? "El rango de fechas es demasiado amplio." : null;

    private Task<string> CurrencyAsync(CancellationToken ct) => db.Organizations.Where(o => o.Id == org.Id).Select(o => o.Currency).FirstAsync(ct);
    private Task<string> TimeZoneAsync(CancellationToken ct) => db.Organizations.Where(o => o.Id == org.Id).Select(o => o.TimeZone).FirstAsync(ct);
}
