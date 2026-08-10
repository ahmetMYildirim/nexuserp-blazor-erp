namespace NexusErp.Domain.Enums;

public enum InvoiceType
{
    Sales = 1,          // satış faturası
    SalesReturn = 2,    // satış iade
    Proforma = 3,       // proforma — bağlayıcı değil, cari bakiyeye İŞLEMEZ
    Purchase = 4        // alış faturası — Faz 2
}
