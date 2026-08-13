using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Domain.Subscriptions;

namespace NexusErp.Application.Subscriptions;

public sealed record UsageEntry(
    Guid SubscriptionId,
    decimal Quantity,
    DateOnly? OccurredOn = null,
    string? Description = null,
    string? ExternalId = null);

public sealed record UsageListItem(
    Guid Id, DateOnly OccurredOn, decimal Quantity, string? Description,
    string? ExternalId, bool IsBilled, string? InvoiceNumber);

/// <summary>
/// Cari dönem kullanım özeti — abonelik ekranındaki "şu ana kadar" kutusu.
/// <paramref name="Billable"/> ücretsiz kota düşüldükten SONRA kalan miktardır.
/// </summary>
public sealed record UsageSummary(
    Guid SubscriptionId,
    string? UnitName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal PeriodQuantity,
    decimal Allowance,
    decimal Billable,
    decimal OveragePrice,
    string Currency,
    decimal UnbilledQuantity)
{
    public decimal EstimatedAmount =>
        Math.Round(Billable * OveragePrice, 2, MidpointRounding.AwayFromZero);

    public decimal AllowanceRemaining => Math.Max(0m, Allowance - PeriodQuantity);

    public decimal AllowanceUsedPercent => Allowance <= 0
        ? 100m
        : Math.Min(100m, Math.Round(PeriodQuantity / Allowance * 100m, 1,
                                    MidpointRounding.AwayFromZero));
}

