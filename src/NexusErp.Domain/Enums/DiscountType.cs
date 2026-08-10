namespace NexusErp.Domain.Enums;

public enum DiscountType
{
    None = 0,
    Percentage = 1,   // oran (%10 → 0,10)
    Amount = 2        // tutar (100 TL)
}
