using NexusErp.Application.Subscriptions;

namespace NexusErp.Api.Endpoints;

public static class SubscriptionEndpoints
{
    public static void MapSubscriptionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/abonelikler")
            .WithTags("Abonelik")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapGet("/", async (SubscriptionService service, int sayfa = 0, int adet = 25) =>
        {
            var result = await service.SearchAsync(page: sayfa, pageSize: adet);
            return Results.Ok(new { toplam = result.TotalCount, kayitlar = result.Items });
        })
        .WithSummary("Abonelik listesi");

        group.MapGet("/planlar", async (SubscriptionService service) =>
            Results.Ok(await service.GetPlansAsync()))
        .WithSummary("Abonelik planları");

        group.MapGet("/ozet", async (SubscriptionService service) =>
            Results.Ok(await service.GetStatsAsync(DateOnly.FromDateTime(DateTime.Today))))
        .WithSummary("MRR / ARR ve abonelik özeti");

        // ------------------------------------------------------------------
        // Kullanım bazlı faturalandırmanın ENTEGRASYON uçları.
        // ⚠️ Kullanım verisi genellikle başka bir sistemden (santral, SMS ağ geçidi,
        // API kapısı) gelir; asıl giriş noktası ekran değil BU uçtur.
        // ------------------------------------------------------------------

        group.MapPost("/{id:guid}/kullanim", async (
            Guid id, UsageService usage, KullanimIstegi istek, CancellationToken ct) =>
        {
            var recordId = await usage.RecordAsync(new UsageEntry(
                SubscriptionId: id,
                Quantity: istek.Miktar,
                OccurredOn: istek.Tarih,
                Description: istek.Aciklama,
                ExternalId: istek.KaynakNo), ct);

            return Results.Ok(new { kayitId = recordId });
        })
        .WithSummary("Kullanım kaydı ekle")
        .WithDescription(
            "IDEMPOTENT: kaynakNo (external id) gönderilirse aynı kayıt ikinci kez " +
            "yazılmaz, mevcut kaydın kimliği döner. Ağ hatası sonrası yeniden deneme " +
            "müşteriye fazladan fatura çıkarmaz — kaynakNo GÖNDERİN.")
        .RequireAuthorization(p => p.RequireRole("Admin", "Muhasebe"));

        group.MapGet("/{id:guid}/kullanim", async (
            Guid id, UsageService usage, CancellationToken ct) =>
        {
            var summary = await usage.GetSummaryAsync(id, ct);

            if (summary is null)
                return Results.NotFound(new { hata = "Abonelik bulunamadı veya kullanım bazlı değil." });

            return Results.Ok(new
            {
                donem = new { baslangic = summary.PeriodStart, bitis = summary.PeriodEnd },
                birim = summary.UnitName,
                donemKullanimi = summary.PeriodQuantity,
                ucretsizKota = summary.Allowance,
                kalanKota = summary.AllowanceRemaining,
                ucretlendirilecek = summary.Billable,
                tahminiTutar = summary.EstimatedAmount,
                paraBirimi = summary.Currency,
                faturalanmamisToplam = summary.UnbilledQuantity
            });
        })
        .WithSummary("Cari dönem kullanım özeti");

        group.MapPost("/faturalandir", async (
            SubscriptionBillingService billing, DateOnly? tarih) =>
        {
            var result = await billing.RunAsync(tarih ?? DateOnly.FromDateTime(DateTime.Today));
            return Results.Ok(new
            {
                uretilen = result.Created,
                atlanan = result.Skipped,
                hatali = result.Failed,
                ozet = result.Summary
            });
        })
        .WithSummary("Vadesi gelen abonelikleri faturalandır")
        .WithDescription("IDEMPOTENT: aynı abonelik + aynı dönem için ikinci fatura üretilmez. " +
                         "Zamanlanmış bir görevden güvenle çağrılabilir.")
        .RequireAuthorization(p => p.RequireRole("Admin", "Muhasebe"));
    }

    /// <summary>
    /// Kullanım kaydı isteği.
    /// <paramref name="KaynakNo"/> doldurulduğunda çağrı idempotent olur —
    /// entegrasyonun yeniden denemesi kullanımı ikiye katlamaz.
    /// </summary>
    public sealed record KullanimIstegi(
        decimal Miktar, DateOnly? Tarih = null, string? Aciklama = null, string? KaynakNo = null);
}
