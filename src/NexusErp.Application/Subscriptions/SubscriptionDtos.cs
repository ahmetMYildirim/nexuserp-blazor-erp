using NexusErp.Domain.Enums;

namespace NexusErp.Application.Subscriptions;

public sealed record PlanListItem(
    Guid Id, string Code, string Name, decimal Price, string Currency,
    BillingCycle Cycle, int TrialDays, bool IsActive,
    int ActiveSubscriptions, decimal MonthlyValue,
    BillingModel BillingModel = BillingModel.Flat, string? UsageUnitName = null,
    decimal IncludedUnits = 0m, decimal OveragePrice = 0m)
{
    public bool IsMetered => BillingModel is BillingModel.Metered or BillingModel.Hybrid;
    public bool HasFlatFee => BillingModel is BillingModel.Flat or BillingModel.Hybrid;

    public string BillingModelText => BillingModel switch
    {
        BillingModel.Flat => "Sabit ücret",
        BillingModel.Metered => "Kullanım bazlı",
        BillingModel.Hybrid => "Sabit + kullanım",
        _ => "?"
    };

    /// <summary>"100 SMS dahil, sonrası 2,00 ₺" gibi tek satırlık özet.</summary>
    public string UsageText
    {
        get
        {
            if (!IsMetered) return string.Empty;
            var unit = UsageUnitName ?? "birim";
            return IncludedUnits > 0
                ? $"{IncludedUnits:N0} {unit} dahil, sonrası {OveragePrice:N2} {Currency}"
                : $"{unit} başına {OveragePrice:N2} {Currency}";
        }
    }
}

