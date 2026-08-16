using System.Globalization;
using System.Xml.Linq;
using NexusErp.Application.Accounting;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Invoicing;
using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Infrastructure.EInvoice;
using NexusErp.Infrastructure.Invoicing;
using NexusErp.Infrastructure.Persistence;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.EInvoice;

[Collection(nameof(DatabaseCollection))]
public sealed class UblInvoiceBuilderTests(DatabaseFixture fixture)
{
    private static readonly XNamespace Cac =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc =
        "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    private sealed record Ctx(AppDbContext Db, InvoiceService Invoices,
                              UblInvoiceBuilder Ubl, Guid PartyId);

    private Ctx Setup(Guid tenant)
    {
        fixture.SeedChartOfAccounts(tenant);

        var db = fixture.CreateContext(tenant);
        var generator = new InvoiceNumberGenerator(db, fixture.CreateTenantContext(tenant));
        var invoices = new InvoiceService(
            fixture.CreateFactory(tenant), generator, TimeProvider.System,
            new AutoPostingService(generator));

        db.Tenants.Add(new Tenant
        {
            Id = tenant, Name = "Test Yazılım A.Ş.", TaxNumber = "1234567890",
            TaxOffice = "Kağıthane", City = "İstanbul", Address = "Demo Cad. No:1"
        });

        var party = new Party
        {
            TenantId = tenant, Code = "MUS0001", Title = "Alıcı Lojistik A.Ş.",
            Type = PartyType.Customer, PaymentTermDays = 30, TaxOffice = "Beşiktaş",
            City = "İstanbul"
        };
        party.SetTaxNumber("1234567890");
        db.Parties.Add(party);
        db.SaveChanges();

        return new Ctx(db, invoices, new UblInvoiceBuilder(db), party.Id);
    }

    private static async Task<Guid> IssueAsync(Ctx c, params InvoiceLineForm[] lines)
    {
        var id = await c.Invoices.SaveDraftAsync(new InvoiceForm
        {
            PartyId = c.PartyId,
            Series = "NEX",
            IssueDate = new DateOnly(2026, 3, 1),
            Lines = lines.ToList()
        });
        await c.Invoices.IssueAsync(id);
        return id;
    }

    private static InvoiceLineForm Line(decimal qty, decimal price, decimal taxRate,
                                        decimal? withholding = null, string unit = "Adet") => new()
    {
        ProductCode = "HZM001", ProductName = "Danışmanlık Hizmeti", Unit = unit,
        Quantity = qty, UnitPrice = price, TaxRate = taxRate, WithholdingRate = withholding
    };

    private static decimal D(string? s) =>
        decimal.Parse(s ?? "0", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Ubl_iskeleti_ve_zorunlu_alanlar_dogru()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        var id = await IssueAsync(c, Line(10m, 100m, 0.20m));
        var doc = await c.Ubl.BuildAsync(id);
        var x = XDocument.Parse(doc.Xml);
        var root = x.Root!;

        root.Name.LocalName.ShouldBe("Invoice");
        root.Element(Cbc + "UBLVersionID")!.Value.ShouldBe("2.1");
        root.Element(Cbc + "CustomizationID")!.Value.ShouldBe("TR1.2");   // GİB yerelleştirmesi
        root.Element(Cbc + "ProfileID")!.Value.ShouldBe("TEMELFATURA");
        root.Element(Cbc + "InvoiceTypeCode")!.Value.ShouldBe("SATIS");
        root.Element(Cbc + "ID")!.Value.ShouldBe("NEX2026000000001");
        root.Element(Cbc + "UUID")!.Value.ShouldBe(doc.Ettn.ToString());  // ETTN
        root.Element(Cbc + "IssueDate")!.Value.ShouldBe("2026-03-01");
        root.Element(Cbc + "DocumentCurrencyCode")!.Value.ShouldBe("TRY");
        root.Element(Cbc + "LineCountNumeric")!.Value.ShouldBe("1");
    }

    [Fact]
    public async Task Taraf_bilgileri_vkn_ve_scheme_ile_yazilir()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        var doc = await c.Ubl.BuildAsync(await IssueAsync(c, Line(1m, 1_000m, 0.20m)));
        var root = XDocument.Parse(doc.Xml).Root!;

        var supplier = root.Element(Cac + "AccountingSupplierParty")!.Element(Cac + "Party")!;
        supplier.Element(Cac + "PartyName")!.Element(Cbc + "Name")!.Value
                .ShouldBe("Test Yazılım A.Ş.");

        var supplierId = supplier.Element(Cac + "PartyIdentification")!.Element(Cbc + "ID")!;
        supplierId.Value.ShouldBe("1234567890");
        supplierId.Attribute("schemeID")!.Value.ShouldBe("VKN");   // 10 hane → tüzel kişi

        var customer = root.Element(Cac + "AccountingCustomerParty")!.Element(Cac + "Party")!;
        customer.Element(Cac + "PartyName")!.Element(Cbc + "Name")!.Value
                .ShouldBe("Alıcı Lojistik A.Ş.");
    }

    /// <summary>
    /// XML'deki toplamlar faturayla BİREBİR aynı olmalı. Entegratör tutarsızlığı
    /// reddeder ve fatura GİB'e hiç ulaşmaz.
    /// </summary>
    [Fact]
    public async Task Toplamlar_faturayla_birebir_ayni()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        var id = await IssueAsync(c,
            Line(3m, 1_000m, 0.20m),
            Line(2m, 500m, 0.10m));

        var inv = c.Db.Invoices.Single(i => i.Id == id);
        var root = XDocument.Parse((await c.Ubl.BuildAsync(id)).Xml).Root!;

        var totals = root.Element(Cac + "LegalMonetaryTotal")!;
        D(totals.Element(Cbc + "LineExtensionAmount")?.Value).ShouldBe(inv.TaxBaseTotal);
        D(totals.Element(Cbc + "TaxExclusiveAmount")?.Value).ShouldBe(inv.TaxBaseTotal);
        D(totals.Element(Cbc + "TaxInclusiveAmount")?.Value)
            .ShouldBe(inv.TaxBaseTotal + inv.TaxTotal);
        D(totals.Element(Cbc + "PayableAmount")?.Value).ShouldBe(inv.GrandTotal);

        var taxTotal = root.Element(Cac + "TaxTotal")!;
        D(taxTotal.Element(Cbc + "TaxAmount")?.Value).ShouldBe(inv.TaxTotal);
    }

    /// <summary>GİB tek toplam değil, HER KDV ORANI için ayrı alt toplam ister.</summary>
    [Fact]
    public async Task Kdv_oranlari_ayri_alt_toplamlara_ayrilir()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        var id = await IssueAsync(c,
            Line(1m, 1_000m, 0.20m),
            Line(1m, 1_000m, 0.10m),
            Line(1m, 1_000m, 0.01m));

        var root = XDocument.Parse((await c.Ubl.BuildAsync(id)).Xml).Root!;
        var subtotals = root.Element(Cac + "TaxTotal")!.Elements(Cac + "TaxSubtotal").ToList();

        subtotals.Count.ShouldBe(3);

        var percents = subtotals.Select(s => D(s.Element(Cbc + "Percent")?.Value)).ToList();
        percents.ShouldBe([1m, 10m, 20m]);

        // Alt toplamların KDV'si genel KDV toplamına eşit olmalı
        subtotals.Sum(s => D(s.Element(Cbc + "TaxAmount")?.Value)).ShouldBe(310m);
    }

    [Fact]
    public async Task Tevkifatli_fatura_dogru_kodlanir()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        // 10.000 TL, %20 KDV, 7/10 tevkifat
        var id = await IssueAsync(c, Line(1m, 10_000m, 0.20m, withholding: 0.70m));
        var doc = await c.Ubl.BuildAsync(id);
        var root = XDocument.Parse(doc.Xml).Root!;

        root.Element(Cbc + "InvoiceTypeCode")!.Value.ShouldBe("TEVKIFAT");
        root.Element(Cbc + "ProfileID")!.Value.ShouldBe("TICARIFATURA");
        doc.Profile.ShouldBe(EInvoiceProfile.Ticari);

        var withholding = root.Element(Cac + "WithholdingTaxTotal")!;
        D(withholding.Element(Cbc + "TaxAmount")?.Value).ShouldBe(1_400m);

        var sub = withholding.Element(Cac + "TaxSubtotal")!;
        D(sub.Element(Cbc + "Percent")?.Value).ShouldBe(70m);
        sub.Descendants(Cbc + "TaxTypeCode").Single().Value.ShouldBe("0021");

        // Satıcıya ödenecek tutar: 10.000 + 2.000 − 1.400
        D(root.Element(Cac + "LegalMonetaryTotal")!
              .Element(Cbc + "PayableAmount")?.Value).ShouldBe(10_600m);
    }

    [Fact]
    public async Task Satirlar_birim_kodu_ve_tutarlariyla_yazilir()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        var id = await IssueAsync(c, Line(2.5m, 400m, 0.20m, unit: "Saat"));
        var root = XDocument.Parse((await c.Ubl.BuildAsync(id)).Xml).Root!;
        var line = root.Elements(Cac + "InvoiceLine").Single();

        line.Element(Cbc + "ID")!.Value.ShouldBe("1");

        var qty = line.Element(Cbc + "InvoicedQuantity")!;
        D(qty.Value).ShouldBe(2.5m);
        qty.Attribute("unitCode")!.Value.ShouldBe("HUR");   // UN/ECE Rec.20: saat

        D(line.Element(Cbc + "LineExtensionAmount")?.Value).ShouldBe(1_000m);
        line.Element(Cac + "Item")!.Element(Cbc + "Name")!.Value
            .ShouldBe("Danışmanlık Hizmeti");
    }

    /// <summary>Tutarlar XML'de NOKTA ondalık ayırıcı kullanmalı — kültüre duyarlı olamaz.</summary>
    [Fact]
    public async Task Tutarlar_invariant_kultur_ile_yazilir()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        var doc = await c.Ubl.BuildAsync(await IssueAsync(c, Line(1m, 1_234.56m, 0.20m)));

        doc.Xml.ShouldContain("1234.56");
        doc.Xml.ShouldNotContain("1.234,56");   // tr-TR formatı XML'e SIZMAMALI
    }

    [Fact]
    public async Task Taslak_fatura_icin_xml_uretilemez()
    {
        var c = Setup(Guid.CreateVersion7());
        await using var _ = c.Db;

        var id = await c.Invoices.SaveDraftAsync(new InvoiceForm
        {
            PartyId = c.PartyId, Series = "NEX",
            IssueDate = new DateOnly(2026, 3, 1),
            Lines = [Line(1m, 100m, 0.20m)]
        });

        await Should.ThrowAsync<DomainException>(() => c.Ubl.BuildAsync(id));
    }
}
