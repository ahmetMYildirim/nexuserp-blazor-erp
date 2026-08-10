namespace NexusErp.Domain.Enums;

public enum SubscriptionStatus
{
    Trialing = 1,     // deneme sürecinde
    Active = 2,
    PastDue = 3,      // fatura kesildi, ödenmedi — dunning süreci
    Paused = 4,
    Cancelled = 9
}
