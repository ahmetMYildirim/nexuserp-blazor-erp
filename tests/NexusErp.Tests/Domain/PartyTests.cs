using NexusErp.Domain.Common;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Domain.ValueObjects;
using Shouldly;

namespace NexusErp.Tests.Domain;

public class PartyTests
{
    [Fact]
    public void Vade_tarihi_odeme_vadesine_gore_hesaplanir()
    {
        var party = new Party { PaymentTermDays = 45 };

        party.CalculateDueDate(new DateOnly(2026, 3, 1))
             .ShouldBe(new DateOnly(2026, 4, 15));
    }

    [Fact]
    public void Gecerli_vkn_atanir_ve_turu_belirlenir()
    {
        var party = new Party();
        party.SetTaxNumber("1234567890");

        party.TaxNumber.ShouldBe("1234567890");
        party.TaxNumberKind.ShouldBe(TaxIdKind.Vkn);
    }

    [Fact]
    public void Gecersiz_vkn_atanamaz()
    {
        var party = new Party();
        Should.Throw<DomainException>(() => party.SetTaxNumber("1111111111"));
    }

    [Fact]
    public void Bos_vergi_numarasi_temizler()
    {
        var party = new Party();
        party.SetTaxNumber("1234567890");
        party.SetTaxNumber(null);

        party.TaxNumber.ShouldBeNull();
        party.TaxNumberKind.ShouldBeNull();
    }

    [Fact]
    public void Hem_musteri_hem_tedarikci_olabilir()
    {
        var party = new Party { Type = PartyType.Both };

        party.IsCustomer.ShouldBeTrue();
        party.IsSupplier.ShouldBeTrue();
    }

    [Fact]
    public void Pasif_cariye_fatura_kesilemez()
    {
        var party = new Party { Title = "Test A.Ş.", IsActive = false };

        Should.Throw<DomainException>(party.EnsureCanBeInvoiced)
              .Message.ShouldContain("pasif");
    }

    [Fact]
    public void Tedarikciye_satis_faturasi_kesilemez()
    {
        var party = new Party { Title = "Tedarikçi Ltd.", Type = PartyType.Supplier };

        Should.Throw<DomainException>(party.EnsureCanBeInvoiced)
              .Message.ShouldContain("müşteri tipinde değil");
    }
}
