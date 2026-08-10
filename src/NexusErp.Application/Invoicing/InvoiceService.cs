using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Parties;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Domain.Invoicing;

namespace NexusErp.Application.Invoicing;

public sealed class InvoiceService(
    IAppDbContext db,
    IInvoiceNumberGenerator numbers,
    TimeProvider clock)
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    // ------------------------------------------------------------------
    // Okuma
    // ------------------------------------------------------------------

    public async Task<PagedResult<InvoiceListItem>> SearchAsync(
        InvoiceQuery q, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().Date);
        var query = db.Invoices.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + q.Search.Trim().ToUpper(Tr) + "%";
            query = query.Where(i =>
                (i.Number != null && EF.Functions.Like(i.Number.ToUpper(), pattern)) ||
                EF.Functions.Like(i.PartyTitle.ToUpper(), pattern));
        }

        if (q.Status is not null) query = query.Where(i => i.Status == q.Status.Value);
        if (q.Type is not null) query = query.Where(i => i.Type == q.Type.Value);
        if (q.PartyId is not null) query = query.Where(i => i.PartyId == q.PartyId.Value);

        if (q.OnlyOverdue)
            query = query.Where(i =>
                (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid)
                && i.DueDate < today);

        var total = await query.CountAsync(ct);

        query = (q.SortBy, q.Descending) switch
        {
            (nameof(InvoiceListItem.Number), false) => query.OrderBy(i => i.Number),
            (nameof(InvoiceListItem.Number), true) => query.OrderByDescending(i => i.Number),
            (nameof(InvoiceListItem.GrandTotal), false) => query.OrderBy(i => i.GrandTotal),
            (nameof(InvoiceListItem.GrandTotal), true) => query.OrderByDescending(i => i.GrandTotal),
            (nameof(InvoiceListItem.DueDate), false) => query.OrderBy(i => i.DueDate),
            (nameof(InvoiceListItem.DueDate), true) => query.OrderByDescending(i => i.DueDate),
            (_, false) => query.OrderBy(i => i.IssueDate).ThenBy(i => i.Number),
            _ => query.OrderByDescending(i => i.IssueDate).ThenByDescending(i => i.Number)
        };

        var items = await query
            .Skip(q.Page * q.PageSize)
            .Take(q.PageSize)
            .Select(i => new InvoiceListItem(
                i.Id, i.Number, i.Type, i.Status, i.PartyTitle, i.IssueDate, i.DueDate,
                i.Currency, i.GrandTotal, i.PaidAmount, i.SubscriptionId))
            .ToListAsync(ct);

        return new PagedResult<InvoiceListItem>(items, total);
    }

    public async Task<InvoiceForm?> GetFormAsync(Guid id, CancellationToken ct = default)
    {
        var inv = await db.Invoices.AsNoTracking()
            .Include(i => i.Lines.OrderBy(l => l.LineNumber))
            .FirstOrDefaultAsync(i => i.Id == id, ct);

        if (inv is null) return null;

        return new InvoiceForm
        {
            Id = inv.Id,
            PartyId = inv.PartyId,
            Type = inv.Type,
            Series = inv.Series,
            IssueDate = inv.IssueDate,
            DueDate = inv.DueDate,
            Currency = inv.Currency,
            ExchangeRate = inv.ExchangeRate,
            DocumentDiscountType = inv.DocumentDiscountType,
            DocumentDiscountValue = inv.DocumentDiscountValue,
            Notes = inv.Notes,
            SubscriptionId = inv.SubscriptionId,
            PeriodStart = inv.PeriodStart,
            PeriodEnd = inv.PeriodEnd,
            Lines = inv.Lines.Select(l => new InvoiceLineForm
            {
                ProductId = l.ProductId,
                ProductCode = l.ProductCode,
                ProductName = l.ProductName,
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                DiscountType = l.DiscountType,
                DiscountValue = l.DiscountValue,
                TaxRateId = l.TaxRateId,
                TaxRate = l.TaxRate,
                WithholdingRate = l.WithholdingRate,
                Description = l.Description
            }).ToList()
        };
    }

    // ------------------------------------------------------------------
    // Yazma
    // ------------------------------------------------------------------

    public async Task<Guid> SaveDraftAsync(InvoiceForm form, CancellationToken ct = default)
    {
        if (form.Lines.Count == 0)
            throw new DomainException("Fatura en az bir satır içermelidir.");

        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == form.PartyId, ct)
                    ?? throw new DomainException("Cari kart bulunamadı.");

        if (form.Type is InvoiceType.Sales or InvoiceType.Proforma)
            party.EnsureCanBeInvoiced();

        // --- Hesaplama: TEK kaynak. UI'daki hesap sadece önizleme (Bölüm 08). ---
        var inputs = form.Lines
            .Select(l => new LineInput(l.Quantity, l.UnitPrice, l.DiscountType,
                                       l.DiscountValue, l.TaxRate, l.WithholdingRate))
            .ToList();

        var calc = InvoiceCalculator.CalculateDocument(
            inputs, form.DocumentDiscountType, form.DocumentDiscountValue);

        var isNew = form.Id is null;
        Invoice invoice;

        if (isNew)
        {
            invoice = new Invoice { Series = form.Series, Year = form.IssueDate.Year };
        }
        else
        {
            invoice = await db.Invoices.Include(i => i.Lines)
                          .FirstOrDefaultAsync(i => i.Id == form.Id, ct)
                      ?? throw new DomainException("Fatura bulunamadı.");

            invoice.EnsureEditable();       // durum makinesi koruması
            db.InvoiceLines.RemoveRange(invoice.Lines);
            invoice.Lines.Clear();
        }

        // --- Cari SNAPSHOT'ı: belge değişmezliği (Bölüm 07) ---
        invoice.PartyId = party.Id;
        invoice.PartyTitle = party.Title;
        invoice.PartyTaxNumber = party.TaxNumber;
        invoice.PartyTaxOffice = party.TaxOffice;
        invoice.PartyAddress = $"{party.Address} {party.District}/{party.City}".Trim();

        invoice.Type = form.Type;
        invoice.IssueDate = form.IssueDate;
        invoice.DueDate = form.DueDate ?? party.CalculateDueDate(form.IssueDate);
        invoice.Currency = form.Currency;
        invoice.ExchangeRate = form.ExchangeRate;
        invoice.DocumentDiscountType = form.DocumentDiscountType;
        invoice.DocumentDiscountValue = form.DocumentDiscountValue;
        invoice.Notes = form.Notes;
        invoice.SubscriptionId = form.SubscriptionId;
        invoice.PeriodStart = form.PeriodStart;
        invoice.PeriodEnd = form.PeriodEnd;

        if (invoice.DueDate < invoice.IssueDate)
            throw new DomainException("Vade tarihi, fatura tarihinden önce olamaz.");

        for (var i = 0; i < form.Lines.Count; i++)
        {
            var src = form.Lines[i];
            var res = calc.Lines[i];

            if (string.IsNullOrWhiteSpace(src.ProductName))
                throw new DomainException($"{i + 1}. satırda ürün/hizmet seçilmemiş.");

            invoice.Lines.Add(new InvoiceLine
            {
                LineNumber = i + 1,
                ProductId = src.ProductId,
                ProductCode = string.IsNullOrWhiteSpace(src.ProductCode) ? "-" : src.ProductCode,
                ProductName = src.ProductName,
                Unit = src.Unit,
                Quantity = src.Quantity,
                UnitPrice = src.UnitPrice,
                DiscountType = src.DiscountType,
                DiscountValue = src.DiscountValue,
                TaxRateId = src.TaxRateId,
                TaxRate = src.TaxRate,
                WithholdingRate = src.WithholdingRate,
                Description = src.Description,

                GrossAmount = res.GrossAmount,
                DiscountAmount = res.DiscountAmount,
                DocumentDiscountShare = res.DocumentDiscountShare,
                TaxBase = res.TaxBase,
                TaxAmount = res.TaxAmount,
                WithholdingAmount = res.WithholdingAmount,
                LineTotal = res.LineTotal
            });
        }

        invoice.GrossTotal = calc.GrossTotal;
        invoice.DiscountTotal = calc.DiscountTotal;
        invoice.TaxBaseTotal = calc.TaxBaseTotal;
        invoice.TaxTotal = calc.TaxTotal;
        invoice.WithholdingTotal = calc.WithholdingTotal;
        invoice.GrandTotal = calc.GrandTotal;

        // Add EN SON — doğrulama hatası olursa context'te öksüz entity kalmasın (Bölüm 06)
        if (isNew) db.Invoices.Add(invoice);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            if (isNew) db.Detach(invoice);
            throw;
        }

        return invoice.Id;
    }

    /// <summary>Taslağı resmî faturaya çevirir: numara verir, durumu Issued yapar.</summary>
    public async Task<string> IssueAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var invoice = await db.Invoices.Include(i => i.Lines)
                          .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
                      ?? throw new DomainException("Fatura bulunamadı.");

        // Numara SADECE burada veriliyor, taslakta değil: taslak silinebilir ve
        // silinen taslağın numarası boşluk bırakır. Mevzuat boşluksuz seri ister.
        var (number, sequence) = await numbers.NextAsync(invoice.Series, invoice.Year, ct);

        invoice.MarkIssued(number, sequence, clock.GetUtcNow());

        await db.SaveChangesAsync(ct);
        return number;
    }

    public async Task CancelAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
                      ?? throw new DomainException("Fatura bulunamadı.");

        invoice.Cancel();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteDraftAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
                      ?? throw new DomainException("Fatura bulunamadı.");

        if (invoice.Status != InvoiceStatus.Draft)
            throw new DomainException(
                "Kesilmiş fatura silinemez. İptal edin veya iade faturası kesin.");

        db.Invoices.Remove(invoice);      // soft delete'e dönüşür (ADR-009)
        await db.SaveChangesAsync(ct);
    }
}
