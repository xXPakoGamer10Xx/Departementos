using Habita.Application.Notifications;
using Habita.Domain.Notifications;
using Habita.Domain.Payments;
using Habita.Domain.Settings;
using Xunit;

namespace Habita.UnitTests;

public class PersonNameTests
{
    [Theory]
    [InlineData("Ana Ejemplo", "Ana Ejemplo")]
    [InlineData("  Ana   María  Pérez   López ", "Ana María Pérez López")]
    [InlineData("Valeria Irene Ortiz", "Valeria Irene Ortiz")]
    [InlineData("Alba Eunice Armijo Ogazon", "Alba Eunice Armijo Ogazon")]
    [InlineData("\tLuis\nPrueba ", "Luis Prueba")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void The_name_keeps_its_order_and_only_loses_stray_whitespace(string? input, string expected) =>
        Assert.Equal(expected, PersonName.Display(input));

    [Fact]
    public void A_name_is_never_turned_into_surname_comma_name()
    {
        var shown = PersonName.Display("María Fernanda Pérez López");
        Assert.DoesNotContain(",", shown);
        Assert.StartsWith("María Fernanda", shown);
        Assert.EndsWith("Pérez López", shown);
    }
}

public class NotificationTextTests
{
    const string Name = "María Fernanda Pérez López";
    static readonly DateOnly Day = new(2026, 10, 17);

    static IEnumerable<NotificationMessage> AllAboutAPerson() =>
    [
        NotificationText.ClaimSubmitted(Name, 1000m, PaymentMethod.BankTransfer, Day),
        NotificationText.IssueReported(Name, "Fuga", "101", Guid.NewGuid()),
        NotificationText.ClaimConfirmed(Name, 1000m, Day),
        NotificationText.ClaimRejected(Name, 1000m, "No coincide"),
        NotificationText.CashCollected(Name, 500m),
        NotificationText.RentDueSoon(Name, "Renta octubre", 8500m, Day, 3),
        NotificationText.RentDueSoon(Name, "Renta octubre", 8500m, Day, 0),
        NotificationText.ChargeOverdueForTenant(Name, "Renta octubre", 8500m, 7),
        NotificationText.ChargeOverdueForStaff(Name, "Renta octubre", 8500m, 7, "101"),
        NotificationText.LeaseExpiringForTenant(Name, "101", Day, 30),
        NotificationText.LeaseExpiringForStaff(Name, "101", Day, 30, Guid.NewGuid()),
        NotificationText.PromiseDue(Name, "Renta octubre", 8500m),
        NotificationText.PromiseBroken(Name, "Renta octubre", 8500m, Day),
    ];

    [Fact]
    public void Every_notification_about_a_person_opens_with_the_first_name_and_then_the_surnames()
    {
        foreach (var m in AllAboutAPerson())
        {
            Assert.StartsWith(Name, m.Title); // "María Fernanda Pérez López ..." exactly, in that order
            Assert.True(m.Title.IndexOf("María", StringComparison.Ordinal) < m.Title.IndexOf("Pérez", StringComparison.Ordinal), m.Title);
            Assert.DoesNotContain("Pérez López, María", m.Title);
        }
    }

    [Fact]
    public void Every_message_is_valid_and_fits_its_columns()
    {
        var longText = new string('x', 900);
        var messages = AllAboutAPerson().Concat([NotificationText.ClaimRejected(Name, 1m, longText), NotificationText.IssueReported(Name, longText, "101", Guid.NewGuid())]);
        foreach (var m in messages)
            Assert.Null(Notification.Validate(m.Title, m.Body, m.Url));
    }

    [Fact]
    public void Amounts_and_dates_are_written_for_Mexico()
    {
        Assert.Equal("$8,500.00", NotificationText.Money(8500m));
        Assert.Equal("17 de octubre de 2026", NotificationText.Date(Day));
        Assert.Equal("1 día", NotificationText.Days(1));
        Assert.Equal("3 días", NotificationText.Days(3));
    }

    [Fact]
    public void Rent_reminders_say_today_or_in_how_many_days()
    {
        Assert.Equal($"{Name}, tu renta vence hoy", NotificationText.RentDueSoon(Name, "R", 1m, Day, 0).Title);
        Assert.Equal($"{Name}, tu renta vence en 1 día", NotificationText.RentDueSoon(Name, "R", 1m, Day, 1).Title);
        Assert.Equal($"{Name}, tu renta vence en 3 días", NotificationText.RentDueSoon(Name, "R", 1m, Day, 3).Title);
    }

