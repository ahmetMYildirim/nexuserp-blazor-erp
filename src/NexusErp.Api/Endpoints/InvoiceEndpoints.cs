using NexusErp.Application.Abstractions;
using NexusErp.Application.Invoicing;
using NexusErp.Application.Payments;
using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

namespace NexusErp.Api.Endpoints;

public static class InvoiceEndpoints
{
    public static void MapInvoiceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/faturalar")
            .WithTags("Fatura")
            .RequireAuthorization()
            .RequireRateLimiting("api");

        group.MapGet("/", async (
            InvoiceService service, string? ara, InvoiceStatus? durum,
            bool vadesiGecen = false, int sayfa = 0, int adet = 25) =>
        {
            var result = await service.SearchAsync(new InvoiceQuery(
                Search: ara, Status: durum, OnlyOverdue: vadesiGecen,
                Page: sayfa, PageSize: adet));

            return Results.Ok(new { toplam = result.TotalCount, kayitlar = result.Items });
        })
        .WithSummary("Fatura listesi");

        group.MapGet("/{id:guid}", async (Guid id, InvoiceService service) =>
            await service.GetFormAsync(id) is { } form ? Results.Ok(form) : Results.NotFound())
        .WithSummary("Fatura detayı (satırlarıyla)");

        group.MapPost("/", async (InvoiceForm form, InvoiceService service) =>
        {
            try
            {
                var id = await service.SaveDraftAsync(form);
                return Results.Created($"/api/faturalar/{id}", new { id, durum = "Taslak" });
            }
            catch (DomainException ex)
            {
                return Results.Problem(ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "İş kuralı ihlali");
            }
        })
        .WithSummary("Taslak fatura oluştur")
        .WithDescription("Hesaplama (KDV, tevkifat, iskonto) sunucuda yapılır — " +
                         "gönderdiğiniz toplamlar dikkate ALINMAZ.")
        .RequireAuthorization(p => p.RequireRole("Admin", "Muhasebe", "Satis"));

        group.MapPost("/{id:guid}/kes", async (Guid id, InvoiceService service) =>
        {
            try
            {
                var number = await service.IssueAsync(id);
                return Results.Ok(new { faturaNo = number });
            }
            catch (DomainException ex)
            {
                return Results.Problem(ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "İş kuralı ihlali");
            }
        })
        .WithSummary("Taslağı resmî faturaya çevir")
        .WithDescription("Atomik numara üretilir; kesilen fatura ARTIK DEĞİŞTİRİLEMEZ.")
        .RequireAuthorization(p => p.RequireRole("Admin", "Muhasebe"));

        group.MapGet("/{id:guid}/pdf", async (Guid id, IInvoicePdfGenerator pdf) =>
            Results.File(await pdf.GenerateAsync(id), "application/pdf", $"{id}.pdf"))
        .WithSummary("Fatura PDF'i");

        group.MapGet("/{id:guid}/ubl", async (Guid id, IUblInvoiceBuilder ubl) =>
        {
            try
            {
                var doc = await ubl.BuildAsync(id);
                return Results.Text(doc.Xml, "application/xml");
            }
            catch (DomainException ex)
            {
                return Results.Problem(ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        })
        .WithSummary("e-Fatura XML (UBL-TR 1.2)")
        .WithDescription("GİB e-Fatura formatında XML. Entegratöre gönderilmeye hazır.");

        // ---------------- Tahsilat ----------------
        var payments = app.MapGroup("/api/tahsilatlar")
            .WithTags("Tahsilat")
            .RequireAuthorization(p => p.RequireRole("Admin", "Muhasebe"))
            .RequireRateLimiting("api");

        payments.MapPost("/", async (PaymentForm form, PaymentService service) =>
        {
            try
            {
                var id = await service.CreateAsync(form);
                return Results.Created($"/api/tahsilatlar/{id}", new { id });
            }
            catch (DomainException ex)
            {
                return Results.Problem(ex.Message,
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "İş kuralı ihlali");
            }
        })
        .WithSummary("Tahsilat kaydet")
        .WithDescription("AutoAllocate=true ise açık faturalara vade sırasına göre (FIFO) dağıtılır.");

        payments.MapGet("/yaslandirma", async (PartyBalanceService service, DateOnly? tarih) =>
            Results.Ok(await service.GetAgingAsync(tarih ?? DateOnly.FromDateTime(DateTime.Today))))
        .WithSummary("Yaşlandırma raporu");

        payments.MapGet("/bakiye/{partyId:guid}", async (Guid partyId, PartyBalanceService service) =>
            Results.Ok(new { partyId, bakiye = await service.GetBalanceAsync(partyId) }))
        .WithSummary("Cari bakiyesi");
    }
}
