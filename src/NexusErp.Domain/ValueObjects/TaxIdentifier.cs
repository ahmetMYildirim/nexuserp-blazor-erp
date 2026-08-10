using NexusErp.Domain.Common;

namespace NexusErp.Domain.ValueObjects;

public enum TaxIdKind { Vkn, Tckn }

/// <summary>
/// VKN (10 hane, tüzel kişi) veya TCKN (11 hane, gerçek kişi).
/// Her ikisinin de resmi kontrol basamağı algoritması uygulanır.
/// </summary>
public readonly record struct TaxIdentifier
{
    private readonly string? _value;

    public string Value => _value ?? string.Empty;
    public TaxIdKind Kind { get; }

    private TaxIdentifier(string value, TaxIdKind kind) => (_value, Kind) = (value, kind);

    public static TaxIdentifier Parse(string input) =>
        TryParse(input, out var result) ? result : throw new DomainException($"Geçersiz VKN/TCKN: {input}");

    public static bool TryParse(string? input, out TaxIdentifier result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        // boşluk, tire vb. ayıklanır: "123 456 78-90" → "1234567890"
        var s = new string(input.Where(char.IsAsciiDigit).ToArray());

        if (s.Length == 10 && IsValidVkn(s)) { result = new TaxIdentifier(s, TaxIdKind.Vkn); return true; }
        if (s.Length == 11 && IsValidTckn(s)) { result = new TaxIdentifier(s, TaxIdKind.Tckn); return true; }
        return false;
    }

    /// <summary>
    /// VKN kontrol basamağı (Gelir İdaresi algoritması).
    /// i = 1..9 için: tmp = (d[i] + 10 - i) mod 10
    ///               v[i] = tmp == 9 ? 9 : (tmp * 2^(10-i)) mod 9
    /// kontrol = (10 - (Σv mod 10)) mod 10
    /// </summary>
    private static bool IsValidVkn(string s)
    {
        var d = s.Select(c => c - '0').ToArray();
        var sum = 0;

        for (var j = 0; j < 9; j++)                  // j = i-1 (0 tabanlı)
        {
            var tmp = (d[j] + 9 - j) % 10;
            sum += tmp == 9 ? 9 : (tmp << (9 - j)) % 9;
        }

        return (10 - sum % 10) % 10 == d[9];
    }

    /// <summary>
    /// TCKN kontrol basamakları:
    /// d10 = ((d1+d3+d5+d7+d9) * 7 - (d2+d4+d6+d8)) mod 10
    /// d11 = (d1..d10 toplamı) mod 10
    /// </summary>
    private static bool IsValidTckn(string s)
    {
        var d = s.Select(c => c - '0').ToArray();
        if (d[0] == 0) return false;

        var odd = d[0] + d[2] + d[4] + d[6] + d[8];
        var even = d[1] + d[3] + d[5] + d[7];

        // ⚠️ C#'ta -3 % 10 == -3. Negatif mod koruması şart.
        var digit10 = ((odd * 7 - even) % 10 + 10) % 10;
        if (digit10 != d[9]) return false;

        var digit11 = d.Take(10).Sum() % 10;
        return digit11 == d[10];
    }

    public override string ToString() => Value;
}
