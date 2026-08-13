using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;

namespace NexusErp.Application.Messaging;

/// <summary>
/// Outbox sağlık göstergesi.
///
/// ⚠️ Asıl metrik BEKLEYEN SAYISI DEĞİL, EN ESKİ BEKLEYEN MESAJIN YAŞI.
/// 10.000 mesaj varsa ama hepsi 5 saniyelikse sistem sağlıklıdır; 3 mesaj varsa
/// ve en eskisi 2 saatlikse işçi ölmüş demektir. Alarm yaşa kurulur.
/// </summary>
public sealed record OutboxHealth(
    int Pending, int Failed, TimeSpan? OldestPendingAge, DateTimeOffset CheckedAt)
{
    /// <summary>5 dakikadan eski bekleyen mesaj varsa işçi takılmış olabilir.</summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);

    public bool IsHealthy => OldestPendingAge is null || OldestPendingAge < StaleThreshold;

    public string Status => IsHealthy ? "saglikli" : "gecikmis";

    public string Summary => OldestPendingAge switch
    {
        null => "Bekleyen mesaj yok.",
        var age when age < StaleThreshold =>
            $"{Pending} mesaj bekliyor, en eskisi {age.Value.TotalSeconds:N0} sn.",
        var age =>
            $"UYARI: en eski bekleyen mesaj {age.Value.TotalMinutes:N0} dakikadır gönderilmedi."
    };
}

public sealed class OutboxHealthService(IAppDbContextFactory factory, TimeProvider clock)
{
    public async Task<OutboxHealth> CheckAsync(CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var now = clock.GetUtcNow();

        // ⚠️ IgnoreQueryFilters: sağlık tüm tenant'ları kapsar. Filtre açık kalsaydı
        // yalnızca varsayılan tenant'ın mesajları sayılır ve sistem sağlıklı görünürdü.
        var pending = db.OutboxMessages.IgnoreQueryFilters()
            .Where(m => m.ProcessedAt == null);

        var count = await pending.CountAsync(ct);

        var failed = await db.OutboxMessages.IgnoreQueryFilters()
            .CountAsync(m => m.ProcessedAt == null && m.AttemptCount >= 5, ct);

        DateTimeOffset? oldest = count == 0
            ? null
            : await pending.MinAsync(m => (DateTimeOffset?)m.OccuredAt, ct);

        return new OutboxHealth(
            Pending: count,
            Failed: failed,
            OldestPendingAge: oldest is null ? null : now - oldest.Value,
            CheckedAt: now);
    }
}
