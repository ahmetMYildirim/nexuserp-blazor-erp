namespace NexusErp.Domain.Enums;

public enum LedgerEntryType
{
    Invoice = 1,          // satış faturası      → borç
    InvoiceReturn = 2,    // iade faturası       → alacak
    Payment = 3,          // tahsilat            → alacak
    Refund = 4,           // iade ödemesi        → borç
    Adjustment = 5,       // düzeltme / ters kayıt

    // ⚠️ Alış tarafı satışın AYNASI: tedarikçiye borçlanırız (alacak),
    // ödediğimizde borç azalır. Yön karışırsa cari bakiye ters çıkar.
    PurchaseInvoice = 6,  // alış faturası       → alacak (tedarikçiye borç)
    PurchaseReturn = 7,   // alış iade           → borç
    Disbursement = 8      // tediye (para çıkışı)→ borç
}
