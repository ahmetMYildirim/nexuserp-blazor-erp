using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

namespace NexusErp.Domain.Invoicing;

public sealed record LineInput(
    decimal Quantity,
    decimal UnitPrice,
    DiscountType DiscountType = DiscountType.None,
    decimal DiscountValue = 0m,
    decimal TaxRate = 0m,
    decimal? WithholdingRate = null);

public sealed record LineResult(
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal DocumentDiscountShare,
    decimal TaxBase,
    decimal TaxAmount,
    decimal WithholdingAmount,
    decimal LineTotal);

public sealed record DocumentResult(
    IReadOnlyList<LineResult> Lines,
    decimal GrossTotal,
    decimal DiscountTotal,
    decimal TaxBaseTotal,
    decimal TaxTotal,
    decimal WithholdingTotal,
    decimal GrandTotal);

/// <summary>
/// Saf fonksiyon: girdi al, çıktı ver, yan etki yok, veri tabanı yok.
/// Bu yüzden testleri milisaniyeler içinde koşar ve UI önizlemesi ile sunucu kaydı
/// AYNI kodu çağırabilir — iki hesap zamanla ayrışamaz.
/// </summary>
public static class InvoiceCalculator
{
    /// <summary>
    /// Kuruşa yuvarlama. GİB standardı SATIR BAZINDA yuvarlama ister; belge toplamı
    /// yuvarlanmış satırların toplamıdır. Tersini yaparsan satırların toplamı ile
    /// genel toplam tutmaz ve muhasebeci ilk gün fark eder (ADR-003).
    /// </summary>
    private static decimal R(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    // ------------------------------------------------------------------
    // 1) TEK SATIR
    // ------------------------------------------------------------------

    public static LineResult CalculateLine(LineInput input, decimal documentDiscountShare = 0m)
    {
        Validate(input);

        // Adım 1: brüt
        var gross = R(input.Quantity * input.UnitPrice);

        // Adım 2: satır iskontosu
        var discount = input.DiscountType switch
        {
            DiscountType.Percentage => R(gross * input.DiscountValue),
            DiscountType.Amount => R(input.DiscountValue),
            _ => 0m
        };

        if (discount > gross)
            throw new DomainException("Satır iskontosu, satır tutarını aşamaz.");

        // Adım 3: KDV matrahı (satır iskontosu + belge iskontosu payı düşülmüş)
        var taxBase = R(gross - discount - documentDiscountShare);
        if (taxBase < 0)
            throw new DomainException("İskontolar toplamı satır tutarını aşamaz.");

        // Adım 4: KDV
        var tax = R(taxBase * input.TaxRate);

        // Adım 5: tevkifat — KDV'nin bir kısmını satıcı değil, ALICI devlete öder
        var withholding = input.WithholdingRate is { } w ? R(tax * w) : 0m;

        // Adım 6: satır toplamı
        var lineTotal = R(taxBase + tax - withholding);

        return new LineResult(gross, discount, documentDiscountShare,
                              taxBase, tax, withholding, lineTotal);
    }

    // ------------------------------------------------------------------
    // 2) BELGE
    // ------------------------------------------------------------------

    public static DocumentResult CalculateDocument(
        IReadOnlyList<LineInput> lines,
        DiscountType documentDiscountType = DiscountType.None,
        decimal documentDiscountValue = 0m)
    {
        if (lines.Count == 0)
            throw new DomainException("Fatura en az bir satır içermelidir.");

        // Adım A: belge iskontosu olmadan ön hesap — dağıtım ağırlıklarını bulmak için
        var draft = lines.Select(l => CalculateLine(l)).ToArray();
        var netWeights = draft.Select(d => d.GrossAmount - d.DiscountAmount).ToArray();
        var netSum = netWeights.Sum();

        // Adım B: belge iskontosu tutarı
        var documentDiscount = documentDiscountType switch
        {
            DiscountType.Percentage => R(netSum * documentDiscountValue),
            DiscountType.Amount => R(documentDiscountValue),
            _ => 0m
        };

        if (documentDiscount > netSum)
            throw new DomainException("Belge iskontosu, fatura tutarını aşamaz.");

        // Adım C: satırlara orantılı dağıt (kuruş artığı dahil)
        var shares = Allocate(documentDiscount, netWeights);

        // Adım D: kesin hesap
        var results = new LineResult[lines.Count];
        for (var i = 0; i < lines.Count; i++)
            results[i] = CalculateLine(lines[i], shares[i]);

        return new DocumentResult(
            Lines: results,
            GrossTotal: results.Sum(r => r.GrossAmount),
            DiscountTotal: R(results.Sum(r => r.DiscountAmount + r.DocumentDiscountShare)),
            TaxBaseTotal: results.Sum(r => r.TaxBase),
            TaxTotal: results.Sum(r => r.TaxAmount),
            WithholdingTotal: results.Sum(r => r.WithholdingAmount),
            GrandTotal: results.Sum(r => r.LineTotal));
    }

    // ------------------------------------------------------------------
    // 3) KURUŞ DAĞITIMI ("penny allocation")
    // ------------------------------------------------------------------

    /// <summary>
    /// Bir tutarı ağırlıklara orantılı dağıtır ve yuvarlamadan kalan kuruşu EN BÜYÜK
    /// ağırlıklı satıra ekler. Böylece parçaların toplamı kaynağa TAM eşit olur.
    ///
    /// 100 TL'yi üç eşit satıra dağıt:
    ///   naif:  33,33 + 33,33 + 33,33 = 99,99   → 1 kuruş kayıp ✗
    ///   bizim: 33,34 + 33,33 + 33,33 = 100,00  ✓
    /// </summary>
    internal static decimal[] Allocate(decimal total, IReadOnlyList<decimal> weights)
    {
        var result = new decimal[weights.Count];
        if (total == 0m) return result;

        var sum = weights.Sum();
        if (sum <= 0m) return result;

        var allocated = 0m;
        for (var i = 0; i < weights.Count; i++)
        {
            result[i] = R(total * weights[i] / sum);
            allocated += result[i];
        }

        var remainder = R(total) - allocated;
        if (remainder != 0m)
        {
            var maxIndex = 0;
            for (var i = 1; i < weights.Count; i++)
                if (weights[i] > weights[maxIndex]) maxIndex = i;

            result[maxIndex] += remainder;
        }

        return result;
    }

    private static void Validate(LineInput l)
    {
        if (l.Quantity <= 0)
            throw new DomainException("Miktar sıfırdan büyük olmalıdır.");
        if (l.UnitPrice < 0)
            throw new DomainException("Birim fiyat negatif olamaz.");
        if (l.TaxRate is < 0m or > 1m)
            throw new DomainException("KDV oranı 0–1 aralığında olmalıdır (%20 → 0,20).");
        if (l.WithholdingRate is < 0m or > 1m)
            throw new DomainException("Tevkifat oranı 0–1 aralığında olmalıdır.");
        if (l.DiscountType == DiscountType.Percentage && l.DiscountValue is < 0m or > 1m)
            throw new DomainException("İskonto oranı 0–1 aralığında olmalıdır (%10 → 0,10).");
        if (l.DiscountType == DiscountType.Amount && l.DiscountValue < 0m)
            throw new DomainException("İskonto tutarı negatif olamaz.");
    }
}
