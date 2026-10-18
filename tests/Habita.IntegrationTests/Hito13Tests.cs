using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Habita.Application.Abstractions;
using Habita.Application.Notifications;
using Habita.Domain.Organizations;
using Habita.Infrastructure.Persistence;
using Habita.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Habita.Infrastructure;
using Xunit;

namespace Habita.IntegrationTests;

public class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UtcNow => Now;
}

public class CapturingEmail : IEmailSender
{
    public ConcurrentQueue<(string To, string Subject, string Body)> Sent { get; } = new();
    public Func<string, bool>? FailFor { get; set; }
    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (FailFor?.Invoke(to) == true) throw new InvalidOperationException("SMTP caído (simulado)");
        Sent.Enqueue((to, subject, body));
        return Task.CompletedTask;
    }
    public List<(string To, string Subject, string Body)> To(string address) => Sent.Where(m => m.To == address).ToList();
}

public class FakePush : IPushSender
{
    public bool Enabled { get; set; } = true;
    public string? PublicKey => "BFAKE-PUBLIC-KEY";
    public IReadOnlyList<string> AllowedHosts { get; } = [.. Habita.Domain.Notifications.PushSubscription.DefaultAllowedHosts];
    public ConcurrentQueue<(PushTarget Target, PushMessage Message)> Sent { get; } = new();
    public Func<PushTarget, PushOutcome> Outcome { get; set; } = _ => PushOutcome.Sent;
    public Task<PushOutcome> SendAsync(PushTarget target, PushMessage message, CancellationToken ct)
    {
        var outcome = Outcome(target);
        if (outcome == PushOutcome.Sent) Sent.Enqueue((target, message));
        return Task.FromResult(outcome);
    }
}

/// <summary>An API whose clock, email and push are in the test's hands, with a fast live stream.</summary>
public class NotificationsFixture : ApiFixture
{
    public FakeClock Clock { get; } = new();
    public CapturingEmail Email { get; } = new();
    public FakePush Push { get; } = new();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Factory = Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Notifications:StreamPollSeconds", "0.3");
            b.UseSetting("Notifications:StreamMaxSeconds", "4");
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IClock>(); s.AddSingleton<IClock>(Clock);
                s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(Email);
                s.RemoveAll<IPushSender>(); s.AddSingleton<IPushSender>(Push);
            });
        });
    }
}

