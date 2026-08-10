using System.Globalization;
using NexusErp.Domain.Common;

namespace NexusErp.Domain.ValueObjects;

/// <summary>
/// Para. decimal kullanılır — double/float ikili kayan noktadır ve 0,1'i tam gösteremez,
/// bu da faturada kuruş farkı demektir.
/// </summary>
public readonly record struct Money
{
    private readonly string? _currency;

    public decimal Amount { get; }

    /// <summary>ISO 4217 kodu. default(Money) durumunda TRY kabul edilir.</summary>
    public string Currency => _currency ?? "TRY";

    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new DomainException("Para birimi 3 harfli ISO 4217 kodu olmalı (TRY, USD, EUR).");

        Amount = amount;
        _currency = currency.ToUpperInvariant();
    }

    public static Money Zero(string currency = "TRY") => new(0m, currency);
    public static Money Try(decimal amount) => new(amount, "TRY");

    /// <summary>
    /// Kuruşa yuvarlar. AwayFromZero — .NET varsayılanı ToEven'dır ("banker's rounding")
    /// ve 2,005 → 2,00 yapar. Türkiye'de beklenen davranış 2,005 → 2,01 (ADR-003).
    /// </summary>
    public Money Round() => new(Math.Round(Amount, 2, MidpointRounding.AwayFromZero), Currency);

    public Money Multiply(decimal factor) => new(Amount * factor, Currency);

    public static Money operator +(Money a, Money b) => new(a.Amount + Same(a, b).Amount, a.Currency);
    public static Money operator -(Money a, Money b) => new(a.Amount - Same(a, b).Amount, a.Currency);
    public static Money operator -(Money a) => new(-a.Amount, a.Currency);

    public static bool operator >(Money a, Money b) => a.Amount > Same(a, b).Amount;
    public static bool operator <(Money a, Money b) => a.Amount < Same(a, b).Amount;
    public static bool operator >=(Money a, Money b) => a.Amount >= Same(a, b).Amount;
    public static bool operator <=(Money a, Money b) => a.Amount <= Same(a, b).Amount;

    /// <summary>Para birimi uyuşmazlığı sessizce geçmemeli — 100 TRY + 50 USD hatadır.</summary>
    private static Money Same(Money a, Money b) => a.Currency == b.Currency
        ? b
        : throw new DomainException(
            $"Farklı para birimleri işleme sokulamaz: {a.Currency} / {b.Currency}.");

    public override string ToString() =>
        Amount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " " + Currency;
}
