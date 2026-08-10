using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;
using NexusErp.Domain.Invoicing;
using Shouldly;

namespace NexusErp.Tests.Invoicing;

public class InvoiceCalculatorTests
{
    [Fact]
    public void Basit_satir_kdv_hesabi()
    {
        var r = InvoiceCalculator.CalculateLine(
            new LineInput(Quantity: 10m, UnitPrice: 100m, TaxRate: 0.20m));

        r.GrossAmount.ShouldBe(1_000m);
        r.TaxBase.ShouldBe(1_000m);
        r.TaxAmount.ShouldBe(200m);
        r.LineTotal.ShouldBe(1_200m);
    }

    [Fact]
    public void Yuzde_iskonto_matrahtan_dusulur()
    {
        var r = InvoiceCalculator.CalculateLine(new LineInput(
            Quantity: 10m, UnitPrice: 100m,
            DiscountType: DiscountType.Percentage, DiscountValue: 0.10m,
            TaxRate: 0.20m));

        r.DiscountAmount.ShouldBe(100m);
        r.TaxBase.ShouldBe(900m);
        r.TaxAmount.ShouldBe(180m);      // KDV iskonto SONRASI matrah üzerinden
        r.LineTotal.ShouldBe(1_080m);
    }

    [Fact]
    public void Tevkifat_kdvnin_bir_kismini_alicidan_ayirir()
    {
        // 10.000 TL temizlik hizmeti, %20 KDV, 7/10 tevkifat
        var r = InvoiceCalculator.CalculateLine(new LineInput(
            Quantity: 1m, UnitPrice: 10_000m,
            TaxRate: 0.20m, WithholdingRate: 0.70m));

        r.TaxBase.ShouldBe(10_000m);
        r.TaxAmount.ShouldBe(2_000m);
        r.WithholdingAmount.ShouldBe(1_400m);   // alıcı bunu devlete öder
        r.LineTotal.ShouldBe(10_600m);          // 10.000 + 2.000 − 1.400
    }

    [Fact]
    public void Kademeli_iskonto_carpimsaldir_toplamsal_degil()
    {
        // %10 satır + %5 belge iskontosu → toplam %14,5 (%15 DEĞİL)
        var doc = InvoiceCalculator.CalculateDocument(
            [new LineInput(1m, 1_000m, DiscountType.Percentage, 0.10m, 0.20m)],
            DiscountType.Percentage, 0.05m);

        doc.TaxBaseTotal.ShouldBe(855m);     // 1000 × 0,90 × 0,95
        doc.DiscountTotal.ShouldBe(145m);    // 100 + 45
    }

    [Fact]
    public void Belge_iskontosu_kurus_kaybi_olmadan_dagitilir()
    {
        var doc = InvoiceCalculator.CalculateDocument(
            [
                new LineInput(1m, 100m, TaxRate: 0.20m),
                new LineInput(1m, 100m, TaxRate: 0.20m),
                new LineInput(1m, 100m, TaxRate: 0.20m)
            ],
            DiscountType.Amount, 100m);

        // 100 / 3 = 33,3333... → naif yuvarlama 99,99 verir, bizde tam 100,00
        doc.Lines.Sum(l => l.DocumentDiscountShare).ShouldBe(100m);
        doc.Lines[0].DocumentDiscountShare.ShouldBe(33.34m);   // kalan kuruş buraya
        doc.Lines[1].DocumentDiscountShare.ShouldBe(33.33m);
        doc.TaxBaseTotal.ShouldBe(200m);

        // ⚠️ Genel toplam 240,00 DEĞİL 239,99.
        // Satır bazlı yuvarlamanın kaçınılmaz sonucu: matrahlar 66,66 / 66,67 / 66,67,
        // KDV'ler 13,33 / 13,33 / 13,33 → toplam 39,99 (40,00 değil).
        // Bu bir HATA DEĞİL; GİB satır bazlı yuvarlama istediği için doğru davranış budur.
        doc.TaxTotal.ShouldBe(39.99m);
        doc.GrandTotal.ShouldBe(239.99m);
    }