/// <summary>Hito 13: notifications (in the app, email, push, live), the daily reminders and the worker's jobs.</summary>
public class Hito13Tests(NotificationsFixture fx) : IClassFixture<NotificationsFixture>
{
    static readonly TimeZoneInfo Mexico = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
    static string D(DateOnly d) => d.ToString("yyyy-MM-dd");
    static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, Mexico).Date);
    /// <summary>The UTC instant at which a Mexico City wall clock shows this day and time.</summary>
    static DateTimeOffset Local(DateOnly day, int hour, int minute = 0) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, minute)), Mexico), TimeSpan.Zero);

    // ---------- scaffolding ----------

    record World(HttpClient Owner, Guid OrgId, string OwnerEmail, HttpClient A, string EmailA, HttpClient B, Guid TenantA, Guid TenantB, Guid LeaseA, Guid LeaseB, Guid PropertyId);

    int _unit;
    async Task<Guid> Lease(HttpClient owner, Guid propertyId, Guid tenantId, DateOnly? end = null)
    {
        var uid = (await Json(await owner.PostAsJsonAsync($"/api/v1/properties/{propertyId}/units", new { code = $"N{Interlocked.Increment(ref _unit)}", baseRent = 1000 }))).GetProperty("id").GetGuid();
        var lid = (await Json(await owner.PostAsJsonAsync("/api/v1/leases", new { unitId = uid, tenantId, startDate = D(Today.AddMonths(-6)), endDate = end is null ? null : D(end.Value), monthlyRent = 1000, dueDay = 5 }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/v1/leases/{lid}/activate", null)).StatusCode);
        return lid;
    }

    async Task<(HttpClient Client, string Email)> Login(HttpClient owner, Guid tenantId)
    {
        var code = (await Json(await owner.PostAsJsonAsync("/api/v1/access-codes", new { role = "Tenant", tenantId }))).GetProperty("code").GetString();
        var c = fx.NewClient(); var email = $"inq-{Guid.NewGuid():N}@example.com";
        var r = await c.PostAsJsonAsync("/api/v1/auth/access-codes/redeem", new { code, email, fullName = "Inquilino Portal", password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return (c, email);
    }

    async Task<Guid> NewTenant(HttpClient owner, string name) =>
        (await Json(await owner.PostAsJsonAsync("/api/v1/tenants", new { fullName = name }))).GetProperty("id").GetGuid();

    async Task<World> NewWorld(string tag)
    {
        fx.Clock.Now = Micros(DateTimeOffset.UtcNow); fx.Email.FailFor = null; fx.Push.Enabled = true; fx.Push.Outcome = _ => PushOutcome.Sent;
        var (owner, orgId, ownerEmail) = await fx.RegisterAsync(tag);
        var pid = (await Json(await owner.PostAsJsonAsync("/api/v1/properties", new { name = "Torre Norte", address = "Reforma 100" }))).GetProperty("id").GetGuid();
        var ta = await NewTenant(owner, "Ana Ejemplo"); var tb = await NewTenant(owner, "Luis Prueba");
        var la = await Lease(owner, pid, ta); var lb = await Lease(owner, pid, tb);
        var (a, emailA) = await Login(owner, ta); var (b, _) = await Login(owner, tb);
        return new(owner, orgId, ownerEmail, a, emailA, b, ta, tb, la, lb, pid);
    }

    static async Task<Guid> Charge(HttpClient c, Guid leaseId, decimal amount, DateOnly due, string description = "Renta")
    {
        var r = await c.PostAsJsonAsync("/api/v1/rent-charges", new { leaseId, description, amount, dueDate = D(due) });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    static async Task Pay(HttpClient c, Guid tenantId, decimal amount, Guid charge)
    {
        var r = await c.PostAsJsonAsync("/api/v1/payments", new { tenantId, receivedOn = D(Today), amount, method = "BankTransfer", allocations = new[] { new { chargeId = charge, amount } } });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    static byte[] Png(int seed) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, (byte)seed, (byte)(seed >> 8), 1, 2, 3, 4, 5, 6];
    int _seed = 3000;
    async Task<Guid> ReportClaim(HttpClient tenant, decimal amount = 1000m)
    {
        var part = new ByteArrayContent(Png(Interlocked.Increment(ref _seed)));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        part.Headers.TryAddWithoutValidation("Content-Disposition", "form-data; name=\"file\"; filename=\"comprobante.png\"");
        var up = await tenant.PostAsync("/api/v1/files", new MultipartFormDataContent { part });
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var fileId = (await Json(up)).GetProperty("id").GetGuid();
        var r = await tenant.PostAsJsonAsync("/api/v1/portal/payment-claims", new { amount, paidOn = D(Today), method = "BankTransfer", reference = "SPEI-1", fileId });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetGuid();
    }

    static async Task<List<JsonElement>> Inbox(HttpClient c, bool unreadOnly = false) =>
        (await Json(await c.GetAsync($"/api/v1/notifications?pageSize=50{(unreadOnly ? "&unreadOnly=true" : "")}"))).GetProperty("items").EnumerateArray().ToList();
    static List<string> Titles(List<JsonElement> items) => items.Select(i => i.GetProperty("title").GetString()!).OrderBy(t => t).ToList();

    async Task<T> InOrg<T>(Guid orgId, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = fx.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentOrganization>().Set(orgId, Role.Owner, OrganizationStatus.Active);
        return await work(scope.ServiceProvider);
    }
    Task<ReminderRun> RemindersFor(Guid orgId) => InOrg(orgId, sp => sp.GetRequiredService<ReminderService>().RunIfDueAsync(default));
    Task<int> DispatchFor(Guid orgId) => InOrg(orgId, async sp =>
    {
        var d = sp.GetRequiredService<NotificationDispatcher>(); var total = 0; int n;
        do { n = await d.DispatchBatchAsync(default); total += n; } while (n == NotificationDispatcher.BatchSize);
        return total;
    });
    async Task<T> Db<T>(Func<HabitaDbContext, Task<T>> work)
    {
        using var scope = fx.Factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<HabitaDbContext>());
    }
    Task Sql(string sql, params object[] args) => Db(async db => await db.Database.ExecuteSqlRawAsync(sql, args));
    static async Task<Guid> UserId(HttpClient c) => (await Json(await c.GetAsync("/api/v1/me"))).GetProperty("user").GetProperty("id").GetGuid();

    // ---------- events: who is told, and the name comes first ----------

    [Fact]
    public async Task A_reported_payment_tells_the_reviewers_only_and_opens_with_the_tenants_name()
    {
        var w = await NewWorld("evclaim");
        var admin = await fx.AddMemberAsync(w.Owner, "Admin"); var finance = await fx.AddMemberAsync(w.Owner, "Finance");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector"); var maintenance = await fx.AddMemberAsync(w.Owner, "Maintenance"); var readOnly = await fx.AddMemberAsync(w.Owner, "ReadOnly");
        await ReportClaim(w.A, 1000m);

        foreach (var reviewer in new[] { w.Owner, admin, finance })
        {
            var inbox = await Inbox(reviewer);
            var n = Assert.Single(inbox);
            Assert.Equal("Ana Ejemplo reportó un pago", n.GetProperty("title").GetString());
            Assert.Equal("ClaimSubmitted", n.GetProperty("type").GetString());
            Assert.Equal("/claims", n.GetProperty("url").GetString());
            Assert.Contains("$1,000.00", n.GetProperty("body").GetString());
            Assert.Contains("transferencia", n.GetProperty("body").GetString());
            Assert.False(n.GetProperty("read").GetBoolean());
        }
        foreach (var other in new[] { collector, maintenance, readOnly, w.A, w.B }) Assert.Empty(await Inbox(other));
    }

    [Fact]
    public async Task A_name_with_several_surnames_is_written_exactly_as_the_record_holds_it()
    {
        var w = await NewWorld("evname");
        var id = await NewTenant(w.Owner, "María Fernanda Pérez López");
        await Lease(w.Owner, w.PropertyId, id);
        var (maria, _) = await Login(w.Owner, id);
        await ReportClaim(maria, 250m);
        var title = Assert.Single(await Inbox(w.Owner)).GetProperty("title").GetString()!;
        Assert.Equal("María Fernanda Pérez López reportó un pago", title);
        Assert.True(title.IndexOf("María", StringComparison.Ordinal) < title.IndexOf("Pérez", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirming_or_rejecting_a_report_tells_that_tenant_by_name_and_nobody_else()
    {
        var w = await NewWorld("evreview");
        var good = await ReportClaim(w.A, 1000m); var bad = await ReportClaim(w.A, 400m);
        await ReportClaim(w.B, 77m);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{good}/confirm", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/v1/payment-claims/{bad}/reject", new { comment = "El monto no coincide con el banco" })).StatusCode);

        var mine = await Inbox(w.A);
        Assert.Equal(["Ana Ejemplo, tu pago de $1,000.00 fue confirmado", "Ana Ejemplo, tu reporte de pago de $400.00 no fue aceptado"], Titles(mine));
        var rejected = mine.Single(n => n.GetProperty("type").GetString() == "ClaimRejected");
        Assert.Equal("El monto no coincide con el banco", rejected.GetProperty("body").GetString());
        Assert.Equal("/portal/pay", rejected.GetProperty("url").GetString());
        Assert.Equal("/portal/payments", mine.Single(n => n.GetProperty("type").GetString() == "ClaimConfirmed").GetProperty("url").GetString());
        Assert.Empty(await Inbox(w.B)); // Luis got nothing about Ana's reports
        Assert.DoesNotContain(Titles(await Inbox(w.Owner)), t => t.Contains("tu pago")); // staff never receive the tenant-facing text
    }

    [Fact]
    public async Task A_cash_collection_tells_the_tenant()
    {
        var w = await NewWorld("evcash");
        var collector = await fx.AddMemberAsync(w.Owner, "Collector");
        var charge = await Charge(w.Owner, w.LeaseA, 900m, Today.AddDays(-2));
        var token = await Json(await w.A.PostAsync("/api/v1/portal/cash-token", null));
        var r = await collector.PostAsJsonAsync($"/api/v1/cash-tokens/{token.GetProperty("id").GetGuid()}/collect", new { amount = 900m, allocations = new[] { new { chargeId = charge, amount = 900m } } });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var n = Assert.Single(await Inbox(w.A));
        Assert.Equal("Ana Ejemplo, registramos tu pago en efectivo de $900.00", n.GetProperty("title").GetString());
        Assert.Empty(await Inbox(collector));
    }

    [Fact]
    public async Task A_tenants_fault_report_tells_owner_and_admin_with_the_name_first()
    {
        var w = await NewWorld("evissue");
        var admin = await fx.AddMemberAsync(w.Owner, "Admin"); var finance = await fx.AddMemberAsync(w.Owner, "Finance"); var maintenance = await fx.AddMemberAsync(w.Owner, "Maintenance");
        var r = await w.A.PostAsJsonAsync("/api/v1/portal/issues", new { title = "Fuga en el baño", description = "Gotea" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var issueId = (await Json(r)).GetProperty("id").GetGuid();
        foreach (var manager in new[] { w.Owner, admin })
        {
            var n = Assert.Single(await Inbox(manager));
            Assert.Equal("Ana Ejemplo reportó una falla", n.GetProperty("title").GetString());
            Assert.Contains("Fuga en el baño", n.GetProperty("body").GetString());
            Assert.Equal($"/maintenance/{issueId}", n.GetProperty("url").GetString());
        }
        Assert.Empty(await Inbox(finance)); Assert.Empty(await Inbox(maintenance)); Assert.Empty(await Inbox(w.B));
    }

    // ---------- reading your own notifications ----------

    [Fact]
    public async Task Everyone_reads_only_their_own_and_cannot_touch_anyone_elses()
    {
        var w = await NewWorld("inbox");
        var (stranger, _, _) = await fx.RegisterAsync("inbox2");
        await ReportClaim(w.A, 10m); await ReportClaim(w.A, 20m); await ReportClaim(w.A, 30m);
        var ownerItems = await Inbox(w.Owner);
        Assert.Equal(3, ownerItems.Count);
        var id = ownerItems[0].GetProperty("id").GetGuid();

        // The tenant, a tenant colleague and a person from another organization cannot read or mark it: it looks like it does not exist.
        foreach (var other in new[] { w.A, w.B, stranger }) Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/notifications/{id}/read", null)).StatusCode);
        Assert.Equal(3, (await Json(await w.Owner.GetAsync("/api/v1/notifications"))).GetProperty("unread").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsync($"/api/v1/notifications/{id}/read", null)).StatusCode);
        Assert.Equal(2, (await Json(await w.Owner.GetAsync("/api/v1/notifications/unread-count"))).GetProperty("unread").GetInt32());
        Assert.Equal(2, (await Inbox(w.Owner, unreadOnly: true)).Count);
        var page = await Json(await w.Owner.GetAsync("/api/v1/notifications?pageSize=2&page=2"));
        Assert.Equal(3, page.GetProperty("total").GetInt32()); Assert.Equal(1, page.GetProperty("items").GetArrayLength());

        Assert.Equal(2, (await Json(await w.Owner.PostAsync("/api/v1/notifications/read-all", null))).GetProperty("marked").GetInt32());
        Assert.Equal(0, (await Json(await w.Owner.GetAsync("/api/v1/notifications/unread-count"))).GetProperty("unread").GetInt32());
        Assert.Empty(await Inbox(stranger));
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.PostAsync($"/api/v1/notifications/{Guid.NewGuid()}/read", null)).StatusCode);
    }

    [Fact]
    public async Task A_person_marking_all_read_does_not_mark_a_colleagues()
    {
        var w = await NewWorld("readall");
        var admin = await fx.AddMemberAsync(w.Owner, "Admin");
        await ReportClaim(w.A);
        await w.Owner.PostAsync("/api/v1/notifications/read-all", null);
        Assert.Equal(1, (await Json(await admin.GetAsync("/api/v1/notifications/unread-count"))).GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task What_a_notification_says_cannot_be_changed_in_the_database()
    {
        var w = await NewWorld("immut");
        await ReportClaim(w.A);
        var id = (await Inbox(w.Owner))[0].GetProperty("id").GetGuid();
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"Notifications\" SET \"Title\" = 'Otro' WHERE \"Id\" = {0}", id));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"Notifications\" SET \"Url\" = '/otro' WHERE \"Id\" = {0}", id));
        await Assert.ThrowsAnyAsync<Exception>(() => Sql("UPDATE \"Notifications\" SET \"Url\" = 'https://evil.example' WHERE \"Id\" = {0}", id)); // not even by changing its link to an outside address
        await Sql("UPDATE \"Notifications\" SET \"ReadAt\" = now() WHERE \"Id\" = {0}", id); // read state does move
    }

    // ---------- reminders ----------

    [Fact]
    public async Task Rent_reminders_go_to_the_tenant_on_the_configured_days_and_overdue_ones_also_to_staff()
    {
        var w = await NewWorld("rem");
        var admin = await fx.AddMemberAsync(w.Owner, "Admin"); var finance = await fx.AddMemberAsync(w.Owner, "Finance"); var collector = await fx.AddMemberAsync(w.Owner, "Collector");
        var day = Today;
        await Charge(w.Owner, w.LeaseA, 1000m, day.AddDays(3), "Renta tres");
        await Charge(w.Owner, w.LeaseA, 1000m, day.AddDays(2), "Renta dos");            // 2 is not a reminder day
        var zero = await Charge(w.Owner, w.LeaseA, 500m, day, "Renta cero");
        await Charge(w.Owner, w.LeaseA, 800m, day.AddDays(-7), "Atrasada siete");
        await Charge(w.Owner, w.LeaseA, 800m, day.AddDays(-5), "Atrasada cinco");       // 5 is not an overdue step
        var paid = await Charge(w.Owner, w.LeaseA, 700m, day.AddDays(3), "Ya pagada");
        await Pay(w.Owner, w.TenantA, 700m, paid);
        await Pay(w.Owner, w.TenantA, 200m, zero);                                       // half paid: the reminder shows what is left
        await Charge(w.Owner, w.LeaseB, 600m, day.AddDays(1), "Renta de Luis");

        fx.Clock.Now = Local(day, 9, 30);
        var run = await RemindersFor(w.OrgId);
        Assert.True(run.Ran);

        var forAna = await Inbox(w.A);
        Assert.Equal(new[] { "Ana Ejemplo, tienes un cargo vencido", "Ana Ejemplo, tu renta vence en 3 días", "Ana Ejemplo, tu renta vence hoy" }.OrderBy(t => t).ToList(), Titles(forAna));
        Assert.Contains("$300.00", forAna.Single(n => n.GetProperty("title").GetString()!.EndsWith("vence hoy")).GetProperty("body").GetString());
        Assert.Contains("venció hace 7 días", forAna.Single(n => n.GetProperty("title").GetString()!.EndsWith("vencido")).GetProperty("body").GetString());
        Assert.Equal(["Luis Prueba, tu renta vence en 1 día"], Titles(await Inbox(w.B)));

        foreach (var staff in new[] { w.Owner, admin, finance })
            Assert.Equal(["Ana Ejemplo tiene un cargo vencido hace 7 días"], Titles(await Inbox(staff)));
        Assert.Empty(await Inbox(collector)); // field staff are not sent the office's reminders
    }

    [Fact]
    public async Task The_reminder_days_come_from_the_settings_and_an_empty_list_switches_them_off()
    {
        var w = await NewWorld("remsettings");
        var day = Today;
        await Charge(w.Owner, w.LeaseA, 1000m, day.AddDays(3), "Tres"); await Charge(w.Owner, w.LeaseA, 1000m, day.AddDays(2), "Dos");
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { rentReminderDays = new[] { 2 } })).StatusCode);
        fx.Clock.Now = Local(day, 9, 30);
        await RemindersFor(w.OrgId);
        Assert.Equal(["Ana Ejemplo, tu renta vence en 2 días"], Titles(await Inbox(w.A)));

        var off = await NewWorld("remoff");
        await Charge(off.Owner, off.LeaseA, 1000m, day.AddDays(3));
        await off.Owner.PatchAsJsonAsync("/api/v1/settings", new { rentReminderDays = Array.Empty<int>() });
        fx.Clock.Now = Local(day, 9, 30);
        await RemindersFor(off.OrgId);
        Assert.Empty(await Inbox(off.A));
    }

    [Fact]
    public async Task The_run_waits_for_the_reminder_hour_and_happens_once_per_local_day()
    {
        var w = await NewWorld("remhour");
        var day = Today;
        await Charge(w.Owner, w.LeaseA, 1000m, day.AddDays(1), "Mañana");
        await Charge(w.Owner, w.LeaseA, 1000m, day.AddDays(2), "Pasado");
        await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { rentReminderDays = new[] { 1 } });

        fx.Clock.Now = Local(day, 8, 59); Assert.False((await RemindersFor(w.OrgId)).Ran);      // 9:00 has not arrived
        Assert.Empty(await Inbox(w.A));
        fx.Clock.Now = Local(day, 9, 0); var first = await RemindersFor(w.OrgId);
        Assert.True(first.Ran); Assert.Equal(1, first.Created);
        fx.Clock.Now = Local(day, 15, 0); Assert.False((await RemindersFor(w.OrgId)).Ran);      // already ran today
        fx.Clock.Now = Local(day.AddDays(1), 8, 0); Assert.False((await RemindersFor(w.OrgId)).Ran); // new day, before the hour
        fx.Clock.Now = Local(day.AddDays(1), 9, 5); var second = await RemindersFor(w.OrgId);
        Assert.True(second.Ran); Assert.Equal(1, second.Created);                                // tomorrow's "due in 1 day" is the other charge
        Assert.Equal(["Ana Ejemplo, tu renta vence en 1 día", "Ana Ejemplo, tu renta vence en 1 día"], Titles(await Inbox(w.A)));
    }

    [Fact]
    public async Task The_reminder_hour_is_a_setting_with_limits()
    {
        var w = await NewWorld("remhour2");
        Assert.Equal(9, (await Json(await w.Owner.GetAsync("/api/v1/settings"))).GetProperty("reminderHour").GetInt32());
        foreach (var bad in new[] { -1, 24, 99 }) Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { reminderHour = bad })).StatusCode);
        Assert.Equal(14, (await Json(await w.Owner.PatchAsJsonAsync("/api/v1/settings", new { reminderHour = 14 }))).GetProperty("reminderHour").GetInt32());
        await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(3));
        fx.Clock.Now = Local(Today, 9, 30); Assert.False((await RemindersFor(w.OrgId)).Ran);
        fx.Clock.Now = Local(Today, 14, 0); Assert.True((await RemindersFor(w.OrgId)).Ran);
        var audit = await (await w.Owner.GetAsync("/api/v1/audit-logs?entityType=OrganizationSettings")).Content.ReadAsStringAsync();
        Assert.Contains("reminderHour", audit);
    }

    [Fact]
    public async Task Each_organization_runs_on_its_own_local_day_not_the_servers()
    {
        var w = await NewWorld("remtokyo");
        await Sql("UPDATE \"Organizations\" SET \"TimeZone\" = 'Asia/Tokyo' WHERE \"Id\" = {0}", w.OrgId);
        // 2026-10-20 00:30 UTC is 09:30 on the 20th in Tokyo, but still 18:30 on the 19th in Mexico City.
        var tokyoMorning = new DateTimeOffset(2026, 10, 20, 0, 30, 0, TimeSpan.Zero);
        await Charge(w.Owner, w.LeaseA, 1000m, new DateOnly(2026, 10, 20), "Vence el 20");

        fx.Clock.Now = tokyoMorning.AddHours(-1);                // 08:30 in Tokyo: the hour has not come
        Assert.False((await RemindersFor(w.OrgId)).Ran);
        fx.Clock.Now = tokyoMorning;
        Assert.True((await RemindersFor(w.OrgId)).Ran);
        // For Tokyo the charge falls due TODAY; a Mexico clock would have called it "tomorrow".
        Assert.Equal(["Ana Ejemplo, tu renta vence hoy"], Titles(await Inbox(w.A)));
    }

    [Fact]
    public async Task Repeating_the_run_never_stores_a_reminder_twice()
    {
        var w = await NewWorld("remdedupe");
        await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(3));
        fx.Clock.Now = Local(Today, 9, 30);
        var first = await RemindersFor(w.OrgId);
        Assert.Equal(1, first.Created);
        await Sql("UPDATE \"Settings\" SET \"LastReminderRunOn\" = NULL WHERE \"OrganizationId\" = {0}", w.OrgId); // as if the first run had crashed before it was recorded
        var again = await RemindersFor(w.OrgId);
        Assert.True(again.Ran); Assert.Equal(0, again.Created);
        Assert.Single(await Inbox(w.A));
    }

    [Fact]
    public async Task Several_workers_at_the_same_moment_make_exactly_one_run()
    {
        var w = await NewWorld("remparallel");
        await Charge(w.Owner, w.LeaseA, 1000m, Today.AddDays(3));
        await Charge(w.Owner, w.LeaseB, 1000m, Today.AddDays(1));
        fx.Clock.Now = Local(Today, 9, 30);
        var runs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RemindersFor(w.OrgId)));
        Assert.Equal(1, runs.Count(r => r.Ran));          // the others found the lock taken (or the day already done) and skipped
        Assert.Equal(2, runs.Sum(r => r.Created));
        Assert.Single(await Inbox(w.A)); Assert.Single(await Inbox(w.B));
    }

    [Fact]
    public async Task Contracts_about_to_end_warn_the_tenant_and_staff_on_the_configured_days()
    {
        var w = await NewWorld("remlease");
        var day = Today;
        var carla = await NewTenant(w.Owner, "Carla Sánchez Ruiz"); await Lease(w.Owner, w.PropertyId, carla, day.AddDays(7)); var (carlaClient, _) = await Login(w.Owner, carla);
        var diego = await NewTenant(w.Owner, "Diego Torres Vega"); await Lease(w.Owner, w.PropertyId, diego, day.AddDays(30)); // no portal login
        var elena = await NewTenant(w.Owner, "Elena Gómez Díaz"); var (elenaLease, _) = (await Lease(w.Owner, w.PropertyId, elena, day.AddDays(10)), 0); var (elenaClient, _) = await Login(w.Owner, elena);
        _ = elenaLease;

        fx.Clock.Now = Local(day, 9, 30);
        await RemindersFor(w.OrgId);
        Assert.Equal(["Carla Sánchez Ruiz, tu contrato termina en 7 días"], Titles(await Inbox(carlaClient)));
        Assert.Empty(await Inbox(elenaClient));           // 10 days is not a warning day
        Assert.Equal(["Carla Sánchez Ruiz: su contrato termina en 7 días", "Diego Torres Vega: su contrato termina en 30 días"], Titles(await Inbox(w.Owner)));
    }

    [Fact]
    public async Task A_promise_is_flagged_the_day_it_falls_due_and_again_if_it_is_broken_but_never_when_kept()
    {
        var w = await NewWorld("rempromise");
        var admin = await fx.AddMemberAsync(w.Owner, "Admin");
        var day = Today;
        var broken = await Charge(w.Owner, w.LeaseA, 1000m, day.AddDays(-2), "Se romperá");
        var kept = await Charge(w.Owner, w.LeaseB, 1000m, day.AddDays(-2), "Se cumplirá");
        foreach (var c in new[] { broken, kept })
            Assert.Equal(HttpStatusCode.Created, (await w.Owner.PostAsJsonAsync("/api/v1/payment-promises", new { chargeId = c, promisedOn = D(day) })).StatusCode);
        await Pay(w.Owner, w.TenantB, 1000m, kept);

        fx.Clock.Now = Local(day, 9, 30);
        await RemindersFor(w.OrgId);
        Assert.Equal(["Ana Ejemplo prometió pagar hoy"], Titles(await Inbox(w.Owner)).Where(t => t.Contains("prometió")).ToList()); // only the creator hears it; Luis already paid
        Assert.DoesNotContain(Titles(await Inbox(admin)), t => t.Contains("prometió"));

        fx.Clock.Now = Local(day.AddDays(1), 9, 30);
        await RemindersFor(w.OrgId);
        var titles = Titles(await Inbox(w.Owner));
        Assert.Contains("Ana Ejemplo no cumplió su promesa de pago", titles);
        Assert.DoesNotContain(titles, t => t.StartsWith("Luis Prueba") && t.Contains("promesa"));
    }

    [Fact]
    public async Task A_tenant_without_a_login_still_lets_staff_know_and_nothing_fails()
    {
        var (owner, orgId, _) = await fx.RegisterAsync("remnologin");
        fx.Clock.Now = Micros(DateTimeOffset.UtcNow);
        var pid = (await Json(await owner.PostAsJsonAsync("/api/v1/properties", new { name = "Casa", address = "x" }))).GetProperty("id").GetGuid();
        var t = await NewTenant(owner, "Sin Acceso Pérez"); var lease = await Lease(owner, pid, t);
        await Charge(owner, lease, 500m, Today.AddDays(-1), "Atrasada");
        fx.Clock.Now = Local(Today, 9, 30);
        Assert.True((await RemindersFor(orgId)).Ran);
        Assert.Equal(["Sin Acceso Pérez tiene un cargo vencido hace 1 día"], Titles(await Inbox(owner)));
    }

    [Fact]
    public async Task The_worker_job_runs_every_active_organization_and_skips_suspended_ones()
    {
        var w = await NewWorld("remjob");
        var s = await NewWorld("remjobsusp");
        foreach (var x in new[] { w, s }) await Charge(x.Owner, x.LeaseA, 1000m, Today.AddDays(3));
        await Sql("UPDATE \"Organizations\" SET \"Status\" = 'Suspended' WHERE \"Id\" = {0}", s.OrgId);
        fx.Clock.Now = Local(Today, 9, 30);
        await fx.Factory.Services.GetRequiredService<NotificationJobs>().RunRemindersAsync(default);
        Assert.Single(await Inbox(w.A));
        await Sql("UPDATE \"Organizations\" SET \"Status\" = 'Active' WHERE \"Id\" = {0}", s.OrgId);
    }

    // ---------- email and push ----------

    [Fact]
    public async Task A_notification_is_emailed_and_pushed_once_with_the_name_in_the_subject_and_no_amounts_in_the_push()
    {
        var w = await NewWorld("deliver");
        var endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.PostAsJsonAsync("/api/v1/notifications/push/subscriptions", new { endpoint, keys = new { p256dh = "BPk-key", auth = "auth-secret" } })).StatusCode);
        await ReportClaim(w.A, 1234.50m);

        Assert.True(await DispatchFor(w.OrgId) > 0);
        var mail = Assert.Single(fx.Email.To(w.OwnerEmail));
        Assert.Equal("Ana Ejemplo reportó un pago", mail.Subject);
        Assert.Contains("$1,234.50", mail.Body);                       // the owner's own inbox may carry the detail
        Assert.Contains("http://localhost:3000/claims", mail.Body);    // an absolute link back into the app
        var push = Assert.Single(fx.Push.Sent, p => p.Target.Endpoint == endpoint);
        Assert.Equal("Ana Ejemplo reportó un pago", push.Message.Title);
        Assert.Equal("Abre Habita para ver el detalle.", push.Message.Body);
        Assert.DoesNotContain("1,234", push.Message.Body); Assert.DoesNotContain("1,234", push.Message.Title); // a lock screen shows no amounts
        Assert.Equal("/claims", push.Message.Url);

        Assert.Equal(0, await DispatchFor(w.OrgId));                  // nothing is sent twice
        Assert.Single(fx.Email.To(w.OwnerEmail)); Assert.Single(fx.Push.Sent, p => p.Target.Endpoint == endpoint);
        Assert.Empty(fx.Email.To(w.EmailA)); // the tenant had nothing to be told
    }

    [Fact]
    public async Task Preferences_switch_email_and_push_off_per_person_and_the_app_list_stays()
    {
        var w = await NewWorld("prefs");
        var admin = await fx.AddMemberAsync(w.Owner, "Admin");
        Assert.True((await Json(await w.Owner.GetAsync("/api/v1/notifications/preferences"))).GetProperty("emailEnabled").GetBoolean());
        var endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
        await w.Owner.PostAsJsonAsync("/api/v1/notifications/push/subscriptions", new { endpoint, keys = new { p256dh = "k", auth = "a" } });
        var updated = await Json(await w.Owner.PutAsJsonAsync("/api/v1/notifications/preferences", new { emailEnabled = false, pushEnabled = false }));
        Assert.False(updated.GetProperty("emailEnabled").GetBoolean()); Assert.False(updated.GetProperty("pushEnabled").GetBoolean());
        Assert.True((await Json(await admin.GetAsync("/api/v1/notifications/preferences"))).GetProperty("emailEnabled").GetBoolean()); // a colleague's choice is untouched

        await ReportClaim(w.A);
        await DispatchFor(w.OrgId);
        Assert.Empty(fx.Email.To(w.OwnerEmail)); Assert.DoesNotContain(fx.Push.Sent, p => p.Target.Endpoint == endpoint);
        Assert.Single(await Inbox(w.Owner));                           // still in the app
        var me = await UserId(w.Owner);
        Assert.Equal(0, await Db(db => db.Notifications.IgnoreQueryFilters().CountAsync(n => n.UserId == me && n.DeliveredAt == null))); // finished, not stuck retrying

        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PutAsJsonAsync("/api/v1/notifications/preferences", new { emailEnabled = true })).StatusCode);
        Assert.False((await Json(await w.Owner.GetAsync("/api/v1/notifications/preferences"))).GetProperty("pushEnabled").GetBoolean()); // only what was sent changes
        await ReportClaim(w.A);
        await DispatchFor(w.OrgId);
        Assert.Single(fx.Email.To(w.OwnerEmail));
    }

    /// <summary>PostgreSQL keeps microseconds; a Linux clock has 100 ns ticks. Tests that compare a stored time with the clock must use whole microseconds.</summary>
    static DateTimeOffset Micros(DateTimeOffset t) => new(t.Ticks - t.Ticks % 10, t.Offset);

    [Fact]
    public async Task A_failed_email_is_retried_later_with_a_delay_and_never_sent_twice_once_it_works()
    {
        var w = await NewWorld("retry");
        var start = fx.Clock.Now = Micros(DateTimeOffset.UtcNow);
        fx.Email.FailFor = to => to == w.OwnerEmail;
        await ReportClaim(w.A);
        var me = await UserId(w.Owner);
        Task<(int Attempts, DateTimeOffset? Next, DateTimeOffset? Done)> State() => Db(async db =>
        {
            var n = await db.Notifications.IgnoreQueryFilters().SingleAsync(x => x.UserId == me);
            return (n.DeliveryAttempts, n.NextAttemptAt, n.DeliveredAt);
        });

        await DispatchFor(w.OrgId);
        var s1 = await State();
        Assert.Equal(1, s1.Attempts); Assert.Null(s1.Done);
        Assert.Equal(start + TimeSpan.FromMinutes(1), s1.Next);

        await DispatchFor(w.OrgId);                                    // not due yet: no new attempt
        Assert.Equal(1, (await State()).Attempts);

        fx.Clock.Now = start.AddMinutes(2); await DispatchFor(w.OrgId);
        var s2 = await State();
        Assert.Equal(2, s2.Attempts); Assert.Equal(start.AddMinutes(2) + TimeSpan.FromMinutes(5), s2.Next);

        fx.Email.FailFor = null; fx.Clock.Now = start.AddMinutes(10); await DispatchFor(w.OrgId);
        Assert.Single(fx.Email.To(w.OwnerEmail));
        Assert.NotNull((await State()).Done);
        fx.Clock.Now = start.AddHours(3); await DispatchFor(w.OrgId);
        Assert.Single(fx.Email.To(w.OwnerEmail));
    }

    [Fact]
    public async Task After_five_failures_it_gives_up_and_the_app_entry_remains()
    {
        var w = await NewWorld("giveup");
        var start = fx.Clock.Now = Micros(DateTimeOffset.UtcNow);
        fx.Email.FailFor = to => to == w.OwnerEmail;
        await ReportClaim(w.A);
        for (var i = 0; i < 8; i++) { fx.Clock.Now = start.AddHours(i * 2); await DispatchFor(w.OrgId); }
        var me = await UserId(w.Owner);
        var n = await Db(db => db.Notifications.IgnoreQueryFilters().SingleAsync(x => x.UserId == me));
        Assert.Equal(Habita.Domain.Notifications.Notification.MaxDeliveryAttempts, n.DeliveryAttempts);
        Assert.NotNull(n.DeliveredAt);
        Assert.Empty(fx.Email.To(w.OwnerEmail));
        Assert.Single(await Inbox(w.Owner));
    }

    [Fact]
    public async Task A_push_that_fails_is_retried_without_resending_the_email_and_a_gone_device_is_forgotten()
    {
        var w = await NewWorld("pushretry");
        var start = fx.Clock.Now = Micros(DateTimeOffset.UtcNow);
        var live = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}"; var dead = $"https://updates.push.services.mozilla.com/wpush/v2/{Guid.NewGuid():N}";
        await w.Owner.PostAsJsonAsync("/api/v1/notifications/push/subscriptions", new { endpoint = live, keys = new { p256dh = "k", auth = "a" } });
        await w.Owner.PostAsJsonAsync("/api/v1/notifications/push/subscriptions", new { endpoint = dead, keys = new { p256dh = "k", auth = "a" } });
        fx.Push.Outcome = t => t.Endpoint == dead ? PushOutcome.Gone : PushOutcome.Failed;
        await ReportClaim(w.A);

        await DispatchFor(w.OrgId);
        Assert.Single(fx.Email.To(w.OwnerEmail));                       // the email went out on the first try
        Assert.DoesNotContain(fx.Push.Sent, p => p.Target.Endpoint == live);
        var me = await UserId(w.Owner);
        Assert.Equal(1, await Db(db => db.PushSubscriptions.CountAsync(s => s.UserId == me))); // the dead device is gone, the live one stays

        fx.Push.Outcome = _ => PushOutcome.Sent; fx.Clock.Now = start.AddMinutes(2); await DispatchFor(w.OrgId);
        Assert.Single(fx.Push.Sent, p => p.Target.Endpoint == live);
        Assert.Single(fx.Email.To(w.OwnerEmail));                       // still one email
    }

    [Fact]
    public async Task An_old_notification_is_not_emailed_but_stays_in_the_app()
    {
        var w = await NewWorld("stale");
        var start = fx.Clock.Now = Micros(DateTimeOffset.UtcNow);
        await ReportClaim(w.A);
        fx.Clock.Now = start.AddHours(49);                              // the worker was down for two days
        await DispatchFor(w.OrgId);
        Assert.Empty(fx.Email.To(w.OwnerEmail));
        Assert.Single(await Inbox(w.Owner));
        var me = await UserId(w.Owner);
        Assert.Equal(0, await Db(db => db.Notifications.IgnoreQueryFilters().CountAsync(n => n.UserId == me && n.DeliveredAt == null)));
    }

    [Fact]
    public async Task Several_workers_delivering_at_once_send_each_email_exactly_once()
    {
        var w = await NewWorld("dispatchrace");
        for (var i = 0; i < 5; i++) { await ReportClaim(w.A, 10m + i); await ReportClaim(w.B, 20m + i); }   // ten notifications for the owner
        var jobs = fx.Factory.Services.GetRequiredService<NotificationJobs>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => jobs.DispatchAsync(default)));
        Assert.Equal(10, fx.Email.To(w.OwnerEmail).Count);
        Assert.Equal(5, fx.Email.To(w.OwnerEmail).Count(m => m.Subject == "Ana Ejemplo reportó un pago"));
        Assert.Equal(5, fx.Email.To(w.OwnerEmail).Count(m => m.Subject == "Luis Prueba reportó un pago"));
    }

    // ---------- push devices ----------

    [Fact]
    public async Task Push_devices_must_be_real_push_services_and_belong_to_one_person()
    {
        var w = await NewWorld("pushdev");
        var keys = new { p256dh = "BPk", auth = "au" };
        var cfg = await Json(await w.A.GetAsync("/api/v1/notifications/push/config"));
        Assert.True(cfg.GetProperty("enabled").GetBoolean()); Assert.Equal("BFAKE-PUBLIC-KEY", cfg.GetProperty("publicKey").GetString());

        foreach (var bad in new[] { "http://fcm.googleapis.com/x", "https://localhost/x", "https://127.0.0.1/x", "https://169.254.169.254/latest", "https://user:pw@fcm.googleapis.com/x",
                                    "https://fcm.googleapis.com:8443/x", "https://evil.example/x", "https://fcm.googleapis.com.evil.example/x", "no es una url", "" })
            Assert.Equal(HttpStatusCode.BadRequest, (await w.A.PostAsJsonAsync("/api/v1/notifications/push/subscriptions", new { endpoint = bad, keys })).StatusCode);
        var ok = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.BadRequest, (await w.A.PostAsJsonAsync("/api/v1/notifications/push/subscriptions", new { endpoint = ok, keys = new { p256dh = "", auth = "au" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.A.PostAsJsonAsync("/api/v1/notifications/push/subscriptions", new { endpoint = ok })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await w.A.PostAsJsonAsync("/api/v1/notifications/push/subscriptions", new { endpoint = ok, keys })).StatusCode); // a tenant can subscribe too
        Assert.Equal(HttpStatusCode.NoContent, (await w.A.PostAsJsonAsync("/api/v1/notifications/push/subscriptions", new { endpoint = ok, keys })).StatusCode); // idempotent
        var ana = await UserId(w.A);
        Assert.Equal(1, await Db(db => db.PushSubscriptions.CountAsync(s => s.UserId == ana && s.Endpoint == ok)));

        // The same browser signs in as someone else: the device follows the current person and the previous one can no longer remove it.
        Assert.Equal(HttpStatusCode.NoContent, (await w.B.PostAsJsonAsync("/api/v1/notifications/push/subscriptions", new { endpoint = ok, keys })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.A.PostAsJsonAsync("/api/v1/notifications/push/unsubscribe", new { endpoint = ok })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.B.PostAsJsonAsync("/api/v1/notifications/push/unsubscribe", new { endpoint = ok })).StatusCode);

        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.PostAsJsonAsync("/api/v1/notifications/push/subscriptions", new { endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}", keys })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync("/api/v1/notifications/push/subscriptions", new { endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}", keys })).StatusCode);

        fx.Push.Enabled = false;                                        // a server with no VAPID keys
        var off = await Json(await w.A.GetAsync("/api/v1/notifications/push/config"));
        Assert.False(off.GetProperty("enabled").GetBoolean()); Assert.Equal(JsonValueKind.Null, off.GetProperty("publicKey").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await w.A.PostAsJsonAsync("/api/v1/notifications/push/subscriptions", new { endpoint = ok, keys })).StatusCode);
        fx.Push.Enabled = true;
    }

    [Fact]
    public async Task Notification_routes_need_a_session()
    {
        var anon = fx.NewClient();
        foreach (var url in new[] { "/api/v1/notifications", "/api/v1/notifications/unread-count", "/api/v1/notifications/stream", "/api/v1/notifications/preferences", "/api/v1/notifications/push/config" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/v1/notifications/read-all", null)).StatusCode);
    }

    // ---------- the worker ----------

    [Fact]
    public async Task The_workers_service_graph_builds_with_full_validation_and_resolves_its_jobs()
    {
        // The worker has no web host. This builds the same composition in Development, where .NET checks that every registered service can be
        // constructed: a missing piece fails here, not when someone starts ./dev.sh.
        var b = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, Args = [] });
        b.Configuration["ConnectionStrings:Default"] = fx.ConnectionString;
        b.Services.AddInfrastructure(b.Configuration, withIdentityServices: false);
        using var host = b.Build();
        Assert.NotNull(host.Services.GetRequiredService<NotificationJobs>());
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentOrganization>().Set(Guid.NewGuid(), Role.Owner, OrganizationStatus.Active);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ReminderService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<NotificationDispatcher>());
        Assert.Null(scope.ServiceProvider.GetRequiredService<ICurrentUser>().UserId); // nobody is "signed in" in the worker
        await Task.CompletedTask;
    }

    // ---------- live stream ----------

    static async Task<List<string>> ReadEventsAsync(HttpResponseMessage response, TimeSpan upTo, Func<List<string>, bool>? until = null)
    {
        using var cts = new CancellationTokenSource(upTo);
        var lines = new List<string>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        try
        {
            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                lines.Add(line);
                if (until?.Invoke(lines) == true) break;
            }
        }
        catch (OperationCanceledException) { /* time is up */ }
        return lines;
    }

    [Fact]
    public async Task The_live_stream_delivers_a_new_notification_to_its_owner_with_the_name_first()
    {
        var w = await NewWorld("sse");
        using var stream = await w.Owner.GetAsync("/api/v1/notifications/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", stream.Headers.CacheControl?.ToString() ?? "");

        _ = Task.Run(async () => { await Task.Delay(700); await ReportClaim(w.A, 321m); });
        var lines = await ReadEventsAsync(stream, TimeSpan.FromSeconds(8), l => l.Any(x => x.StartsWith("data:")));
        Assert.Contains("event: notification", lines);
        var data = JsonDocument.Parse(lines.First(x => x.StartsWith("data:"))[5..].Trim()).RootElement;
        Assert.Equal("Ana Ejemplo reportó un pago", data.GetProperty("title").GetString());
        Assert.Equal("ClaimSubmitted", data.GetProperty("type").GetString());
    }

    [Fact]
    public async Task The_live_stream_never_carries_someone_elses_notifications_and_ends_on_its_own()
    {
        var w = await NewWorld("sseiso");
        var (stranger, _, _) = await fx.RegisterAsync("sseiso2");
        using var tenantB = await w.B.GetAsync("/api/v1/notifications/stream", HttpCompletionOption.ResponseHeadersRead);
        using var other = await stranger.GetAsync("/api/v1/notifications/stream", HttpCompletionOption.ResponseHeadersRead);
        _ = Task.Run(async () => { await Task.Delay(500); await ReportClaim(w.A, 55m); });

        var started = DateTimeOffset.UtcNow;
        var bLines = ReadEventsAsync(tenantB, TimeSpan.FromSeconds(12));
        var otherLines = ReadEventsAsync(other, TimeSpan.FromSeconds(12));
        var results = await Task.WhenAll(bLines, otherLines);
        foreach (var lines in results) Assert.DoesNotContain(lines, l => l.StartsWith("event: notification") || l.StartsWith("data:"));
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(11), "the stream should close by itself after its maximum lifetime so the session is re-checked");
    }

    [Fact]
    public async Task One_person_can_hold_only_a_few_streams_open()
    {
        var w = await NewWorld("ssecap");
        var open = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < 5; i++) { var r = await w.Owner.GetAsync("/api/v1/notifications/stream", HttpCompletionOption.ResponseHeadersRead); Assert.Equal(HttpStatusCode.OK, r.StatusCode); open.Add(r); }
            Assert.Equal(HttpStatusCode.TooManyRequests, (await w.Owner.GetAsync("/api/v1/notifications/stream", HttpCompletionOption.ResponseHeadersRead)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await w.A.GetAsync("/api/v1/notifications/stream", HttpCompletionOption.ResponseHeadersRead)).StatusCode); // another person is unaffected
        }
        finally { foreach (var r in open) r.Dispose(); }
        await Task.Delay(1500); // the slots free up when the connections close
        using var again = await w.Owner.GetAsync("/api/v1/notifications/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }
}
