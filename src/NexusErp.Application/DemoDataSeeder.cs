using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Invoicing;
using NexusErp.Application.Payments;
using NexusErp.Domain.Enums;

namespace NexusErp.Application;

/// <summary>
/// Demo fatura ve tahsilat verisi — dashboard ile yaşlandırma raporu boş görünmesin diye.
/// Faturaları doğrudan INSERT etmek yerine gerçek servisler üzerinden üretiyoruz:
/// hesaplama motoru, numaralandırma ve cari hareket kaydı da böylece çalışmış oluyor.
/// </summary>
public sealed class DemoDataSeeder(
    IAppDbContext db,
    InvoiceService invoices,
    PaymentService payments)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        // Yalnızca bir kez: elle kesilmiş (NEX serisi) fatura varsa çık
        if (await db.Invoices.AnyAsync(i => i.Series == "NEX", ct)) return;

        var today = DateOnly.FromDateTime(DateTime.Today);

        var customers = await db.Parties
            .Where(p => (p.Type & PartyType.Customer) != 0)
            .OrderBy(p => p.Code)
            .Select(p => p.Id)
            .ToListAsync(ct);

        var products = await db.Products
            .OrderBy(p => p.Code)
            .Select(p => new
            {
                p.Id, p.Code, p.Name, p.Unit, p.UnitPrice,
                p.TaxRateId, Rate = p.TaxRate.Rate, p.WithholdingRate
            })
            .ToListAsync(ct);

        if (customers.Count < 4 || products.Count < 4) return;

        // (cari, ürün, miktar, fatura tarihi ofseti, vade ofseti) — yaşlandırma kovalarını doldurur
        var plan = new (int Party, int Product, decimal Qty, int Issue, int Due)[]
        {
            (0, 1, 8m,  -120, -100),   // 90+ gün gecikmiş
            (1, 0, 1m,   -95,  -65),   // 61–90 gün
            (2, 3, 2m,   -70,  -40),   // 31–60 gün
            (0, 2, 1m,   -45,  -15),   // 1–30 gün  (tevkifatlı hizmet)
            (3, 1, 4m,   -20,   10),   // vadesi gelmemiş
            (1, 3, 1m,    -8,   22),   // vadesi gelmemiş
        };

        foreach (var p in plan)
        {
            var product = products[p.Product];

            var id = await invoices.SaveDraftAsync(new InvoiceForm
            {
                PartyId = customers[p.Party],
                Series = "NEX",
                IssueDate = today.AddDays(p.Issue),
                DueDate = today.AddDays(p.Due),
                Lines =
                [
                    new InvoiceLineForm
                    {
                        ProductId = product.Id,
                        ProductCode = product.Code,
                        ProductName = product.Name,
                        Unit = product.Unit,
                        Quantity = p.Qty,
                        UnitPrice = product.UnitPrice,
                        TaxRateId = product.TaxRateId,
                        TaxRate = product.Rate,
                        WithholdingRate = product.WithholdingRate
                    }
                ]
            }, ct);

            await invoices.IssueAsync(id, ct);
        }

        // Tahsilatlar — "tahsilat oranı" ve kısmi ödeme durumları anlamlı olsun
        await payments.CreateAsync(new PaymentForm
        {
            PartyId = customers[0], Amount = 20_000m,
            PaymentDate = today.AddDays(-30), Method = PaymentMethod.BankTransfer,
            Reference = "DEKONT-2026-0041", AutoAllocate = true
        }, ct);

        await payments.CreateAsync(new PaymentForm
        {
            PartyId = customers[1], Amount = 5_000m,
            PaymentDate = today.AddDays(-10), Method = PaymentMethod.Cheque,
            Reference = "ÇEK-884213", AutoAllocate = true
        }, ct);
    }
}
