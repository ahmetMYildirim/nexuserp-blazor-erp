using NexusErp.Domain.Common;
using NexusErp.Domain.ValueObjects;
using Shouldly;

namespace NexusErp.Tests.Domain;

public class MoneyTests
{
    [Fact]
    public void Round_yukari_yuvarlamali_bankers_rounding_yapmamali()
    {
        // .NET varsayılanı ToEven olsaydı 2,00 gelirdi — faturada kuruş farkı demek
        Money.Try(2.005m).Round().Amount.ShouldBe(2.01m);
        Money.Try(2.015m).Round().Amount.ShouldBe(2.02m);
        Money.Try(-2.005m).Round().Amount.ShouldBe(-2.01m);
    }

    [Fact]
    public void Farkli_para_birimleri_toplanamaz()
    {
        var tl = Money.Try(100m);
        var usd = new Money(50m, "USD");

        Should.Throw<DomainException>(() => tl + usd);
    }

    [Fact]
    public void Ayni_para_birimleri_toplanir()
    {
        (Money.Try(100.50m) + Money.Try(49.50m)).Amount.ShouldBe(150m);
    }

    [Fact]
    public void Gecersiz_para_birimi_reddedilir()
    {
        Should.Throw<DomainException>(() => new Money(10m, "TRYX"));
        Should.Throw<DomainException>(() => new Money(10m, ""));
    }

    [Fact]
    public void Para_birimi_buyuk_harfe_cevrilir()
    {
        new Money(10m, "usd").Currency.ShouldBe("USD");
    }
}
