namespace NexusErp.Domain.Enums;

/// <summary>
/// Fişin kaynağı. Manuel fişte <see cref="Manual"/>, otomatik üretilenlerde
/// hangi belgeden doğduğu.
///
/// ⚠️ (SourceType, SourceId) çifti üzerinde unique index var: aynı faturadan
/// ikinci kez fiş kesilemez. Çift kayıt mizanı bozmaz (dengeli kalır) ama
/// cironun ve KDV'nin iki katı görünmesine yol açar — bu tür hata ancak
/// beyanname aşamasında fark edilir, o da geç olur.
/// </summary>
public enum JournalSourceType
{
    Manual = 1,           // elle girilen fiş
    SalesInvoice = 2,     // satış faturası kesildi
    PurchaseInvoice = 3,  // alış faturası kaydedildi
    Payment = 4,          // tahsilat işlendi
    PaymentReversal = 5   // tahsilat iptali (ters kayıt)
}
