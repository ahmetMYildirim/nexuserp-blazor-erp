using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence;

public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantContext tenantContext,
    ICurrentUser currentUser)
    : DbContext(options), IAppDbContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<TaxRate> TaxRates => Set<TaxRate>();
    public DbSet<Product> Products => Set<Product>();

    /// <summary>
    /// Query filter içinden okunur. EF Core, DbContext ÜYESİNE yapılan erişimi sabit değil
    /// PARAMETRE olarak derler — model önbelleğe alınsa bile her sorguda güncel tenant kullanılır.
    /// </summary>
    public Guid CurrentTenantId => tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        // ToList(): koleksiyonu gezerken model üzerinde işlem yapıyoruz
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            var clr = entityType.ClrType;

            // EF Core 9'da entity başına TEK query filter olur — tenant ve soft delete birlikte
            if (typeof(ITenantScoped).IsAssignableFrom(clr))
                TenantFilterMethod.MakeGenericMethod(clr).Invoke(null, [modelBuilder, this]);
            else if (typeof(AuditableEntity).IsAssignableFrom(clr))
                SoftDeleteFilterMethod.MakeGenericMethod(clr).Invoke(null, [modelBuilder]);
        }

        base.OnModelCreating(modelBuilder);
    }

    // ------------------------------------------------------------------
    // Query filter'ları generic uygulamak için reflection köprüsü
    // ------------------------------------------------------------------

    private static readonly MethodInfo TenantFilterMethod = typeof(AppDbContext)
        .GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo SoftDeleteFilterMethod = typeof(AppDbContext)
        .GetMethod(nameof(ApplySoftDeleteFilter), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void ApplyTenantFilter<T>(ModelBuilder builder, AppDbContext context)
        where T : AuditableEntity, ITenantScoped
        => builder.Entity<T>()
                  .HasQueryFilter(e => !e.IsDeleted && e.TenantId == context.CurrentTenantId);

    private static void ApplySoftDeleteFilter<T>(ModelBuilder builder)
        where T : AuditableEntity
        => builder.Entity<T>().HasQueryFilter(e => !e.IsDeleted);

    // ------------------------------------------------------------------
    // SaveChanges: audit + tenant ataması + soft delete dönüşümü
    // ------------------------------------------------------------------

    public void Detach(object entity) => Entry(entity).State = EntityState.Detached;

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // ⚠️ Npgsql timestamptz kolonuna yalnızca offset'i SIFIR olan DateTimeOffset yazar.
        // DateTimeOffset.Now (+03:00) verirsen çalışma zamanı hatası alırsın.
        var now = DateTimeOffset.UtcNow;
        var user = currentUser.UserName;

        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = user;
                    if (entry.Entity is ITenantScoped scoped && scoped.TenantId == Guid.Empty)
                        scoped.TenantId = tenantContext.TenantId;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = user;
                    // oluşturma bilgisi değişmez olmalı
                    entry.Property(nameof(AuditableEntity.CreatedAt)).IsModified = false;
                    entry.Property(nameof(AuditableEntity.CreatedBy)).IsModified = false;
                    break;

                case EntityState.Deleted:
                    // Muhasebe verisi silinmez (ADR-009): DELETE → UPDATE is_deleted
                    entry.State = EntityState.Modified;
                    entry.Entity.IsDeleted = true;
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = user;
                    break;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
