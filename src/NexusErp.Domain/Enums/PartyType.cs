namespace NexusErp.Domain.Enums;

/// <summary>
/// [Flags] çünkü gerçek hayatta bir firma hem müşterin hem tedarikçin olabilir.
/// İki ayrı kart açmak veri tekrarı ve mutabakat kâbusu doğurur.
/// </summary>
[Flags]
public enum PartyType
{
    None = 0,
    Customer = 1,
    Supplier = 2,
    Both = Customer | Supplier
}
