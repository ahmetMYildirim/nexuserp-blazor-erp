using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Accounting;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;

namespace NexusErp.Application.Accounting;

/// <summary>
/// Belgelerden otomatik muhasebe fişi üretir.
///
/// ⚠️ EN ÖNEMLİ TASARIM KARARI: fiş, kaynak belgeyle AYNI TRANSACTION'da
/// yazılıyor — outbox üzerinden asenkron DEĞİL.
///
/// Neden: outbox en-az-bir-kez teslimat yapar ve tüketici ayrı bir transaction'da
/// çalışır. Muhasebe fişi ise tam-bir-kez ve belgeyle atomik olmak zorundadır.
/// Asenkron üretseydik iki hata sınıfı doğardı:
///   (1) fatura yazıldı, tüketici henüz çalışmadı → mizan eksik, o aralıkta
///       alınan her rapor yanlış;
///   (2) tüketici yeniden denedi → aynı faturadan ikinci fiş.
/// Bu yüzden metotlar çağıranın IAppDbContext'ini parametre alıyor ve kendi
/// SaveChanges'ini ÇAĞIRMIYOR: kayıt, faturayı kesen SaveChanges ile birlikte
/// commit oluyor. PaymentService'in eşleştirme yardımcıları da aynı desende.
///
/// İkinci savunma hattı veri tabanında: (tenant, source_type, source_id) unique
/// index'i aynı belgeden ikinci fişi INSERT aşamasında reddediyor.
/// </summary>
public sealed class AutoPostingService(IInvoiceNumberGenerator numbers)
{
    private const string Series = "MUH";

    /// <summary>
    /// Fatura kesildiğinde fiş üretir. Fişi context'e EKLER, kaydetmez.
    /// Proforma için çağrılmamalı (bağlayıcı olmayan teklif muhasebeleşmez).
    /// </summary>
    public async Task<JournalEntry?> BuildForInvoiceAsync(
        IAppDbContext db, Invoice invoice, CancellationToken ct = default)
    {
        if (!invoice.AffectsBalance) return null;

        var sourceType = invoice.IsPurchase
            ? JournalSourceType.PurchaseInvoice
            : JournalSourceType.SalesInvoice;

        if (await ExistsAsync(db, sourceType, invoice.Id, ct)) return null;

        var accounts = await LoadAccountsAsync(db, ct);

        var entry = new JournalEntry
        {
            EntryDate = invoice.IssueDate,
            Year = invoice.IssueDate.Year,
            Description = $"{invoice.TypeText} faturası — {invoice.PartyTitle}",
            SourceType = sourceType,
            SourceId = invoice.Id,
            SourceDocumentNumber = invoice.Number
        };

        // ⚠️ TEVKİFAT: KDV'nin bir kısmını alıcı doğrudan vergi dairesine öder,
        // bize ödemez. Belge toplamı da o kadar düşüktür:
        //     GrandTotal = Matrah + KDV − Tevkifat
        // Bu yüzden KDV hesabına KDV'nin TAMAMI değil, bizim tahsil ettiğimiz
        // kısmı yazılır. Tamamı yazılsaydı fiş tevkifat tutarı kadar dengesiz
        // kalır ve fatura hiç kesilemezdi.
        var collectableTax = invoice.TaxTotal - invoice.WithholdingTotal;

        if (invoice.IsPurchase)
        {
            // Alış: mal/hizmet ve indirilecek KDV borç, tedarikçi alacak.
            Add(entry, accounts, TdhpAccounts.TicariMallar,
                debit: invoice.TaxBaseTotal, credit: 0m, "Mal / hizmet bedeli");

            Add(entry, accounts, TdhpAccounts.IndirilecekKdv,
                debit: collectableTax, credit: 0m, "İndirilecek KDV");

            Add(entry, accounts, TdhpAccounts.Saticilar,
                debit: 0m, credit: invoice.GrandTotal, invoice.PartyTitle,
                partyId: invoice.PartyId);
        }
        else if (invoice.Type == InvoiceType.SalesReturn)
        {
            // İade: satış ters döner — iade hesabı ve KDV borç, müşteri alacak.
            Add(entry, accounts, TdhpAccounts.SatistanIadeler,
                debit: invoice.TaxBaseTotal, credit: 0m, "Satıştan iade");

            Add(entry, accounts, TdhpAccounts.HesaplananKdv,
                debit: collectableTax, credit: 0m, "Hesaplanan KDV (iade)");

            Add(entry, accounts, TdhpAccounts.Alicilar,
                debit: 0m, credit: invoice.GrandTotal, invoice.PartyTitle,
                partyId: invoice.PartyId);
        }
        else
        {
            // Satış: müşteri borç, satış geliri ve hesaplanan KDV alacak.
            Add(entry, accounts, TdhpAccounts.Alicilar,
                debit: invoice.GrandTotal, credit: 0m, invoice.PartyTitle,
                partyId: invoice.PartyId);

            Add(entry, accounts, TdhpAccounts.YurtIciSatislar,
                debit: 0m, credit: invoice.TaxBaseTotal, "Satış geliri");

            Add(entry, accounts, TdhpAccounts.HesaplananKdv,
                debit: 0m, credit: collectableTax, "Hesaplanan KDV");
        }

        return await FinalizeAsync(db, entry, ct);
    }

