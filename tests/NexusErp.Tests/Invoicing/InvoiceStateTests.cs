using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using Shouldly;

namespace NexusErp.Tests.Invoicing;

public class InvoiceStateTests
{
    private static Invoice Draft(decimal total = 1_000m) => new()
    {
        Series = "NEX",
        Year = 2026,
        PartyTitle = "Test A.Ş.",
        GrandTotal = total,
        Lines = [new InvoiceLine { LineNumber = 1, ProductCode = "X", ProductName = "Y" }]
    };

    private static readonly DateTimeOffset Now = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Taslak_kesilince_numara_ve_durum_atanir()
    {
        var inv = Draft();
        inv.MarkIssued("NEX2026000000001", 1, Now);

        inv.Number.ShouldBe("NEX2026000000001");
        inv.Sequence.ShouldBe(1);
        inv.Status.ShouldBe(InvoiceStatus.Issued);
        inv.IssuedAt.ShouldBe(Now);
        inv.IsEditable.ShouldBeFalse();
    }

    [Fact]
    public void Kesilmis_fatura_ikinci_kez_kesilemez()
    {
        var inv = Draft();
        inv.MarkIssued("NEX2026000000001", 1, Now);

        Should.Throw<DomainException>(() => inv.MarkIssued("NEX2026000000002", 2, Now));
    }

    [Fact]
    public void Satirsiz_fatura_kesilemez()
    {
        var inv = Draft();
        inv.Lines.Clear();

        Should.Throw<DomainException>(() => inv.MarkIssued("NEX2026000000001", 1, Now))
              .Message.ShouldContain("Satırı olmayan");
    }

    [Fact]
    public void Sifir_tutarli_fatura_kesilemez()
    {
        var inv = Draft(total: 0m);

        Should.Throw<DomainException>(() => inv.MarkIssued("NEX2026000000001", 1, Now));
    }

    [Fact]
    public void Kesilmis_fatura_degistirilemez()
    {
        var inv = Draft();
        inv.MarkIssued("NEX2026000000001", 1, Now);

        Should.Throw<DomainException>(inv.EnsureEditable)
              .Message.ShouldContain("değiştirilemez");
    }

    [Fact]
    public void Taslak_iptal_edilemez_silinir()
    {
        Should.Throw<DomainException>(Draft().Cancel)
              .Message.ShouldContain("silinir");
    }

    [Fact]
    public void Tahsilati_olan_fatura_iptal_edilemez()
    {
        var inv = Draft();
        inv.MarkIssued("NEX2026000000001", 1, Now);
        inv.PaidAmount = 500m;

        Should.Throw<DomainException>(inv.Cancel)
              .Message.ShouldContain("Tahsilatı olan");
    }

    [Fact]
    public void Tahsilat_durumu_otomatik_guncellenir()
    {
        var inv = Draft(total: 1_000m);
        inv.MarkIssued("NEX2026000000001", 1, Now);

        inv.PaidAmount = 0m;
        inv.RefreshPaymentStatus();
        inv.Status.ShouldBe(InvoiceStatus.Issued);

        inv.PaidAmount = 400m;
        inv.RefreshPaymentStatus();
        inv.Status.ShouldBe(InvoiceStatus.PartiallyPaid);
        inv.RemainingAmount.ShouldBe(600m);

        inv.PaidAmount = 1_000m;
        inv.RefreshPaymentStatus();
        inv.Status.ShouldBe(InvoiceStatus.Paid);
        inv.RemainingAmount.ShouldBe(0m);
    }

    [Fact]
    public void Proforma_cari_bakiyeye_islemez()
    {
        var inv = Draft();
        inv.Type = InvoiceType.Proforma;
        inv.MarkIssued("PRF2026000000001", 1, Now);

        inv.AffectsBalance.ShouldBeFalse();
    }

    [Fact]
    public void Satis_faturasi_kesilince_cari_bakiyeye_isler()
    {
        var inv = Draft();
        inv.AffectsBalance.ShouldBeFalse();      // taslakken hayır

        inv.MarkIssued("NEX2026000000001", 1, Now);
        inv.AffectsBalance.ShouldBeTrue();       // kesilince evet

        inv.Cancel();
        inv.AffectsBalance.ShouldBeFalse();      // iptal edilince hayır
    }
}
