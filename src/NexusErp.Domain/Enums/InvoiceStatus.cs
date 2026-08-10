namespace NexusErp.Domain.Enums;

public enum InvoiceStatus
{
    Draft = 0,          // taslak — düzenlenebilir, silinebilir
    Issued = 1,         // kesildi — artık değiştirilemez
    PartiallyPaid = 2,
    Paid = 3,
    Cancelled = 9
}
