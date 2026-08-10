namespace NexusErp.Domain.Common;

/// <summary>
/// Bu arayüzü implemente eden her entity, tenant bazlı global query filter'a takılır.
/// Filtre DbContext'te otomatik uygulanır — entity başına tekrar yazmaya gerek yok.
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; set; }
}
