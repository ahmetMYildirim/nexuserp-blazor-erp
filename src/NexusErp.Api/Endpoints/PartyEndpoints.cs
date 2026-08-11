using NexusErp.Application.Parties;
using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

namespace NexusErp.Api.Endpoints;

public static class PartyEndpoints
{
    public static void MapPartyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/cariler")
            .WithTags("Cari")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapGet("/", async (
            PartyService service, string? ara, PartyType? tip, int sayfa = 0, int adet = 25) =>
        {
            var result = await service.SearchAsync(
                new PartyQuery(Search: ara, Type: tip, Page: sayfa, PageSize: adet));

            return Results.Ok(new { toplam = result.TotalCount, kayitlar = result.Items });
        })
        .WithSummary("Cari listesi")
        .WithDescription("Sayfalama sunucu tarafında; tenant filtresi jetondaki " +
                         "tenant_id claim'inden otomatik uygulanır.");

        group.MapGet("/{id:guid}", async (Guid id, PartyService service) =>
            await service.GetFormAsync(id) is { } form
                ? Results.Ok(form)
                : Results.NotFound())
        .WithSummary("Cari detayı");

        group.MapPost("/", async (PartyForm form, PartyService service) =>
        {
            try
            {
                var id = await service.SaveAsync(form);
                return Results.Created($"/api/cariler/{id}", new { id });
            }
            catch (DomainException ex)
            {
                // İş kuralı ihlali → 422, teknik hata → 500 (UseExceptionHandler yakalar)
                return Results.Problem(ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "İş kuralı ihlali");
            }
        })
        .WithSummary("Cari oluştur veya güncelle")
        .RequireAuthorization(p => p.RequireRole("Admin", "Muhasebe", "Satis"));
    }
}
