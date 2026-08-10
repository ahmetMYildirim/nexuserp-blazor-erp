namespace NexusErp.Infrastructure.Tenancy;

public sealed class TenantOptions
{
    public const string SectionName = "Tenant";

    /// <summary>
    /// Kimlik doğrulama gelene kadar (Bölüm 12) kullanılan varsayılan tenant.
    /// appsettings.Development.json → "Tenant:DefaultTenantId"
    /// </summary>
    public Guid DefaultTenantId { get; set; }
}
