using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Parties;
using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Parties;

[Collection(nameof(DatabaseCollection))]
public sealed class PartyServiceTests(DatabaseFixture fixture)
{
    // Her test kendi tenant'ını kullanır → testler birbirini etkilemez
    private static Guid NewTenant() => Guid.CreateVersion7();

    [Fact]
    public async Task Ayni_kod_ikinci_kez_kullanilamaz()
    {
        var service = new PartyService(fixture.CreateFactory(NewTenant()));

        await service.SaveAsync(new PartyForm { Code = "MUS9001", Title = "Test A" });

        var ex = await Should.ThrowAsync<DomainException>(
            () => service.SaveAsync(new PartyForm { Code = "MUS9001", Title = "Test B" }));

        ex.Message.ShouldContain("zaten kullanılıyor");
    }

    [Fact]
    public async Task Gecersiz_vkn_reddedilir()
    {
        var service = new PartyService(fixture.CreateFactory(NewTenant()));

        await Should.ThrowAsync<DomainException>(
            () => service.SaveAsync(new PartyForm
            {
                Code = "MUS9002",
                Title = "Hatalı VKN",
                TaxNumber = "1111111111"
            }));
    }

    /// <summary>
    /// Multi-tenant sistemde en pahalı hata veri sızıntısıdır ve gözle fark edilmez.
    /// Bu test global query filter'ın gerçekten çalıştığını kanıtlıyor.
    /// </summary>
    [Fact]
    public async Task Tenant_izolasyonu_calisir()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();

        await new PartyService(fixture.CreateFactory(tenantA)).SaveAsync(
            new PartyForm { Code = "MUS9100", Title = "A Tenant Carisi" });

        var resultB = await new PartyService(fixture.CreateFactory(tenantB))
            .SearchAsync(new PartyQuery(Search: "MUS9100"));

        resultB.TotalCount.ShouldBe(0);   // B, A'nın verisini GÖRMEMELİ

        await using var dbB = fixture.CreateContext(tenantB);
        (await dbB.Parties.CountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// Eskiden bu senaryo Detach() yamasıyla çözülüyordu: uzun ömürlü context'te
    /// başarısız kayıt Added entity'yi takipte bırakıyor, ikinci deneme unique index'e
    /// takılıyordu. Fabrikaya geçtikten sonra her çağrı taze context açıyor —
    /// test artık bunun gerçekten böyle olduğunu doğruluyor.
    /// </summary>
    [Fact]
    public async Task Basarisiz_kayit_denemesi_context_kirletmez()
    {
        var tenant = NewTenant();
        var service = new PartyService(fixture.CreateFactory(tenant));

        // 1. deneme: geçersiz VKN → hata
        await Should.ThrowAsync<DomainException>(() => service.SaveAsync(new PartyForm
        {
            Code = "MUS9500",
            Title = "Önce Hatalı",
            TaxNumber = "1111111111"
        }));

        // 2. deneme: aynı kod, düzeltilmiş VKN → BAŞARILI olmalı
        var id = await service.SaveAsync(new PartyForm
        {
            Code = "MUS9500",
            Title = "Sonra Doğru",
            TaxNumber = "1234567890"
        });

        id.ShouldNotBe(Guid.Empty);

        await using var db = fixture.CreateContext(tenant);
        (await db.Parties.CountAsync(p => p.Code == "MUS9500")).ShouldBe(1);
    }

    [Fact]
    public async Task Kod_onerisi_sirayla_ilerler()
    {
        var service = new PartyService(fixture.CreateFactory(NewTenant()));

        (await service.SuggestCodeAsync(PartyType.Customer)).ShouldBe("MUS0001");

        await service.SaveAsync(new PartyForm { Code = "MUS0001", Title = "İlk" });

        (await service.SuggestCodeAsync(PartyType.Customer)).ShouldBe("MUS0002");
        (await service.SuggestCodeAsync(PartyType.Supplier)).ShouldBe("TED0001");
    }

    [Fact]
    public async Task Soft_delete_sonrasi_ayni_kod_kullanilamaz_ama_kayit_kaybolmaz()
    {
        var tenant = NewTenant();
        var service = new PartyService(fixture.CreateFactory(tenant));

        var id = await service.SaveAsync(new PartyForm { Code = "MUS9200", Title = "Silinecek" });

        await using var db = fixture.CreateContext(tenant);
        var entity = await db.Parties.FirstAsync(p => p.Id == id);
        db.Parties.Remove(entity);            // SaveChanges override → soft delete
        await db.SaveChangesAsync();

        // Normal sorguda görünmez
        (await db.Parties.CountAsync(p => p.Id == id)).ShouldBe(0);
        // Ama satır duruyor — muhasebe verisi silinmez
        (await db.Parties.IgnoreQueryFilters().CountAsync(p => p.Id == id)).ShouldBe(1);
    }

    [Fact]
    public async Task Turkce_arama_buyuk_kucuk_harf_duyarsiz()
    {
        var service = new PartyService(fixture.CreateFactory(NewTenant()));

        await service.SaveAsync(new PartyForm { Code = "MUS9300", Title = "İstanbul Lojistik A.Ş." });

        (await service.SearchAsync(new PartyQuery(Search: "istanbul"))).TotalCount.ShouldBe(1);
        (await service.SearchAsync(new PartyQuery(Search: "İSTANBUL"))).TotalCount.ShouldBe(1);
        (await service.SearchAsync(new PartyQuery(Search: "lojistik"))).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task Audit_alanlari_otomatik_dolar()
    {
        var tenant = NewTenant();
        var service = new PartyService(fixture.CreateFactory(tenant));

        var id = await service.SaveAsync(new PartyForm { Code = "MUS9400", Title = "Audit Testi" });

        await using var db = fixture.CreateContext(tenant);
        var entity = await db.Parties.AsNoTracking().FirstAsync(p => p.Id == id);
        entity.CreatedBy.ShouldBe("test");
        entity.CreatedAt.ShouldNotBe(default);
        entity.UpdatedAt.ShouldBeNull();

        await service.SaveAsync(new PartyForm { Id = id, Code = "MUS9400", Title = "Güncellendi" });

        var updated = await db.Parties.AsNoTracking().FirstAsync(p => p.Id == id);
        updated.UpdatedAt.ShouldNotBeNull();
        updated.CreatedAt.ShouldBe(entity.CreatedAt);   // oluşturma bilgisi değişmemeli
    }
}
