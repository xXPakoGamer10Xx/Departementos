namespace Habita.Domain.Collections;

/// <summary>
/// CLABE interbancaria: 18 digits = bank (3) + branch (3) + account (11) + check digit (1).
/// The check digit weights the first 17 digits 3,7,1 repeating, takes each product mod 10, and completes the sum to a multiple of 10.
/// </summary>
public static class ClabeRules
{
    public const int Length = 18;
    private static readonly int[] Weights = [3, 7, 1];

    // Only banks whose code is certain. An unknown prefix is still a valid CLABE; it just has no name here.
    private static readonly Dictionary<string, string> Banks = new()
    {
        ["002"] = "Banamex", ["006"] = "Bancomext", ["009"] = "Banobras", ["012"] = "BBVA México", ["014"] = "Santander",
        ["019"] = "Banjercito", ["021"] = "HSBC", ["030"] = "BanBajío", ["036"] = "Inbursa", ["042"] = "Mifel",
        ["044"] = "Scotiabank", ["058"] = "Banregio", ["059"] = "Invex", ["062"] = "Afirme", ["072"] = "Banorte",
        ["127"] = "Banco Azteca", ["130"] = "Compartamos Banco", ["137"] = "BanCoppel", ["138"] = "ABC Capital",
        ["166"] = "Banco del Bienestar", ["638"] = "Nu México", ["646"] = "STP", ["722"] = "Mercado Pago",
    };

    /// <summary>Removes spaces and dashes (people paste it as "0123 4567 ..."). Null when empty.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = new string(input.Where(c => c is not (' ' or '-')).ToArray());
        return s.Length == 0 ? null : s;
    }

    public static int CheckDigit(ReadOnlySpan<char> first17)
    {
        var sum = 0;
        for (var i = 0; i < 17; i++) sum += ((first17[i] - '0') * Weights[i % 3]) % 10;
        return (10 - sum % 10) % 10;
    }

    public static bool IsValid(string? normalized) =>
        normalized is { Length: Length } && normalized.All(char.IsAsciiDigit) && CheckDigit(normalized.AsSpan(0, 17)) == normalized[17] - '0';

    /// <summary>Bank name from the first three digits, or null when the prefix is not in the catalogue.</summary>
    public static string? BankName(string normalized) => normalized.Length >= 3 && Banks.TryGetValue(normalized[..3], out var name) ? name : null;

    /// <summary>Groups the digits as bank, branch, account and check digit so it is easy to read aloud.</summary>
    public static string Format(string normalized) => $"{normalized[..3]} {normalized[3..6]} {normalized[6..17]} {normalized[17]}";
}
