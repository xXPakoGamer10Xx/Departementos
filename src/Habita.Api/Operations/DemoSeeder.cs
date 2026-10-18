using System.Text;
using Habita.Api.Auth;
using Habita.Application.Abstractions;
using Habita.Application.Charges;
using Habita.Application.Collections;
using Habita.Application.Files;
using Habita.Application.Leases;
using Habita.Application.Maintenance;
using Habita.Application.Members;
using Habita.Application.Payments;
using Habita.Application.Properties;
using Habita.Application.Tenants;
using Habita.Domain.Maintenance;
using Habita.Domain.Organizations;
using Habita.Domain.Payments;
using Habita.Domain.Properties;
using Habita.Infrastructure.Identity;
using Habita.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Habita.Api.Operations;

/// <summary>
/// Fictitious demo data from the spec (§14): organization "Residencial Demo", property "Torre Norte", units 101/102/201, tenants Ana Ejemplo and Luis Prueba,
/// rent $8,500 MXN, a $5,000 payment applied partially, and the edge cases tests and reviewers need: an overdue charge, a reversed payment, a partially paid
/// charge, an ended lease, users without permission, a second organization (to try foreign UUIDs) and a payment with unassigned balance
/// (to try two concurrent requests spending the same balance). Idempotent. Development only: every user gets the same well-known password.
/// </summary>
public static class DemoSeeder
{
    public const string Password = "Habita-Demo-2026!";
    public const string OrgSlug = "demo-residencial-demo";
    public const string OwnerEmail = "propietario@demo.example";

    public static async Task<string> RunAsync(IServiceProvider root, CancellationToken ct = default)
    {
        await using (var check = root.CreateAsyncScope())
        {
            // The demo is identified by its own owner account, not by the organization name: anyone can register an organization called "Residencial Demo".
            var users = check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (await users.FindByEmailAsync(OwnerEmail) is not null) return "La organización demo ya existe; no se hizo nada.";
        }

        var report = new StringBuilder();
        string demoOrgId, otherOrgId, otherPaymentId, otherChargeId, unassignedPaymentId;
        await using (var scope = root.CreateAsyncScope())
        {
            var s = new Seed(scope.ServiceProvider);
            await s.CreateOrganizationAsync("Residencial Demo", OrgSlug, [(OwnerEmail, "Paola Propietaria", Role.Owner), ("admin@demo.example", "Adrián Administrador", Role.Admin),
                ("finanzas@demo.example", "Fernanda Finanzas", Role.Finance), ("mantenimiento@demo.example", "Mario Mantenimiento", Role.Maintenance), ("lectura@demo.example", "Lucía Lectura", Role.ReadOnly), ("cobrador@demo.example", "Carlos Cobrador", Role.Collector)], ct);
            await s.PopulateDemoAsync(ct);
            demoOrgId = s.OrgId.ToString(); unassignedPaymentId = s.UnassignedPaymentId.ToString();
        }
        await using (var scope = root.CreateAsyncScope())
        {
            var s = new Seed(scope.ServiceProvider);
            await s.CreateOrganizationAsync("Otra Organización Demo", "otra-organizacion-demo", [("otra@demo.example", "Olga Otra", Role.Owner)], ct);
            await s.PopulateOtherAsync(ct);
            otherOrgId = s.OrgId.ToString(); otherPaymentId = s.UnassignedPaymentId.ToString(); otherChargeId = s.AnyChargeId.ToString();
        }

        report.AppendLine("Datos de demostración creados (todos ficticios).");
        report.AppendLine($"  Contraseña de todos los usuarios: {Password}");
        report.AppendLine($"  Residencial Demo ({demoOrgId}): propietario@, admin@, finanzas@, mantenimiento@, lectura@, cobrador@ demo.example; ana@demo.example es la inquilina Ana con acceso al portal");
        report.AppendLine($"  Pago con saldo a favor para probar solicitudes concurrentes: {unassignedPaymentId}");
        report.AppendLine($"  Otra Organización Demo ({otherOrgId}): otra@demo.example. Pago ajeno: {otherPaymentId}; cargo ajeno: {otherChargeId} (para probar que no se pueden usar desde Residencial Demo)");
        return report.ToString();
    }

