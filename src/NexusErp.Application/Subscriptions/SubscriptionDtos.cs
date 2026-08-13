using NexusErp.Domain.Enums;

namespace NexusErp.Application.Subscriptions;

public sealed record PlanListItem(
    Guid Id, string Code, string Name, decimal Price, string Currency,
    BillingCycle Cycle, int TrialDays, bool IsActive,
    int ActiveSubscriptions, decimal MonthlyValue);

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
    decimal Amount, string Currency, bool AlreadyBilled);

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