public sealed record SubscriptionListItem(
    Guid Id,
    string PartyTitle,
    string PlanName,
    SubscriptionStatus Status,
    DateOnly StartDate,
    DateOnly NextBillingDate,
    int BillingAnchorDay,
    BillingCycle Cycle,
    decimal EffectivePrice,
    string Currency,
    decimal Quantity)
{
    public decimal MonthlyValue =>
        Math.Round(EffectivePrice * Quantity / (int)Cycle, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Yeni abonelik formu.</summary>
public sealed class SubscriptionForm
{
    public Guid PartyId { get; set; }
    public Guid PlanId { get; set; }
    public DateOnly StartDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>1–31. Null ise başlangıç tarihinin günü çapa olur.</summary>
    public int? BillingAnchorDay { get; set; }

    /// <summary>Plan fiyatından farklı, cariye özel fiyat.</summary>
    public decimal? CustomPrice { get; set; }

    public decimal Quantity { get; set; } = 1m;

    /// <summary>
    /// Deneme başlatılsın mı? Null ise planın TrialDays değeri kullanılır.
    /// 0 verilirse deneme yok, abonelik doğrudan Active başlar.
    /// </summary>
    public int? TrialDays { get; set; }

    public string? Notes { get; set; }
}

/// <summary>Sihirbazdaki "ilk fatura ne zaman, ne kadar" önizlemesi.</summary>
public sealed record SubscriptionPreview(
    DateOnly FirstBillingDate, decimal FirstAmount, string Currency,
    DateOnly? TrialEndsOn, DateOnly PeriodStart, DateOnly PeriodEnd, string CycleText);

/// <summary>Abonelik detay ekranı — tek sorgu turunda üretilir.</summary>
public sealed record SubscriptionDetail(
    Guid Id,
    Guid PartyId,
    string PartyTitle,
    Guid PlanId,
    string PlanName,
    string PlanCode,
    BillingCycle Cycle,
    string CycleText,
    SubscriptionStatus Status,
    string StatusText,
    DateOnly StartDate,
    DateOnly? EndDate,
    DateOnly? TrialEndsOn,
    DateOnly? CancelledOn,
    DateOnly? PausedOn,
    DateOnly NextBillingDate,
    int BillingAnchorDay,
    decimal EffectivePrice,
    decimal? CustomPrice,
    decimal PlanPrice,
    string Currency,
    decimal Quantity,
    string? Notes,
    IReadOnlyList<SubscriptionInvoiceRow> Invoices)
{
    public decimal PeriodAmount =>
        Math.Round(EffectivePrice * Quantity, 2, MidpointRounding.AwayFromZero);

    public decimal MonthlyValue =>
        Math.Round(PeriodAmount / (int)Cycle, 2, MidpointRounding.AwayFromZero);

    public int InvoiceCount => Invoices.Count;

    public decimal TotalBilled => Invoices.Sum(i => i.GrandTotal);

    public decimal TotalOutstanding => Invoices.Sum(i => i.GrandTotal - i.PaidAmount);

    /// <summary>Bir sonraki faturaya kaç gün kaldı. Negatifse gecikmiş.</summary>
    public int DaysToNextBilling =>
        NextBillingDate.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber;

    public bool CanBill => Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue;
}

/// <summary>Aboneliğin ürettiği faturalar — zaman çizelgesinin gövdesi.</summary>
public sealed record SubscriptionInvoiceRow(
    Guid Id, string? Number, DateOnly IssueDate,
    DateOnly? PeriodStart, DateOnly? PeriodEnd,
    decimal GrandTotal, decimal PaidAmount, InvoiceStatus Status)
{
    public decimal Remaining => GrandTotal - PaidAmount;
}

/// <summary>Plan değişikliği önizlemesi — onaylamadan önce farkı göster.</summary>
public sealed record PlanChangePreview(
    string CurrentPlanName, string NewPlanName,
    decimal CurrentPeriodPrice, decimal NewPeriodPrice,
    DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly ChangeDate,
    decimal ProrationAmount, DateOnly NextBillingDate, string Currency)
{
    public bool IsUpgrade => ProrationAmount > 0;

    public string Explanation => ProrationAmount switch
    {
        > 0 => $"Dönem sonuna kadar kalan süre için {ProrationAmount:N2} {Currency} " +
               "fark faturalanacak.",
        < 0 => $"Kullanılmayan süre için {Math.Abs(ProrationAmount):N2} {Currency} " +
               "alacak oluşacak.",
        _ => "Dönem ortası fark oluşmuyor."
    };
}

/// <summary>Tek bir iptal sebebinin churn içindeki payı.</summary>
public sealed record ChurnReasonRow(
    CancellationReason Reason, string Label, int Count, decimal LostMrr)
{
    public static string TextOf(CancellationReason r) => r switch
    {
        CancellationReason.TooExpensive => "Fiyat yüksek",
        CancellationReason.NotUsing => "Kullanmıyor",
        CancellationReason.SwitchedToCompetitor => "Rakibe geçti",
        CancellationReason.MissingFeatures => "Eksik özellik",
        CancellationReason.TemporaryPause => "Geçici ara",
        CancellationReason.BusinessClosed => "Müşteri kapandı",
        CancellationReason.PaymentFailure => "Ödeme alınamadı",
        CancellationReason.Other => "Diğer",
        _ => "Belirtilmemiş"
    };
}

/// <summary>
/// Churn analizi. Oran zaten hesaplanabiliyordu; buradaki değer NEDEN
/// kaybedildiğini göstermek — fiyat sorunu ile ürün sorunu farklı aksiyon ister.
/// </summary>
public sealed record ChurnAnalysis(
    DateOnly From, DateOnly To,
    int CancelledCount, decimal LostMrr,
    IReadOnlyList<ChurnReasonRow> Reasons)
{
    public ChurnReasonRow? TopReason =>
        Reasons.Count == 0 ? null : Reasons.MaxBy(r => r.Count);

    public string Summary => CancelledCount == 0
        ? "Bu dönemde iptal yok."
        : $"{CancelledCount} iptal, aylık {LostMrr:N2} kayıp. " +
          $"En sık sebep: {TopReason?.Label ?? "—"}.";
}

public sealed record SubscriptionStats(
    int ActiveCount,
    decimal Mrr,
    decimal Arr,
    int RenewingThisMonth,
    int PastDueCount);

/// <summary>Faturalandırma turunda kesilecek tek bir fatura.</summary>
public sealed record BillingPreviewRow(
    Guid SubscriptionId, string PartyTitle, string PlanName,
    DateOnly PeriodStart, DateOnly PeriodEnd,
    decimal Amount, string Currency, bool AlreadyBilled,
    decimal UsageQuantity = 0m, decimal UsageAmount = 0m, string? UsageUnitName = null)
{
    public bool HasUsage => UsageAmount != 0m;

    /// <summary>Sabit ucret kismi — kullanim disindaki tutar.</summary>
    public decimal FlatAmount => Amount - UsageAmount;

    public string UsageText => HasUsage
        ? $"{UsageQuantity:N2} {UsageUnitName ?? "birim"} = {UsageAmount:N2}"
        : "-";
}

/// <summary>
/// Faturalandırma turunun ÖNİZLEMESİ — hiçbir şey kaydetmez.
/// Muhasebeci körlemesine buton basmak istemez; ne kesileceğini önce görmeli.
/// </summary>
public sealed record BillingRunPreview(
    DateOnly AsOf, IReadOnlyList<BillingPreviewRow> Rows)
{
    /// <summary>Gerçekten fatura üretecek satırlar (zaten faturalanmışlar hariç).</summary>
    public IReadOnlyList<BillingPreviewRow> Billable =>
        [.. Rows.Where(r => !r.AlreadyBilled)];

    public int BillableCount => Billable.Count;
    public int SkipCount => Rows.Count - BillableCount;
    public decimal Total => Billable.Sum(r => r.Amount);
    public string Currency => Rows.Count == 0 ? "TRY" : Rows[0].Currency;

    public string Summary => (BillableCount, SkipCount) switch
    {
        (0, 0) => "Vadesi gelen abonelik yok.",
        (0, > 0) => $"{SkipCount} abonelik bu dönem için zaten faturalanmış — yeni fatura üretilmeyecek.",
        (_, 0) => $"{BillableCount} fatura kesilecek, toplam {Total:N2} {Currency}.",
        _ => $"{BillableCount} fatura kesilecek ({Total:N2} {Currency}), " +
             $"{SkipCount} abonelik zaten faturalanmış olduğu için atlanacak."
    };
}

/// <summary>Faturalandırma turunun sonucu.</summary>
public sealed record BillingRunResult(int Created, int Skipped, int Failed)
{
    public string Summary => (Created, Skipped, Failed) switch
    {
        (0, 0, 0) => "Vadesi gelen abonelik yok.",
        (0, > 0, _) => $"Yeni fatura üretilmedi — {Skipped} abonelik bu dönem için zaten faturalanmış.",
        (> 0, 0, 0) => $"{Created} fatura kesildi.",
        (> 0, > 0, _) => $"{Created} fatura kesildi, {Skipped} abonelik atlandı (zaten faturalanmış).",
        _ => $"{Created} fatura kesildi, {Failed} abonelik hata verdi."
    };
}