    private sealed class Seed(IServiceProvider sp)
    {
        private readonly HabitaDbContext _db = sp.GetRequiredService<HabitaDbContext>();
        private readonly UserManager<ApplicationUser> _users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        private readonly ICurrentOrganization _org = sp.GetRequiredService<ICurrentOrganization>();
        private readonly HttpCurrentUser _actor = sp.GetRequiredService<HttpCurrentUser>();
        private readonly IClock _clock = sp.GetRequiredService<IClock>();
        public Guid OrgId { get; private set; }
        public Guid UnassignedPaymentId { get; private set; }
        public Guid AnyChargeId { get; private set; }
        private readonly Dictionary<Role, Guid> _userByRole = [];

        public async Task CreateOrganizationAsync(string name, string slug, (string Email, string Name, Role Role)[] team, CancellationToken ct)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var org = new Organization { Name = name, Slug = slug, CreatedAt = _clock.UtcNow };
            _db.Organizations.Add(org);
            _org.Set(org.Id, Role.Owner, org.Status);
            foreach (var (email, fullName, role) in team)
            {
                var user = new ApplicationUser { UserName = email, Email = email, FullName = fullName, EmailConfirmed = true };
                var created = await _users.CreateAsync(user, Password);
                if (!created.Succeeded) throw new InvalidOperationException($"No se pudo crear {email}: {string.Join("; ", created.Errors.Select(e => e.Description))}");
                _db.Memberships.Add(new Membership { OrganizationId = org.Id, UserId = user.Id, Role = role, CreatedAt = _clock.UtcNow });
                _userByRole[role] = user.Id;
            }
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            OrgId = org.Id;
            As(Role.Owner);
        }

        private void As(Role role)
        {
            _org.Set(OrgId, role, OrganizationStatus.Active);
            _actor.Override = _userByRole[role];
        }

        private static T Ok<T>(Result<T> r, string what)
        {
            if (r.Outcome != Outcome.Ok) throw new InvalidOperationException($"{what}: {r.Outcome} {r.Error}");
            return r.Value!;
        }

