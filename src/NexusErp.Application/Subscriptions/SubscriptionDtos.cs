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

public sealed record SubscriptionStats(
    int ActiveCount,
    decimal Mrr,
    decimal Arr,
    int RenewingThisMonth,
    int PastDueCount);

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
