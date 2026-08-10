using Microsoft.EntityFrameworkCore;
using NexusErp.Domain.Entities;

namespace NexusErp.Application.Abstractions;

/// <summary>
/// Repository pattern bilinçli olarak YOK: DbSet&lt;T&gt; zaten bir repository,
/// DbContext zaten Unit of Work. Üstüne katman koymak Include/AsNoTracking/projeksiyon
/// yeteneklerini kaybettirir ve her modülde boilerplate ekler (Bölüm 05).
/// Test edilebilirlik bu arayüzle zaten sağlanıyor.
/// </summary>
public interface IAppDbContext
{
    /// <summary>
    /// Aktif tenant. IgnoreQueryFilters() kullanmak zorunda kalan sorgularda
    /// tenant koşulunu ELLE eklemek için gerekli — aksi halde veri sızar.
    /// </summary>
    Guid CurrentTenantId { get; }

    DbSet<Tenant> Tenants { get; }
    DbSet<Party> Parties { get; }
    DbSet<TaxRate> TaxRates { get; }
    DbSet<Product> Products { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<InvoiceLine> InvoiceLines { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Entity'yi değişiklik takibinden çıkarır.
    /// ⚠️ Blazor Server'da DbContext DEVRE ömrü boyunca yaşar. Başarısız bir kayıt
    /// denemesi Added durumundaki entity'yi takipte bırakır; kullanıcı düzeltip tekrar
    /// kaydettiğinde İKİ kayıt eklenmeye çalışılır ve unique index ihlali oluşur.
    /// </summary>
    void Detach(object entity);
}