        public async Task PopulateDemoAsync(CancellationToken ct)
        {
            var properties = sp.GetRequiredService<PropertyService>(); var tenants = sp.GetRequiredService<TenantService>(); var leases = sp.GetRequiredService<LeaseService>();
            var charges = sp.GetRequiredService<ChargeService>(); var payments = sp.GetRequiredService<PaymentService>(); var issues = sp.GetRequiredService<MaintenanceService>();
            var today = await sp.GetRequiredService<OrgCalendar>().TodayAsync(ct);
            DateOnly Month(int back) => new DateOnly(today.Year, today.Month, 1).AddMonths(-back);

            var tower = Ok(await properties.CreatePropertyAsync(new("Torre Norte", "Av. Demo 123, Ciudad de México", "Edificio ficticio de demostración."), ct), "propiedad");
            var u101 = Ok(await properties.CreateUnitAsync(tower.Id, new("101", UnitType.Apartment, 2, 8500m), ct), "unidad 101");
            var u102 = Ok(await properties.CreateUnitAsync(tower.Id, new("102", UnitType.Apartment, 2, 8500m), ct), "unidad 102");
            var u201 = Ok(await properties.CreateUnitAsync(tower.Id, new("201", UnitType.Apartment, 3, 8500m), ct), "unidad 201");
            var ana = Ok(await tenants.CreateAsync(new("Ana Ejemplo", "ana.ejemplo@example.com", "55 1234 5678", "Inquilina de demostración."), ct), "Ana");
            var luis = Ok(await tenants.CreateAsync(new("Luis Prueba", "luis.prueba@example.com", "55 8765 4321", "Inquilino de demostración."), ct), "Luis");

            // Luis's previous lease on 201 (ended). Ana and Luis currently rent 101 and 102 from three months ago.
            var old = Ok(await leases.CreateAsync(new(u201.Id, luis.Id, Month(8), null, 8500m, 5, 8500m, "Contrato anterior, ya terminado."), ct), "contrato anterior");
            Ok(await leases.ActivateAsync(old.Id, ct), "activar contrato anterior");
            Ok(await leases.TerminateAsync(old.Id, new(Month(4).AddDays(-1), "Cambio de departamento"), ct), "terminar contrato anterior");
            var anaLease = Ok(await leases.CreateAsync(new(u101.Id, ana.Id, Month(3), null, 8500m, 5, 8500m, "Renta mensual, sin prorrateo."), ct), "contrato Ana");
            Ok(await leases.ActivateAsync(anaLease.Id, ct), "activar contrato Ana");
            var luisLease = Ok(await leases.CreateAsync(new(u102.Id, luis.Id, Month(3), null, 8500m, 5, 8500m, null), ct), "contrato Luis");
            Ok(await leases.ActivateAsync(luisLease.Id, ct), "activar contrato Luis");

            for (var back = 8; back >= 0; back--) Ok(await charges.GenerateAsync(new(Month(back).Year, Month(back).Month), ct), $"generar cargos hace {back} meses");

            async Task<List<ChargeDto>> OpenCharges(Guid leaseId) =>
                (await charges.ListAsync(null, leaseId, null, null, null, null, true, 1, 100, ct)).Items.OrderBy(c => c.DueDate).ToList();
            async Task<Guid> Pay(Guid tenantId, decimal amount, DateOnly on, params (Guid Charge, decimal Amount)[] allocations) =>
                Ok(await payments.CreateAsync(new(tenantId, on, amount, PaymentMethod.BankTransfer, null, null, allocations.Select(a => new AllocationInput(a.Charge, a.Amount)).ToList()), Guid.NewGuid().ToString(), ct), "pago").Payment.Id;

            // Luis settled his old lease: four months in one transfer.
            var oldCharges = await OpenCharges(old.Id);
            await Pay(luis.Id, oldCharges.Sum(c => c.Balance), Month(4).AddDays(2), oldCharges.Select(c => (c.Id, c.Balance)).ToArray());

            // Ana: two months paid in full, the next one partially ($5,000 — the spec's example), and one more she has not started to pay.
            var anaOpen = await OpenCharges(anaLease.Id);
            await Pay(ana.Id, 8500m, Month(3).AddDays(3), (anaOpen[0].Id, 8500m));
            await Pay(ana.Id, 8500m, Month(2).AddDays(3), (anaOpen[1].Id, 8500m));
            var partial = await Pay(ana.Id, 5000m, today.AddDays(-2), (anaOpen[2].Id, 5000m));
            AnyChargeId = anaOpen[3].Id;

            // A payment that was reversed (the bank returned the transfer): the charge goes back to what was really paid.
            var returned = await Pay(ana.Id, 3500m, today.AddDays(-1), (anaOpen[2].Id, 3500m));
            Ok(await payments.ReverseAsync(returned, new("Transferencia devuelta por el banco"), ct), "reverso");
            _ = partial;

            // Credit that is not applied to any charge: handy to try two concurrent requests spending the same balance.
            UnassignedPaymentId = await Pay(ana.Id, 2000m, today, []);

            // Luis never paid the current lease: overdue charges. Maintenance issues, one assigned to the maintenance user.
            var leak = Ok(await issues.CreateAsync(new(tower.Id, u101.Id, "Fuga en lavabo", "Gotea desde hace dos días en el baño principal.", IssuePriority.Normal, _userByRole[Role.Maintenance]), ct), "incidencia");
            Ok(await issues.CreateAsync(new(tower.Id, null, "Foco fundido en pasillo", "Pasillo del segundo piso.", IssuePriority.Low, null), ct), "incidencia 2");
            As(Role.Maintenance);
            Ok(await issues.ChangeStatusAsync(leak.Id, new(IssueStatus.InProgress, "Reviso mañana a primera hora."), ct), "incidencia en proceso");
            Ok(await issues.CommentAsync(leak.Id, new("Se necesita un empaque nuevo."), ct), "comentario");
            As(Role.Owner);

            // Collections (Hito 12): where to pay, a promise on Ana's part-paid charge, and Ana's own login with a payment report waiting for review.
            Ok(await sp.GetRequiredService<BankAccountService>().CreateAsync(new("Paola Propietaria", "012180001123456781", null, null), ct), "cuenta bancaria");
            Ok(await sp.GetRequiredService<PaymentPromiseService>().CreateAsync(new(anaOpen[2].Id, today.AddDays(3), "Dijo que paga el viernes."), ct), "promesa de pago");
            var anaUser = new ApplicationUser { UserName = "ana@demo.example", Email = "ana@demo.example", FullName = "Ana Ejemplo", EmailConfirmed = true };
            var created = await _users.CreateAsync(anaUser, Password);
            if (!created.Succeeded) throw new InvalidOperationException($"No se pudo crear a Ana: {string.Join("; ", created.Errors.Select(e => e.Description))}");
            _db.Memberships.Add(new Membership { OrganizationId = OrgId, UserId = anaUser.Id, Role = Role.Tenant, TenantId = ana.Id, CreatedAt = _clock.UtcNow });
            await _db.SaveChangesAsync(ct);
            _org.Set(OrgId, Role.Tenant, OrganizationStatus.Active, ana.Id); _actor.Override = anaUser.Id;
            using var proof = new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
            var file = Ok(await sp.GetRequiredService<FileService>().SaveAsync(proof, "comprobante-demo.png", ct), "comprobante");
            Ok(await sp.GetRequiredService<PortalCollectionService>().ReportAsync(new(3500m, today.AddDays(-1), PaymentMethod.BankTransfer, "SPEI-DEMO-001", "Completo mi pago de este mes.", file.Id), ct), "reporte de pago");
            As(Role.Owner);
        }

