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
}
