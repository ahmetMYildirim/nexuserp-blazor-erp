using NexusErp.Domain.ValueObjects;
using Shouldly;

namespace NexusErp.Tests.Domain;

public class TaxIdentifierTests
{
    [Theory]
    [InlineData("1234567890")]
    public void Gecerli_vkn_kabul_edilir(string value)
    {
        TaxIdentifier.TryParse(value, out var id).ShouldBeTrue();
        id.Kind.ShouldBe(TaxIdKind.Vkn);
        id.Value.ShouldBe(value);
    }

    [Theory]
    [InlineData("10000000146")]
    public void Gecerli_tckn_kabul_edilir(string value)
    {
        TaxIdentifier.TryParse(value, out var id).ShouldBeTrue();
        id.Kind.ShouldBe(TaxIdKind.Tckn);
    }

    [Theory]
    [InlineData("1234567891")]     // yanlış kontrol basamağı
    [InlineData("1111111111")]     // geçersiz VKN
    [InlineData("12345678901")]    // 11 hane ama geçersiz TCKN
    [InlineData("00000000146")]    // ilk hane 0
    [InlineData("123")]            // kısa
    [InlineData("")]
    [InlineData(null)]
    public void Gecersiz_degerler_reddedilir(string? value)
    {
        TaxIdentifier.TryParse(value, out _).ShouldBeFalse();
    }

    [Fact]
    public void Bosluk_ve_tire_temizlenir()
    {
        TaxIdentifier.TryParse("123 456 78-90", out var id).ShouldBeTrue();
        id.Value.ShouldBe("1234567890");
    }
}
