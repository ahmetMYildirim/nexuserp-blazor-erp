namespace NexusErp.Application.Abstractions;

/// <summary>
/// Aktif isteğin hangi tenant'a ait olduğunu söyler.
/// ⚠️ Blazor Server'da scoped = DEVRE (circuit) ömrü, HTTP isteği ömrü değil.
/// Kullanıcı sekmeyi açık tuttuğu sürece aynı örnek yaşar.
/// </summary>
public interface ITenantContext
{
    Guid TenantId { get; }
    void SetTenant(Guid tenantId);
}