    /// <summary>Tahsilat işlendiğinde fiş üretir: kasa/banka borç, müşteri alacak.</summary>
    public async Task<JournalEntry?> BuildForPaymentAsync(
        IAppDbContext db, Payment payment, string partyTitle, CancellationToken ct = default)
    {
        if (await ExistsAsync(db, JournalSourceType.Payment, payment.Id, ct)) return null;

        var accounts = await LoadAccountsAsync(db, ct);

        var entry = new JournalEntry
        {
            EntryDate = payment.PaymentDate,
            Year = payment.PaymentDate.Year,
            Description = $"Tahsilat — {partyTitle}",
            SourceType = JournalSourceType.Payment,
            SourceId = payment.Id,
            SourceDocumentNumber = payment.Number
        };

        Add(entry, accounts, CashAccountFor(payment.Method),
            debit: payment.Amount, credit: 0m, payment.MethodText);

        Add(entry, accounts, TdhpAccounts.Alicilar,
            debit: 0m, credit: payment.Amount, partyTitle, partyId: payment.PartyId);

        return await FinalizeAsync(db, entry, ct);
    }

    /// <summary>
    /// Tahsilat iptalinde TERS kayıt üretir. Orijinal fiş silinmez —
    /// muhasebede düzeltme ters kayıtla yapılır (ADR-009).
    /// </summary>
    public async Task<JournalEntry?> BuildForPaymentReversalAsync(
        IAppDbContext db, Payment payment, string partyTitle, CancellationToken ct = default)
    {
        if (await ExistsAsync(db, JournalSourceType.PaymentReversal, payment.Id, ct))
            return null;

        var accounts = await LoadAccountsAsync(db, ct);

        var entry = new JournalEntry
        {
            EntryDate = DateOnly.FromDateTime(DateTime.Today),
            Year = DateTime.Today.Year,
            Description = $"Tahsilat iptali — {payment.Number}",
            SourceType = JournalSourceType.PaymentReversal,
            SourceId = payment.Id,
            SourceDocumentNumber = payment.Number
        };

        // Yön ters: müşteri borç, kasa/banka alacak.
        Add(entry, accounts, TdhpAccounts.Alicilar,
            debit: payment.Amount, credit: 0m, partyTitle, partyId: payment.PartyId);

        Add(entry, accounts, CashAccountFor(payment.Method),
            debit: 0m, credit: payment.Amount, payment.MethodText);

        return await FinalizeAsync(db, entry, ct);
    }

    // ------------------------------------------------------------------

    /// <summary>Nakit kasaya, diğer tahsilat yöntemleri bankaya yazılır.</summary>
    private static string CashAccountFor(PaymentMethod method) =>
        method == PaymentMethod.Cash ? TdhpAccounts.Kasa : TdhpAccounts.Bankalar;

    private static async Task<bool> ExistsAsync(
        IAppDbContext db, JournalSourceType type, Guid sourceId, CancellationToken ct)
        => await db.JournalEntries
            .AnyAsync(j => j.SourceType == type && j.SourceId == sourceId, ct);

    private static async Task<Dictionary<string, Account>> LoadAccountsAsync(
        IAppDbContext db, CancellationToken ct)
        => await db.Accounts.Where(a => a.IsActive).ToDictionaryAsync(a => a.Code, ct);

    private static void Add(
        JournalEntry entry, Dictionary<string, Account> accounts, string code,
        decimal debit, decimal credit, string? description, Guid? partyId = null)
    {
        // Sıfır tutarlı satır yazılmaz: CHECK constraint reddeder ve zaten
        // muhasebe açısından anlamsızdır (KDV'siz fatura gibi durumlar).
        if (debit == 0m && credit == 0m) return;

        if (!accounts.TryGetValue(code, out var account))
            throw new DomainException(
                $"{code} numaralı hesap bulunamadı. Hesap planı eksik veya pasif — " +
                "Muhasebe → Hesap Planı ekranından kontrol edin.");

        account.EnsurePostable();

        entry.Lines.Add(new JournalLine
        {
            LineNumber = entry.Lines.Count + 1,
            AccountId = account.Id,
            AccountCode = account.Code,
            AccountName = account.Name,
            Debit = debit,
            Credit = credit,
            Description = description,
            PartyId = partyId
        });
    }

    /// <summary>
    /// Fişi kesinleştirip context'e ekler. SaveChanges ÇAĞIRMAZ — kaynak
    /// belgenin transaction'ına katılır.
    /// </summary>
    private async Task<JournalEntry> FinalizeAsync(
        IAppDbContext db, JournalEntry entry, CancellationToken ct)
    {
        var (number, _) = await numbers.NextAsync(Series, entry.Year, ct);
        entry.Post(number, DateTimeOffset.UtcNow);

        db.JournalEntries.Add(entry);
        return entry;
    }
}
