using Microsoft.Extensions.Options;
using NexusErp.Application.Abstractions;

namespace NexusErp.Infrastructure.Tenancy;

/// <summary>
/// Tenant tutucusu. Değeri KİM yazar, barındıran uygulamaya göre değişir:
///
///   • Blazor Server → AuthStateInitializer bileşeni, devre açılırken
///     AuthenticationStateProvider'dan okuyup SetTenant çağırır.
///   • REST API      → ClaimsTenantContext, HttpContext'ten okur (orada güvenli).
///
/// ⚠️ Neden Blazor'da IHttpContextAccessor kullanmıyoruz? Microsoft interaktif render
/// için bunu ÖNERMİYOR: devre (circuit) açıldıktan sonra HttpContext null olabilir ve
/// AsyncLocal üzerinden devreler arası sızabilir. Sessizce varsayılan tenant'a düşmek,
/// çok kiracılı bir sistemde veri sızıntısı demektir.
/// </summary>
public sealed class TenantContext(IOptions<TenantOptions> options) : ITenantContext
{
    private Guid? _override;

    public Guid TenantId => _override ?? options.Value.DefaultTenantId;

    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new InvalidOperationException("Tenant kimliği boş olamaz.");
        _override = tenantId;
    }
}

/// <summary>
/// created_by / updated_by kaynağı. Blazor'da AuthStateInitializer, API'de
/// ClaimsCurrentUser doldurur; hiçbiri çalışmazsa "system" kalır (seed/migration).
/// </summary>
public sealed class MutableCurrentUser : ICurrentUser
{
    private string _userName = "system";

    public string UserName => _userName;

    public void SetUser(string? userName)
        => _userName = string.IsNullOrWhiteSpace(userName) ? "system" : userName;
}