/// <summary>
/// Kullanım kayıtları — metered (kullanım bazlı) faturalandırmanın veri girişi.
///
/// ⚠️ Bu servis TOPLAM tutmaz, OLAY yazar. Fatura tutarı bu olayların toplamına
/// dayandığı için her kayıt denetlenebilir olmak zorunda: müşteri "bu 4.312 SMS
/// nereden çıktı" dediğinde satır satır gösterebilmeliyiz.
/// </summary>
public sealed class UsageService(IAppDbContextFactory factory, TimeProvider clock)
{
    /// <summary>
    /// Kullanım kaydeder. <paramref name="entry"/>.ExternalId verilmişse aynı kayıt
    /// ikinci kez yazılamaz — entegrasyonun yeniden denemesi ücreti ikiye katlamaz.
    /// Tekrar eden çağrıda mevcut kaydın kimliği döner (idempotent).
    /// </summary>
    public async Task<Guid> RecordAsync(UsageEntry entry, CancellationToken ct = default)
    {
        if (entry.Quantity == 0m)
            throw new DomainException("Kullanım miktarı sıfır olamaz.");

        await using var db = factory.Create();

        var sub = await db.Subscriptions
            .Include(s => s.Plan)
            .FirstOrDefaultAsync(s => s.Id == entry.SubscriptionId, ct)
            ?? throw new DomainException("Abonelik bulunamadı.");

        if (!sub.Plan.IsMetered)
            throw new DomainException(
                $"'{sub.Plan.Name}' kullanım bazlı bir plan değil, kullanım kaydedilemez.");

        if (sub.Status is SubscriptionStatus.Cancelled)
            throw new DomainException("İptal edilmiş aboneliğe kullanım kaydedilemez.");

        var occurredOn = entry.OccurredOn ?? DateOnly.FromDateTime(clock.GetUtcNow().Date);

        if (occurredOn < sub.StartDate)
            throw new DomainException(
                $"Kullanım tarihi abonelik başlangıcından ({sub.StartDate:dd.MM.yyyy}) önce olamaz.");

        var externalId = string.IsNullOrWhiteSpace(entry.ExternalId)
            ? null : entry.ExternalId.Trim();

        // Ön kontrol PERFORMANS için; garanti unique index'te. Yarış koşulunda
        // ikinci yazma DbUpdateException alır ve aşağıda tekrar okunur.
        if (externalId is not null)
        {
            var existing = await db.UsageRecords
                .Where(u => u.SubscriptionId == sub.Id && u.ExternalId == externalId)
                .Select(u => u.Id)
                .FirstOrDefaultAsync(ct);

            if (existing != Guid.Empty) return existing;
        }

        var record = new UsageRecord
        {
            SubscriptionId = sub.Id,
            OccurredOn = occurredOn,
            Quantity = entry.Quantity,
            Description = entry.Description,
            ExternalId = externalId
        };

        db.UsageRecords.Add(record);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (externalId is not null)
        {
            // Yarış koşulu: aynı ExternalId iki kez aynı anda geldi. Hata değil —
            // idempotency'nin çalıştığının kanıtı. Kazanan kaydın kimliğini döndür.
            await using var retry = factory.Create();
            return await retry.UsageRecords
                .Where(u => u.SubscriptionId == sub.Id && u.ExternalId == externalId)
                .Select(u => u.Id)
                .FirstAsync(ct);
        }

        return record.Id;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = factory.Create();

        var record = await db.UsageRecords.FirstOrDefaultAsync(u => u.Id == id, ct)
                     ?? throw new DomainException("Kullanım kaydı bulunamadı.");

        record.EnsureEditable();            // faturalanmışsa reddeder
        record.IsDeleted = true;

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<UsageListItem>> ListAsync(
        Guid subscriptionId, int take = 100, CancellationToken ct = default)
    {
        await using var db = factory.Create();

        return await db.UsageRecords.AsNoTracking()
            .Where(u => u.SubscriptionId == subscriptionId)
            .OrderByDescending(u => u.OccurredOn).ThenByDescending(u => u.CreatedAt)
            .Take(take)
            .Select(u => new UsageListItem(
                u.Id, u.OccurredOn, u.Quantity, u.Description, u.ExternalId,
                u.InvoiceId != null,
                db.Invoices.Where(i => i.Id == u.InvoiceId).Select(i => i.Number).FirstOrDefault()))
            .ToListAsync(ct);
    }

    /// <summary>
    /// İçinde bulunulan dönemin özeti.
    ///
    /// ⚠️ "Dönem" = bir SONRAKİ faturanın kapsayacağı değil, ŞU AN İŞLEYEN dönem.
    /// NextBillingDate peşin ücretin tarihidir; işleyen dönem ondan bir önceki
    /// aralıktır. Bunu karıştırırsak müşteriye henüz başlamamış bir dönemin
    /// kullanımını gösteririz ve rakam hep sıfır çıkar.
    /// </summary>
    public async Task<UsageSummary?> GetSummaryAsync(
        Guid subscriptionId, CancellationToken ct = default)
    {
        await using var db = factory.Create();

        var sub = await db.Subscriptions.AsNoTracking()
            .Include(s => s.Plan)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);

        if (sub is null || !sub.Plan.IsMetered) return null;

        var periodStart = BillingSchedule.PreviousPeriodStart(
            sub.NextBillingDate, sub.Plan.Cycle, sub.BillingAnchorDay);

        if (periodStart < sub.StartDate) periodStart = sub.StartDate;

        var periodEnd = sub.NextBillingDate.AddDays(-1);

        var periodQuantity = await db.UsageRecords.AsNoTracking()
            .Where(u => u.SubscriptionId == sub.Id
                     && u.OccurredOn >= periodStart && u.OccurredOn <= periodEnd)
            .SumAsync(u => (decimal?)u.Quantity, ct) ?? 0m;

        // Faturalanmamış TOPLAM: geç gelen eski dönem kayıtları da buna dahil,
        // bir sonraki faturada tahsil edilecekler.
        var unbilled = await db.UsageRecords.AsNoTracking()
            .Where(u => u.SubscriptionId == sub.Id && u.InvoiceId == null)
            .SumAsync(u => (decimal?)u.Quantity, ct) ?? 0m;

        var allowance = sub.Plan.AllowanceFor(sub.Quantity);

        return new UsageSummary(
            SubscriptionId: sub.Id,
            UnitName: sub.Plan.UsageUnitName,
            PeriodStart: periodStart,
            PeriodEnd: periodEnd,
            PeriodQuantity: periodQuantity,
            Allowance: allowance,
            Billable: Math.Max(0m, periodQuantity - allowance),
            OveragePrice: sub.Plan.OveragePrice,
            Currency: sub.Plan.Currency,
            UnbilledQuantity: unbilled);
    }
}
