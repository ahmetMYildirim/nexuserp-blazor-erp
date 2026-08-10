using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Parties;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;

namespace NexusErp.Application.Payments;

public sealed class PaymentService(IAppDbContext db, IInvoiceNumberGenerator numbers)
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public async Task<PagedResult<PaymentListItem>> SearchAsync(
        string? search = null, int page = 0, int pageSize = 25, CancellationToken ct = default)
    {
        var query = db.Payments.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().ToUpper(Tr) + "%";
            query = query.Where(p =>
                (p.Number != null && EF.Functions.Like(p.Number.ToUpper(), pattern)) ||
                EF.Functions.Like(p.Party.Title.ToUpper(), pattern));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.Number)
            .Skip(page * pageSize).Take(pageSize)
            .Select(p => new PaymentListItem(
                p.Id, p.Number, p.Party.Title, p.PaymentDate, p.Method,
                p.Amount, p.AllocatedAmount, p.Currency, p.Reference, p.IsCancelled))
            .ToListAsync(ct);

        return new PagedResult<PaymentListItem>(items, total);
    }

    /// <summary>
    /// Tahsilat kaydeder ve (istenirse) açık faturalara FIFO ile dağıtır.
    /// Tüm işlem tek SaveChanges — yarım kalmış eşleştirme cari bakiyeyi bozar.
    /// </summary>
    public async Task<Guid> CreateAsync(PaymentForm form, CancellationToken ct = default)
    {
        if (form.Amount <= 0)
            throw new DomainException("Tahsilat tutarı sıfırdan büyük olmalıdır.");

        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == form.PartyId, ct)
                    ?? throw new DomainException("Cari kart bulunamadı.");

        var (number, _) = await numbers.NextAsync("THS", form.PaymentDate.Year, ct);

        var payment = new Payment
        {
            Number = number,
            PartyId = party.Id,
            PaymentDate = form.PaymentDate,
            Method = form.Method,
            Amount = form.Amount,
            Currency = form.Currency,
            Reference = form.Reference,
            Notes = form.Notes
        };

        // Cari hareket: tahsilat ALACAK tarafına yazılır
        var ledger = new PartyLedgerEntry
        {
            PartyId = party.Id,
            EntryDate = form.PaymentDate,
            Type = LedgerEntryType.Payment,
            Credit = form.Amount,
            Currency = form.Currency,
            Description = $"Tahsilat — {payment.MethodText}",
            PaymentId = payment.Id,
            DocumentNumber = number
        };

        if (form.AutoAllocate)
            await AllocateFifoAsync(payment, party.Id, ct);
        else
            foreach (var manual in form.Allocations)
                await AllocateAsync(payment, manual.InvoiceId, manual.Amount, form.PaymentDate, ct);

        db.Payments.Add(payment);
        db.PartyLedgerEntries.Add(ledger);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            db.Detach(payment);
            db.Detach(ledger);
            throw;
        }

        return payment.Id;
    }

    /// <summary>
    /// Açık faturalara VADE sırasına göre (en eski önce) dağıtır.
    /// Kalan tutar avans olarak cari bakiyede kalır.
    /// </summary>
    private async Task AllocateFifoAsync(Payment payment, Guid partyId, CancellationToken ct)
    {
        var openInvoices = await db.Invoices
            .Where(i => i.PartyId == partyId
                     && i.Currency == payment.Currency
                     && i.Type != InvoiceType.Proforma
                     && (i.Status == InvoiceStatus.Issued
                      || i.Status == InvoiceStatus.PartiallyPaid))
            .OrderBy(i => i.DueDate)              // FIFO: en eski vade önce
            .ThenBy(i => i.IssueDate)
            .ToListAsync(ct);

        foreach (var invoice in openInvoices)
        {
            if (payment.UnallocatedAmount <= 0) break;

            var amount = Math.Min(payment.UnallocatedAmount, invoice.RemainingAmount);
            if (amount <= 0) continue;

            ApplyAllocation(payment, invoice, amount, payment.PaymentDate);
        }
    }

    private async Task AllocateAsync(Payment payment, Guid invoiceId, decimal amount,
                                     DateOnly date, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
                      ?? throw new DomainException("Fatura bulunamadı.");

        if (invoice.Currency != payment.Currency)
            throw new DomainException(
                $"Fatura ({invoice.Currency}) ve tahsilat ({payment.Currency}) para birimleri farklı.");

        payment.EnsureCanAllocate(amount);

        if (amount > invoice.RemainingAmount)
            throw new DomainException(
                $"{invoice.Number} faturasının kalan tutarı {invoice.RemainingAmount:N2}. " +
                "Daha fazlası eşleştirilemez.");

        ApplyAllocation(payment, invoice, amount, date);
    }

    private void ApplyAllocation(Payment payment, Invoice invoice, decimal amount, DateOnly date)
    {
        db.PaymentAllocations.Add(new PaymentAllocation
        {
            PaymentId = payment.Id,
            InvoiceId = invoice.Id,
            Amount = amount,
            AllocatedOn = date
        });

        payment.AllocatedAmount += amount;
        invoice.PaidAmount += amount;
        invoice.RefreshPaymentStatus();     // durum makinesi entity'nin içinde
    }

    /// <summary>
    /// Tahsilatı geri alır: eşleştirmeleri çözer, TERS KAYIT yazar.
    /// Muhasebede kayıt silinmez, düzeltilir — denetimde "bu tahsilat neden yok?"
    /// sorusuna cevap verebilmek gerekir.
    /// </summary>
    public async Task CancelAsync(Guid paymentId, CancellationToken ct = default)
    {
        var payment = await db.Payments
            .Include(p => p.Allocations).ThenInclude(a => a.Invoice)
            .FirstOrDefaultAsync(p => p.Id == paymentId, ct)
            ?? throw new DomainException("Tahsilat bulunamadı.");

        if (payment.IsCancelled)
            throw new DomainException("Tahsilat zaten iptal edilmiş.");

        foreach (var allocation in payment.Allocations)
        {
            allocation.Invoice.PaidAmount -= allocation.Amount;
            allocation.Invoice.RefreshPaymentStatus();
            db.PaymentAllocations.Remove(allocation);
        }

        payment.AllocatedAmount = 0;
        payment.IsCancelled = true;

        db.PartyLedgerEntries.Add(new PartyLedgerEntry
        {
            PartyId = payment.PartyId,
            EntryDate = DateOnly.FromDateTime(DateTime.Today),
            Type = LedgerEntryType.Adjustment,
            Debit = payment.Amount,                 // alacağı geri alıyoruz → borç
            Currency = payment.Currency,
            Description = $"Tahsilat iptali — {payment.Number}",
            PaymentId = payment.Id,
            DocumentNumber = payment.Number
        });

        await db.SaveChangesAsync(ct);
    }
}
