using System.Globalization;

namespace Habita.Domain.Documents;

/// <summary>How amounts and dates are written in Spanish (Mexico) in notifications, contracts and receipts.</summary>
public static class SpanishFormat
{
    private static readonly CultureInfo Mx = new("es-MX");

    public static string Money(decimal amount) => amount.ToString("C2", Mx);
    public static string Date(DateOnly d) => d.ToString("d 'de' MMMM 'de' yyyy", Mx);
    public static string ShortDate(DateOnly d) => d.ToString("dd/MM/yyyy", Mx);
    public static string DateTime(DateTimeOffset t, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(t, zone).ToString("d 'de' MMMM 'de' yyyy, HH:mm", Mx);
    public static string Days(int n) => n == 1 ? "1 día" : $"{n} días";
}
