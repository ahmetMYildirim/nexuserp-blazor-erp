namespace NexusErp.Domain.Enums;

/// <summary>
/// Değerler AY SAYISI olarak seçildi — (int)cycle doğrudan AddMonths parametresi
/// ve MRR normalizasyonunda bölen olarak kullanılıyor.
/// </summary>
public enum BillingCycle
{
    Monthly = 1,
    Quarterly = 3,
    SemiAnnual = 6,
    Yearly = 12
}
