using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Common;
using NexusErp.Infrastructure.Persistence;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NexusErp.Infrastructure.Documents;

public sealed class InvoicePdfGenerator(AppDbContext db) : IInvoicePdfGenerator
{
    public async Task<byte[]> GenerateAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var inv = await db.Invoices.AsNoTracking()
            .Include(i => i.Lines.OrderBy(l => l.LineNumber))
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new DomainException("Fatura bulunamadı.");

        var tenant = await db.Tenants.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(t => t.Id == inv.TenantId, ct);

        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                // Türkçe karakterler (ş, ğ, İ, ı) için tam destekli font.
                // Linux container'da fonts-dejavu-core kurulu olmalı (Dockerfile).
                page.DefaultTextStyle(t => t.FontSize(9).FontFamily("Arial", "DejaVu Sans"));

                page.Header().Column(header =>
                {
                    header.Item().Row(row =>
                    {
                        row.RelativeItem().Column(col =>
                        {
                            col.Item().Text(tenant.Name).Bold().FontSize(14);
                            col.Item().Text(tenant.Address ?? "");
                            col.Item().Text($"{tenant.City}");
                            col.Item().Text($"VD: {tenant.TaxOffice}   VKN: {tenant.TaxNumber}");
                        });

                        row.ConstantItem(190).Column(col =>
                        {
                            col.Item().AlignRight().Text(TitleOf(inv.Type)).Bold().FontSize(16);
                            col.Item().AlignRight().Text($"No: {inv.Number}");
                            col.Item().AlignRight().Text($"Tarih: {inv.IssueDate:dd.MM.yyyy}");
                            col.Item().AlignRight().Text($"Vade: {inv.DueDate:dd.MM.yyyy}");
                            col.Item().AlignRight().Text($"ETTN: {inv.Ettn}").FontSize(6);
                        });
                    });

                    header.Item().PaddingTop(8).LineHorizontal(1);
                });

                page.Content().PaddingVertical(10).Column(col =>
                {
                    // --- Cari kutusu ---
                    col.Item().Background(Colors.Grey.Lighten4).Padding(8).Column(c =>
                    {
                        c.Item().Text("SAYIN").FontSize(7).FontColor(Colors.Grey.Darken1);
                        c.Item().Text(inv.PartyTitle).Bold().FontSize(11);
                        if (!string.IsNullOrWhiteSpace(inv.PartyAddress))
                            c.Item().Text(inv.PartyAddress);
                        if (!string.IsNullOrWhiteSpace(inv.PartyTaxNumber))
                            c.Item().Text($"VD: {inv.PartyTaxOffice}   VKN/TCKN: {inv.PartyTaxNumber}");
                    });

                    // --- Satır tablosu ---
                    col.Item().PaddingTop(12).Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(20);    // sıra
                            c.RelativeColumn(4);     // ürün
                            c.ConstantColumn(45);    // miktar
                            c.ConstantColumn(35);    // birim
                            c.ConstantColumn(60);    // birim fiyat
                            c.ConstantColumn(45);    // KDV
                            c.ConstantColumn(65);    // matrah
                            c.ConstantColumn(65);    // toplam
                        });

                        table.Header(h =>
                        {
                            HeaderCell(h, "#");
                            HeaderCell(h, "Açıklama");
                            HeaderCell(h, "Miktar", true);
                            HeaderCell(h, "Birim");
                            HeaderCell(h, "Fiyat", true);
                            HeaderCell(h, "KDV", true);
                            HeaderCell(h, "Matrah", true);
                            HeaderCell(h, "Tutar", true);
                        });

                        foreach (var l in inv.Lines)
                        {
                            Cell(table, l.LineNumber.ToString());
                            Cell(table, l.ProductName);
                            Cell(table, l.Quantity.ToString("N2"), true);
                            Cell(table, l.Unit);
                            Cell(table, l.UnitPrice.ToString("N2"), true);
                            Cell(table, $"%{l.TaxRate * 100m:0.##}", true);
                            Cell(table, l.TaxBase.ToString("N2"), true);
                            Cell(table, l.LineTotal.ToString("N2"), true);
                        }
                    });

                    // --- Toplam paneli ---
                    col.Item().PaddingTop(12).AlignRight().Width(240).Column(t =>
                    {
                        TotalLine(t, "Ara Toplam", inv.GrossTotal, inv.Currency);
                        if (inv.DiscountTotal > 0)
                            TotalLine(t, "İskonto", -inv.DiscountTotal, inv.Currency);
                        TotalLine(t, "KDV Matrahı", inv.TaxBaseTotal, inv.Currency);
                        TotalLine(t, "KDV", inv.TaxTotal, inv.Currency);
                        if (inv.WithholdingTotal > 0)
                            TotalLine(t, "KDV Tevkifatı", -inv.WithholdingTotal, inv.Currency);

                        t.Item().PaddingTop(4).LineHorizontal(1);
                        t.Item().PaddingTop(4).Row(r =>
                        {
                            r.RelativeItem().Text("GENEL TOPLAM").Bold();
                            r.ConstantItem(100).AlignRight()
                             .Text($"{inv.GrandTotal:N2} {inv.Currency}").Bold().FontSize(11);
                        });
                    });

                    if (inv.WithholdingTotal > 0)
                    {
                        col.Item().PaddingTop(10).Text(
                            "Bu fatura KDV tevkifatı içermektedir. Tevkif edilen KDV alıcı " +
                            "tarafından doğrudan vergi dairesine ödenir.")
                            .FontSize(7).Italic().FontColor(Colors.Grey.Darken1);
                    }

                    if (!string.IsNullOrWhiteSpace(inv.Notes))
                        col.Item().PaddingTop(10).Text($"Not: {inv.Notes}").FontSize(8);
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                    t.Span("Sayfa ");
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();

        // --- yerel yardımcılar ---

        static string TitleOf(Domain.Enums.InvoiceType t) => t switch
        {
            Domain.Enums.InvoiceType.SalesReturn => "İADE FATURASI",
            Domain.Enums.InvoiceType.Proforma => "PROFORMA FATURA",
            _ => "FATURA"
        };

        // ⚠️ Header bloğu TableCellDescriptor verir, gövde hücreleri TableDescriptor.
        static void HeaderCell(TableCellDescriptor h, string text, bool right = false) =>
            h.Cell().Background(Colors.Grey.Lighten2).Padding(4)
             .AlignMiddle().Element(e => right ? e.AlignRight() : e.AlignLeft())
             .Text(text).Bold().FontSize(8);

        static void Cell(TableDescriptor t, string text, bool right = false) =>
            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4)
             .Element(e => right ? e.AlignRight() : e.AlignLeft())
             .Text(text).FontSize(8);

        static void TotalLine(ColumnDescriptor c, string label, decimal value, string currency) =>
            c.Item().Row(r =>
            {
                r.RelativeItem().Text(label).FontSize(8);
                r.ConstantItem(100).AlignRight().Text($"{value:N2} {currency}").FontSize(8);
            });
    }
}