        public async Task PopulateOtherAsync(CancellationToken ct)
        {
            var properties = sp.GetRequiredService<PropertyService>(); var tenants = sp.GetRequiredService<TenantService>(); var leases = sp.GetRequiredService<LeaseService>();
            var charges = sp.GetRequiredService<ChargeService>(); var payments = sp.GetRequiredService<PaymentService>();
            var today = await sp.GetRequiredService<OrgCalendar>().TodayAsync(ct);
            var start = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);

            var p = Ok(await properties.CreatePropertyAsync(new("Privada Ajena", "Calle Otra 1, Ciudad de México", null), ct), "propiedad ajena");
            var u = Ok(await properties.CreateUnitAsync(p.Id, new("A1", UnitType.House, 3, 12000m), ct), "unidad ajena");
            var t = Ok(await tenants.CreateAsync(new("Inquilino Ajeno", null, null, null), ct), "inquilino ajeno");
            var l = Ok(await leases.CreateAsync(new(u.Id, t.Id, start, null, 12000m, 1, null, null), ct), "contrato ajeno");
            Ok(await leases.ActivateAsync(l.Id, ct), "activar contrato ajeno");
            Ok(await charges.GenerateAsync(new(start.Year, start.Month), ct), "cargos ajenos");
            var charge = (await charges.ListAsync(null, l.Id, null, null, null, null, false, 1, 10, ct)).Items.First();
            AnyChargeId = charge.Id;
            UnassignedPaymentId = Ok(await payments.CreateAsync(new(t.Id, today, 1000m, PaymentMethod.Cash, null, null, []), Guid.NewGuid().ToString(), ct), "pago ajeno").Payment.Id;
        }
    }
}
