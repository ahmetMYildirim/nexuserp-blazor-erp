using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using NexusErp.Domain.Entities;
using NexusErp.Infrastructure.Identity;

namespace NexusErp.Infrastructure.Persistence;

public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantContext tenantContext,
    ICurrentUser currentUser)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IAppDbContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<TaxRate> TaxRates => Set<TaxRate>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<InvoiceCounter> InvoiceCounters => Set<InvoiceCounter>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<PartyLedgerEntry> PartyLedgerEntries => Set<PartyLedgerEntry>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<OutBoxMessage> OutboxMessages => Set<OutBoxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();


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

            // ⚠️ Anahtarları BİZ üretiyoruz (AuditableEntity: Guid.CreateVersion7()).
            // EF Core, Guid PK'yı konvansiyon gereği ValueGeneratedOnAdd sayar; dolu bir
            // anahtarla karşılaşınca "bu kayıt zaten var" varsayıp entity'yi Added yerine
            // MODIFIED işaretler. Sonuç: yeni satırlar INSERT değil UPDATE edilir ve
            // "0 row(s) affected" concurrency hatası alınır.
            if (typeof(AuditableEntity).IsAssignableFrom(clr))
                modelBuilder.Entity(clr).Property(nameof(AuditableEntity.Id)).ValueGeneratedNever();

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

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
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

        // Denetlenecek değişiklikleri TOPLA — henüz yazma.
        // ⚠️ base.SaveChangesAsync'ten ÖNCE olmak zorunda: kayıttan sonra entity'ler
        // Unchanged'a döner ve OriginalValue/CurrentValue farkı kaybolur.
        var pending = CollectAuditEntries(now, user);

        var result = await base.SaveChangesAsync(cancellationToken);

        if (pending.Count > 0)
        {
            AuditEntries.AddRange(pending);
            await base.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    private List<AuditEntry> CollectAuditEntries(DateTimeOffset now, string user)
    {
        var entries = new List<AuditEntry>();

        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            // Denetim kaydının kendisini denetleme — sonsuz döngü olur
            if (entry.Entity is AuditEntry) continue;

            // ⚠️ Outbox'ı da denetleme. OutBoxMessage : AuditableEntity olduğu için
            // aksi halde HER olay yazımı bir Insert denetimi, HER ProcessedAt
            // güncellemesi bir Update denetimi üretir. Denetim tablosu outbox'ın
            // İKİ KATI hızla büyür ve gerçek iş kayıtları çöpün içinde kaybolur.
            if (entry.Entity is OutBoxMessage) continue;

            // Idempotency defteri de altyapi kaydi — denetlenmez.
            if (entry.Entity is ProcessedMessage) continue;

            var action = entry.State switch
            {
                EntityState.Added => AuditAction.Insert,
                // Soft delete: yukarıdaki döngü Deleted'ı Modified'a çevirip
                // IsDeleted = true yaptı. Silme olarak kaydedilmesi gereken durum bu.
                EntityState.Modified when entry.Entity.IsDeleted => AuditAction.Delete,
                EntityState.Modified => AuditAction.Update,
                _ => (AuditAction?)null
            };

            if (action is null) continue;

            var changes = new Dictionary<string, object?>();

            foreach (var prop in entry.Properties)
            {
                // Gürültü: her kayıtta zaten değişen denetim alanlarını atla
                if (prop.Metadata.Name is nameof(AuditableEntity.UpdatedAt)
                                       or nameof(AuditableEntity.UpdatedBy)
                                       or nameof(AuditableEntity.CreatedAt)
                                       or nameof(AuditableEntity.CreatedBy))
                    continue;

                if (action == AuditAction.Insert)
                {
                    if (prop.CurrentValue is not null)
                        changes[prop.Metadata.Name] = prop.CurrentValue;
                }
                else if (prop.IsModified && !Equals(prop.OriginalValue, prop.CurrentValue))
                {
                    changes[prop.Metadata.Name] = new
                    {
                        eski = prop.OriginalValue,
                        yeni = prop.CurrentValue
                    };
                }
            }

            if (changes.Count == 0) continue;   // gerçekten değişen bir şey yok

            entries.Add(new AuditEntry
            {
                TenantId = entry.Entity is ITenantScoped s ? s.TenantId : CurrentTenantId,
                EntityName = entry.Entity.GetType().Name,
                EntityId = entry.Entity.Id.ToString(),
                Action = action.Value,
                UserName = user,
                OccurredAt = now,
                Changes = JsonSerializer.Serialize(changes, JsonOptions),

                // ⚠️ CreatedAt/CreatedBy'ı ELLE dolduruyoruz. Denetim kayıtları ikinci
                // turda base.SaveChangesAsync ile yazılıyor — yani bu override'ın audit
                // döngüsüne HİÇ uğramıyorlar. Set etmezsek CreatedAt 0001-01-01 kalır.
                CreatedAt = now,
                CreatedBy = user
            });
        }

        return entries;
    }

    /// <summary>
    /// ⚠️ UnsafeRelaxedJsonEscaping olmadan Türkçe karakterler ü gibi kaçış
    /// dizilerine dönüşür ve denetim kaydı gözle okunamaz hale gelir.
    /// "Unsafe" adı yanıltıcı — HTML'e basmadığın sürece güvenli.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