    [Fact]
    public void Satir_toplamlari_belge_toplamina_esittir()
    {
        var doc = InvoiceCalculator.CalculateDocument(
            [
                new LineInput(3m, 33.33m, TaxRate: 0.20m),
                new LineInput(7m, 12.47m, TaxRate: 0.10m),
                new LineInput(1m, 999.99m, TaxRate: 0.01m)
            ],
            DiscountType.Percentage, 0.07m);

        doc.GrandTotal.ShouldBe(doc.Lines.Sum(l => l.LineTotal));
        doc.TaxBaseTotal.ShouldBe(doc.Lines.Sum(l => l.TaxBase));
        doc.TaxTotal.ShouldBe(doc.Lines.Sum(l => l.TaxAmount));
    }

    [Fact]
    public void Farkli_kdv_oranlari_ayri_hesaplanir()
    {
        var doc = InvoiceCalculator.CalculateDocument(
            [
                new LineInput(1m, 1_000m, TaxRate: 0.20m),   // KDV 200
                new LineInput(1m, 1_000m, TaxRate: 0.10m),   // KDV 100
                new LineInput(1m, 1_000m, TaxRate: 0.01m)    // KDV  10
            ]);

        doc.TaxTotal.ShouldBe(310m);
        doc.GrandTotal.ShouldBe(3_310m);
    }

    [Theory]
    [InlineData(0, 100)]        // miktar sıfır
    [InlineData(-1, 100)]       // negatif miktar
    [InlineData(1, -100)]       // negatif fiyat
    public void Gecersiz_girdiler_reddedilir(decimal qty, decimal price)
    {
        Should.Throw<DomainException>(() =>
            InvoiceCalculator.CalculateLine(new LineInput(qty, price, TaxRate: 0.20m)));
    }

    [Fact]
    public void Iskonto_satir_tutarini_asamaz()
    {
        Should.Throw<DomainException>(() =>
            InvoiceCalculator.CalculateLine(new LineInput(
                1m, 100m, DiscountType.Amount, 150m, 0.20m)));
    }

    [Fact]
    public void Kdv_orani_yuzde_olarak_verilirse_reddedilir()
    {
        // Yaygın hata: 0,20 yerine 20 girmek
        Should.Throw<DomainException>(() =>
            InvoiceCalculator.CalculateLine(new LineInput(1m, 100m, TaxRate: 20m)));
    }

    [Fact]
    public void Kurus_yuvarlamasi_yukari_yapilir()
    {
        // 3 × 33,335 = 100,005 → 100,01 (banker's rounding olsaydı 100,00)
        var r = InvoiceCalculator.CalculateLine(new LineInput(3m, 33.335m, TaxRate: 0m));
        r.GrossAmount.ShouldBe(100.01m);
    }

    [Fact]
    public void Bos_fatura_hesaplanamaz()
    {
        Should.Throw<DomainException>(() => InvoiceCalculator.CalculateDocument([]));
    }

    [Fact]
    public void Belge_iskontosu_fatura_tutarini_asamaz()
    {
        Should.Throw<DomainException>(() => InvoiceCalculator.CalculateDocument(
            [new LineInput(1m, 100m, TaxRate: 0.20m)],
            DiscountType.Amount, 500m));
    }

    [Fact]
    public void Kurus_dagitimi_agirlikli_yapilir()
    {
        // Ağırlıklar eşit değilse dağıtım orantılı olmalı, kalan EN BÜYÜĞE gitmeli
        var shares = InvoiceCalculator.Allocate(10m, [100m, 200m, 700m]);

        shares.Sum().ShouldBe(10m);
        shares[0].ShouldBe(1m);
        shares[1].ShouldBe(2m);
        shares[2].ShouldBe(7m);
    }
}
