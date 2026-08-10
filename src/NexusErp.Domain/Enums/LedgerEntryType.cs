namespace NexusErp.Domain.Enums;

public enum LedgerEntryType
{
    Invoice = 1,          // satış faturası      → borç
    InvoiceReturn = 2,    // iade faturası       → alacak
    Payment = 3,          // tahsilat            → alacak
    Refund = 4,           // iade ödemesi        → borç
    Adjustment = 5        // düzeltme / ters kayıt
}
