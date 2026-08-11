using System.Globalization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Infrastructure.Persistence;

namespace NexusErp.Infrastructure.EInvoice;

/// <summary>
/// UBL-TR 1.2 (GİB e-Fatura) XML üreteci.
///
/// Fatura entity'sindeki snapshot alanları (cari unvanı, VKN, KDV oranı, ürün adı)
/// tam olarak burası için vardı: e-Fatura XML'i belgeyi kesildiği ANDAKİ haliyle ister.
/// </summary>
public sealed class UblInvoiceBuilder(AppDbContext db) : IUblInvoiceBuilder
{
    // UBL standart ad alanları
    private static readonly XNamespace Inv = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";

    /// <summary>Tutarlar NOKTA ondalık ayırıcıyla yazılır — XML kültürden bağımsızdır.</summary>
    private static string N(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    public async Task<EInvoiceDocument> BuildAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var inv = await db.Invoices.AsNoTracking()
            .Include(i => i.Lines.OrderBy(l => l.LineNumber))
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new DomainException("Fatura bulunamadı.");

        if (inv.Status == InvoiceStatus.Draft)
            throw new DomainException("Taslak fatura için e-Fatura XML'i üretilemez.");

        var tenant = await db.Tenants.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(t => t.Id == inv.TenantId, ct);

        // Alıcı e-Fatura mükellefi değilse e-Arşiv profili kullanılır.
        // Demo'da mükellef bilgisi tutulmadığı için tevkifat/normal ayrımı yapıyoruz.
        var profile = inv.WithholdingTotal > 0 ? EInvoiceProfile.Ticari : EInvoiceProfile.Temel;

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Inv + "Invoice",
                new XAttribute(XNamespace.Xmlns + "cac", Cac),
                new XAttribute(XNamespace.Xmlns + "cbc", Cbc),
                new XAttribute(XNamespace.Xmlns + "ext", Ext),

                new XElement(Ext + "UBLExtensions"),          // mali mühür buraya eklenir

                new XElement(Cbc + "UBLVersionID", "2.1"),
                new XElement(Cbc + "CustomizationID", "TR1.2"),
                new XElement(Cbc + "ProfileID", ProfileCode(profile)),
                new XElement(Cbc + "ID", inv.Number),
                new XElement(Cbc + "CopyIndicator", "false"),
                new XElement(Cbc + "UUID", inv.Ettn),          // ETTN
                new XElement(Cbc + "IssueDate", inv.IssueDate.ToString("yyyy-MM-dd")),
                new XElement(Cbc + "IssueTime",
                    (inv.IssuedAt ?? DateTimeOffset.UtcNow).ToString("HH:mm:ss")),
                new XElement(Cbc + "InvoiceTypeCode", InvoiceTypeCode(inv)),
                new XElement(Cbc + "DocumentCurrencyCode", inv.Currency),
                new XElement(Cbc + "LineCountNumeric", inv.Lines.Count),

                BuildParty(Cac + "AccountingSupplierParty", tenant.Name, tenant.TaxNumber,
                           tenant.TaxOffice, tenant.Address, tenant.City, tenant.Email),

                BuildParty(Cac + "AccountingCustomerParty", inv.PartyTitle, inv.PartyTaxNumber,
                           inv.PartyTaxOffice, inv.PartyAddress, null, null),

                BuildTaxTotal(inv),
                BuildWithholdingTotal(inv),
                BuildMonetaryTotal(inv),
                inv.Lines.Select(l => BuildLine(inv, l))));

        return new EInvoiceDocument(inv.Id, inv.Number!, inv.Ettn, profile, Serialize(doc));
    }

    private static string ProfileCode(EInvoiceProfile p) => p switch
    {
        EInvoiceProfile.Ticari => "TICARIFATURA",
        EInvoiceProfile.EArsiv => "EARSIVFATURA",
        _ => "TEMELFATURA"
    };

    private static string InvoiceTypeCode(Invoice inv) => inv switch
    {
        { WithholdingTotal: > 0 } => "TEVKIFAT",
        { Type: InvoiceType.SalesReturn } => "IADE",
        _ => "SATIS"
    };

    private XElement BuildParty(XName wrapper, string name, string? taxNumber,
                                string? taxOffice, string? address, string? city, string? email)
    {
        // VKN 10 hane (tüzel kişi), TCKN 11 hane (gerçek kişi) — schemeID bunu belirtir
        var schemeId = taxNumber?.Length == 11 ? "TCKN" : "VKN";

        var party = new XElement(Cac + "Party",
            new XElement(Cac + "PartyIdentification",
                new XElement(Cbc + "ID", new XAttribute("schemeID", schemeId),
                    taxNumber ?? "11111111111")),
            new XElement(Cac + "PartyName",
                new XElement(Cbc + "Name", name)),
            new XElement(Cac + "PostalAddress",
                new XElement(Cbc + "StreetName", address ?? ""),
                new XElement(Cbc + "CityName", city ?? ""),
                new XElement(Cac + "Country",
                    new XElement(Cbc + "Name", "Türkiye"))));

        if (!string.IsNullOrWhiteSpace(taxOffice))
            party.Add(new XElement(Cac + "PartyTaxScheme",
                new XElement(Cac + "TaxScheme",
                    new XElement(Cbc + "Name", taxOffice))));

        if (!string.IsNullOrWhiteSpace(email))
            party.Add(new XElement(Cac + "Contact",
                new XElement(Cbc + "ElectronicMail", email)));

        return new XElement(wrapper, party);
    }

    /// <summary>
    /// KDV toplamı, ORAN BAZINDA alt toplamlara ayrılır.
    /// GİB tek bir toplam değil, her KDV oranı için ayrı TaxSubtotal ister.
    /// </summary>
    private XElement BuildTaxTotal(Invoice inv)
    {
        var subtotals = inv.Lines
            .GroupBy(l => l.TaxRate)
            .OrderBy(g => g.Key)
            .Select(g => new XElement(Cac + "TaxSubtotal",
                new XElement(Cbc + "TaxableAmount",
                    new XAttribute("currencyID", inv.Currency), N(g.Sum(l => l.TaxBase))),
                new XElement(Cbc + "TaxAmount",
                    new XAttribute("currencyID", inv.Currency), N(g.Sum(l => l.TaxAmount))),
                new XElement(Cbc + "Percent", N(g.Key * 100m)),
                new XElement(Cac + "TaxCategory",
                    new XElement(Cac + "TaxScheme",
                        new XElement(Cbc + "Name", "KDV"),
                        new XElement(Cbc + "TaxTypeCode", "0015")))));   // GİB: KDV kodu

        return new XElement(Cac + "TaxTotal",
            new XElement(Cbc + "TaxAmount",
                new XAttribute("currencyID", inv.Currency), N(inv.TaxTotal)),
            subtotals);
    }

    /// <summary>Tevkifat — alıcının doğrudan devlete ödeyeceği KDV kısmı.</summary>
    private XElement? BuildWithholdingTotal(Invoice inv)
    {
        if (inv.WithholdingTotal <= 0) return null;

        var lines = inv.Lines.Where(l => l.WithholdingRate > 0).ToList();

        return new XElement(Cac + "WithholdingTaxTotal",
            new XElement(Cbc + "TaxAmount",
                new XAttribute("currencyID", inv.Currency), N(inv.WithholdingTotal)),
            lines.GroupBy(l => l.WithholdingRate!.Value)
                 .Select(g => new XElement(Cac + "TaxSubtotal",
                     new XElement(Cbc + "TaxableAmount",
                         new XAttribute("currencyID", inv.Currency), N(g.Sum(l => l.TaxBase))),
                     new XElement(Cbc + "TaxAmount",
                         new XAttribute("currencyID", inv.Currency),
                         N(g.Sum(l => l.WithholdingAmount))),
                     new XElement(Cbc + "Percent", N(g.Key * 100m)),
                     new XElement(Cac + "TaxCategory",
                         new XElement(Cac + "TaxScheme",
                             new XElement(Cbc + "Name", "KDV Tevkifatı"),
                             new XElement(Cbc + "TaxTypeCode", "0021"))))));
    }

    private XElement BuildMonetaryTotal(Invoice inv)
    {
        // PayableAmount = KDV dahil toplam − tevkifat (satıcıya ödenecek tutar)
        var taxInclusive = inv.TaxBaseTotal + inv.TaxTotal;

        return new XElement(Cac + "LegalMonetaryTotal",
            new XElement(Cbc + "LineExtensionAmount",
                new XAttribute("currencyID", inv.Currency), N(inv.TaxBaseTotal)),
            new XElement(Cbc + "TaxExclusiveAmount",
                new XAttribute("currencyID", inv.Currency), N(inv.TaxBaseTotal)),
            new XElement(Cbc + "TaxInclusiveAmount",
                new XAttribute("currencyID", inv.Currency), N(taxInclusive)),
            new XElement(Cbc + "AllowanceTotalAmount",
                new XAttribute("currencyID", inv.Currency), N(inv.DiscountTotal)),
            new XElement(Cbc + "PayableAmount",
                new XAttribute("currencyID", inv.Currency), N(inv.GrandTotal)));
    }

    private XElement BuildLine(Invoice inv, InvoiceLine l)
    {
        var line = new XElement(Cac + "InvoiceLine",
            new XElement(Cbc + "ID", l.LineNumber),
            new XElement(Cbc + "InvoicedQuantity",
                new XAttribute("unitCode", UnitCode(l.Unit)),
                l.Quantity.ToString("0.######", CultureInfo.InvariantCulture)),
            new XElement(Cbc + "LineExtensionAmount",
                new XAttribute("currencyID", inv.Currency), N(l.TaxBase)));

        var totalDiscount = l.DiscountAmount + l.DocumentDiscountShare;
        if (totalDiscount > 0)
            line.Add(new XElement(Cac + "AllowanceCharge",
                new XElement(Cbc + "ChargeIndicator", "false"),   // false = iskonto
                new XElement(Cbc + "Amount",
                    new XAttribute("currencyID", inv.Currency), N(totalDiscount))));

        line.Add(
            new XElement(Cac + "TaxTotal",
                new XElement(Cbc + "TaxAmount",
                    new XAttribute("currencyID", inv.Currency), N(l.TaxAmount)),
                new XElement(Cac + "TaxSubtotal",
                    new XElement(Cbc + "TaxableAmount",
                        new XAttribute("currencyID", inv.Currency), N(l.TaxBase)),
                    new XElement(Cbc + "TaxAmount",
                        new XAttribute("currencyID", inv.Currency), N(l.TaxAmount)),
                    new XElement(Cbc + "Percent", N(l.TaxRate * 100m)),
                    new XElement(Cac + "TaxCategory",
                        new XElement(Cac + "TaxScheme",
                            new XElement(Cbc + "Name", "KDV"),
                            new XElement(Cbc + "TaxTypeCode", "0015"))))),
            new XElement(Cac + "Item",
                new XElement(Cbc + "Name", l.ProductName),
                new XElement(Cac + "SellersItemIdentification",
                    new XElement(Cbc + "ID", l.ProductCode))),
            new XElement(Cac + "Price",
                new XElement(Cbc + "PriceAmount",
                    new XAttribute("currencyID", inv.Currency),
                    l.UnitPrice.ToString("0.0000", CultureInfo.InvariantCulture))));

        return line;
    }

    /// <summary>UN/ECE Recommendation 20 birim kodları — GİB bunları zorunlu tutuyor.</summary>
    private static string UnitCode(string unit) => unit.ToUpperInvariant() switch
    {
        "ADET" => "C62",
        "KG" => "KGM",
        "SAAT" => "HUR",
        "AY" => "MON",
        "GÜN" => "DAY",
        "LİTRE" or "LITRE" => "LTR",
        "METRE" => "MTR",
        "PAKET" => "PK",
        _ => "C62"
    };

    private static string Serialize(XDocument doc)
    {
        using var writer = new StringWriter();
        doc.Save(writer, SaveOptions.None);
        return writer.ToString();
    }
}

/// <summary>
/// Entegratör bağlantısı olmadan iş akışını uçtan uca çalıştırmak için.
/// Gerçek entegratör geldiğinde YALNIZCA bu sınıfın yerine gerçeği yazılır.
/// </summary>
public sealed class MockEInvoiceGateway : IEInvoiceGateway
{
    public Task<EInvoiceSendResult> SendAsync(
        EInvoiceDocument document, CancellationToken ct = default)
        => Task.FromResult(new EInvoiceSendResult(
            Success: true,
            TrackingId: $"MOCK-{document.Ettn:N}"[..20],
            Message: "Sahte entegratör: XML üretildi ve doğrulandı, gönderim simüle edildi."));
}
