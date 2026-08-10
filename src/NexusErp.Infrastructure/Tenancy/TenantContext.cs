using Microsoft.Extensions.Options;
using NexusErp.Application.Abstractions;

namespace NexusErp.Infrastructure.Tenancy;

public sealed class TenantContext(IOptions<TenantOptions> options) : ITenantContext
{
    private Guid? _override;

    /// <summary>
    /// Bölüm 12'de kimlik doğrulama geldiğinde SetTenant, oturum açan kullanıcının
    /// tenant'ıyla çağrılacak. O zamana kadar yapılandırmadaki varsayılan geçerli.
    /// </summary>
    public Guid TenantId => _override ?? options.Value.DefaultTenantId;

    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new InvalidOperationException("Tenant kimliği boş olamaz.");
        _override = tenantId;
    }
}