    [Fact]
    public void A_rejection_shows_the_reason_the_staff_wrote() =>
        Assert.Equal("El monto no coincide", NotificationText.ClaimRejected(Name, 100m, "El monto no coincide").Body);
}

public class NotificationRulesTests
{
    [Theory]
    [InlineData("/claims", true)]
    [InlineData("/portal/pay", true)]
    [InlineData("claims", false)]
    [InlineData("//evil.example/x", false)]
    [InlineData("https://evil.example/x", false)]
    [InlineData("/a\\b", false)]
    public void Links_are_internal_paths_only(string url, bool valid) =>
        Assert.Equal(valid, Notification.Validate("Título", "Texto", url) is null);

    [Fact]
    public void Title_and_body_have_limits()
    {
        Assert.NotNull(Notification.Validate("", "x", "/"));
        Assert.NotNull(Notification.Validate(new string('t', 201), "x", "/"));
        Assert.NotNull(Notification.Validate("t", new string('b', 501), "/"));
        Assert.Null(Notification.Validate("t", "", "/"));
    }

    [Fact]
    public void Retry_delays_grow_and_stop_growing()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), Notification.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(5), Notification.Backoff(2));
        Assert.Equal(TimeSpan.FromMinutes(15), Notification.Backoff(3));
        Assert.Equal(TimeSpan.FromHours(1), Notification.Backoff(4));
        Assert.Equal(TimeSpan.FromHours(1), Notification.Backoff(9));
    }

    static readonly string[] Allowed = [.. PushSubscription.DefaultAllowedHosts];

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc", true)]
    [InlineData("https://web.push.apple.com/QGx", true)]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=x", true)]
    [InlineData("https://FCM.GoogleAPIs.com/x", true)]
    [InlineData("http://fcm.googleapis.com/x", false)]                    // not https
    [InlineData("https://fcm.googleapis.com:8443/x", false)]              // not the default port
    [InlineData("https://user:pass@fcm.googleapis.com/x", false)]         // credentials
    [InlineData("https://localhost/x", false)]
    [InlineData("https://127.0.0.1/x", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]       // cloud metadata address
    [InlineData("https://[::1]/x", false)]
    [InlineData("https://api/x", false)]                                  // an internal service name
    [InlineData("https://evilfcm.googleapis.com.evil.example/x", false)]  // the allowed host only as a prefix
    [InlineData("https://notfcm.googleapis.com/x", false)]                // a real label boundary is required: this is a subdomain of googleapis.com, not of fcm.googleapis.com
    [InlineData("https://xfcm.googleapis.com.example/x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a url", false)]
    [InlineData("ftp://fcm.googleapis.com/x", false)]
    public void Only_real_push_services_are_accepted(string? endpoint, bool expected) =>
        Assert.Equal(expected, PushSubscription.IsAllowedEndpoint(endpoint, Allowed));

    [Fact]
    public void An_extra_configured_host_is_accepted_and_the_lookalike_is_not()
    {
        string[] hosts = [.. Allowed, "push.example.org"];
        Assert.True(PushSubscription.IsAllowedEndpoint("https://push.example.org/x", hosts));
        Assert.True(PushSubscription.IsAllowedEndpoint("https://eu.push.example.org/x", hosts));
        Assert.False(PushSubscription.IsAllowedEndpoint("https://evilpush.example.org/x", hosts));
        Assert.False(PushSubscription.IsAllowedEndpoint("https://push.example.org.evil.net/x", hosts));
    }

    [Theory]
    [InlineData("BPk=", "abc", true)]
    [InlineData("", "abc", false)]
    [InlineData("BPk=", "", false)]
    [InlineData(null, null, false)]
    public void Subscription_keys_are_required(string? p256dh, string? auth, bool valid) => Assert.Equal(valid, PushSubscription.ValidateKeys(p256dh, auth) is null);

    [Theory]
    [InlineData(9, null, "2026-10-17", 9, true)]     // never ran, the hour has come
    [InlineData(9, null, "2026-10-17", 8, false)]    // too early
    [InlineData(9, "2026-10-16", "2026-10-17", 9, true)]
    [InlineData(9, "2026-10-17", "2026-10-17", 23, false)] // already ran today
    [InlineData(9, "2026-10-17", "2026-10-18", 0, false)]  // new day, but before the hour
    [InlineData(9, "2026-10-17", "2026-10-18", 9, true)]
    [InlineData(0, null, "2026-10-17", 0, true)]     // midnight reminders
    [InlineData(23, null, "2026-10-17", 22, false)]
    [InlineData(9, "2026-10-01", "2026-10-17", 15, true)] // the worker was down for days: run once, today
    public void The_daily_run_happens_once_from_the_reminder_hour(int hour, string? last, string today, int localHour, bool due) =>
        Assert.Equal(due, OrganizationSettings.IsRunDue(hour, last is null ? null : DateOnly.Parse(last), DateOnly.Parse(today), localHour));
}
