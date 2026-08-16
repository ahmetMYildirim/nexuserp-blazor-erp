using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Enums;

namespace NexusErp.Application.Accounting;

/// <summary>
/// Muhasebe modülü eklenmeden ÖNCE kesilmiş belgeler için fişleri geriye dönük üretir.
///
/// Neden gerekli: otomatik fiş üretimi bu sürümle geldi. Mevcut bir kurulumda
/// geçmiş faturaların ve tahsilatların fişi yoktur; defter o belgeleri hiç
/// görmez ve mizan yalnızca yeni hareketleri gösterir. Kullanıcı açısından bu
/// "raporlar boş / eksik" olarak görünür ve nedenini anlaması imkânsızdır.
///
/// ⚠️ Idempotent: her belge için fiş VAR MI diye bakılıyor, ayrıca
/// (tenant, kaynak tür, kaynak kimlik) unique index'i ikinci kaydı zaten
/// reddediyor. Uygulama her açılışta çalıştırılabilir.
///
/// ⚠️ Belgeler TEK TEK ve kendi transaction'ında işleniyor. Biri hata verirse
/// (örn. hesap planında eksik hesap) diğerleri yazılmaya devam eder; hepsi tek
/// SaveChanges'te olsaydı tek bozuk belge tüm geri doldurmayı engellerdi.
/// </summary>
public sealed class AccountingBackfillService(
    IAppDbContextFactory factory,
    AutoPostingService posting)
{
    public sealed record Result(int Invoices, int Payments, int Failed);

    public async Task<Result> RunAsync(CancellationToken ct = default)
    {
        var invoices = 0;
        var payments = 0;
        var failed = 0;

        List<Guid> invoiceIds;
        List<Guid> paymentIds;

        await using (var db = factory.Create())
        {
            // Hesap planı yoksa geri doldurma anlamsız — sessizce çık.
            if (!await db.Accounts.AnyAsync(ct)) return new Result(0, 0, 0);

            var posted = await db.JournalEntries
                .Where(j => j.SourceId != null)
                .Select(j => j.SourceId!.Value)
                .ToListAsync(ct);

            var postedSet = posted.ToHashSet();

            invoiceIds = await db.Invoices
                .Where(i => i.Status != InvoiceStatus.Draft
                         && i.Status != InvoiceStatus.Cancelled
                         && i.Type != InvoiceType.Proforma)
                .OrderBy(i => i.IssueDate)
                .Select(i => i.Id)
                .ToListAsync(ct);

            paymentIds = await db.Payments
                .Where(p => !p.IsCancelled)
                .OrderBy(p => p.PaymentDate)
                .Select(p => p.Id)
                .ToListAsync(ct);

            invoiceIds = invoiceIds.Where(id => !postedSet.Contains(id)).ToList();
            paymentIds = paymentIds.Where(id => !postedSet.Contains(id)).ToList();
        }

        foreach (var id in invoiceIds)
        {
            try
            {
                await using var db = factory.Create();
                var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct);
                if (invoice is null) continue;

                if (await posting.BuildForInvoiceAsync(db, invoice, ct) is null) continue;

                await db.SaveChangesAsync(ct);
                invoices++;
            }
            catch
            {
                failed++;
            }
        }

        foreach (var id in paymentIds)
        {
            try
            {
                await using var db = factory.Create();
                var payment = await db.Payments.Include(p => p.Party)
                    .FirstOrDefaultAsync(p => p.Id == id, ct);
                if (payment is null) continue;

                if (await posting.BuildForPaymentAsync(
                        db, payment, payment.Party.Title, ct) is null) continue;

                await db.SaveChangesAsync(ct);
                payments++;
            }
            catch
            {
                failed++;
            }
        }

        return new Result(invoices, payments, failed);
    }
}
