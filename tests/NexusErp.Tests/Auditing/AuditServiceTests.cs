using NexusErp.Application.Auditing;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Auditing;

/// <summary>ParseChanges saf mantık — veri tabanı gerekmiyor.</summary>
public sealed class AuditChangeParsingTests
{
    [Fact]
    public void Insert_kaydinda_alan_tek_deger_olarak_okunur()
    {
        var rows = AuditService.ParseChanges("""{"Title":"İstanbul Lojistik","PaymentTermDays":30}""");

        rows.Count.ShouldBe(2);
        rows[0].Field.ShouldBe("Title");
        rows[0].Before.ShouldBeNull();
        rows[0].After.ShouldBe("İstanbul Lojistik");
        rows[1].After.ShouldBe("30");
    }

    [Fact]
    public void Update_kaydinda_eski_ve_yeni_ayrisir()
    {
        var rows = AuditService.ParseChanges("""{"Title":{"eski":"Eski A.Ş.","yeni":"Yeni A.Ş."}}""");

        var row = rows.ShouldHaveSingleItem();
        row.Field.ShouldBe("Title");
        row.Before.ShouldBe("Eski A.Ş.");
        row.After.ShouldBe("Yeni A.Ş.");
    }

    [Fact]
    public void Null_degerler_bos_olarak_gelir()
    {
        var rows = AuditService.ParseChanges("""{"TaxNumber":{"eski":null,"yeni":"1234567890"}}""");

        var row = rows.ShouldHaveSingleItem();
        row.Before.ShouldBeNull();
        row.After.ShouldBe("1234567890");
    }

    /// <summary>Bozuk tek bir kayıt yüzünden denetim sayfası komple çökmemeli.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{bozuk json")]
    [InlineData("[1,2,3]")]
    public void Gecersiz_json_bos_liste_doner(string json)
        => AuditService.ParseChanges(json).ShouldBeEmpty();
}

[Collection(nameof(DatabaseCollection))]
public sealed class AuditServiceTests(DatabaseFixture fixture)
{
    private static Guid NewTenant() => Guid.CreateVersion7();

    private async Task<Guid> SeedAsync(Guid tenant)
    {
        await using var db = fixture.CreateContext(tenant);

        var party = new Party
        {
            TenantId = tenant, Code = "MUS8001", Title = "Denetim Testi A.Ş.",
            Type = PartyType.Customer, PaymentTermDays = 30
        };
        db.Parties.Add(party);
        await db.SaveChangesAsync();          // Insert denetimi

        party.Title = "Denetim Testi Güncel";
        await db.SaveChangesAsync();          // Update denetimi

        return party.Id;
    }

    [Fact]
    public async Task Islem_tipine_gore_filtreler()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);
        var service = new AuditService(fixture.CreateFactory(tenant));

        var inserts = await service.SearchAsync(new AuditQuery(Action: AuditAction.Insert));
        var updates = await service.SearchAsync(new AuditQuery(Action: AuditAction.Update));

        inserts.TotalCount.ShouldBe(1);
        updates.TotalCount.ShouldBe(1);
        updates.Items[0].EntityName.ShouldBe(nameof(Party));
    }

    [Fact]
    public async Task Kayit_tipine_gore_filtreler()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);
        var service = new AuditService(fixture.CreateFactory(tenant));

        (await service.SearchAsync(new AuditQuery(EntityName: nameof(Party)))).TotalCount.ShouldBe(2);
        (await service.SearchAsync(new AuditQuery(EntityName: "Invoice"))).TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task Tarih_araligi_bitis_gununu_de_kapsar()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);
        var service = new AuditService(fixture.CreateFactory(tenant));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Bugün hem başlangıç hem bitiş — kayıtlar dahil olmalı
        (await service.SearchAsync(new AuditQuery(From: today, To: today))).TotalCount.ShouldBe(2);

        // Dünle sınırlarsak hiçbiri girmemeli
        (await service.SearchAsync(new AuditQuery(To: today.AddDays(-1)))).TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task En_yeni_kayit_once_gelir()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);
        var service = new AuditService(fixture.CreateFactory(tenant));

        var result = await service.SearchAsync(new AuditQuery());

        result.Items[0].Action.ShouldBe(AuditAction.Update);
        result.Items[1].Action.ShouldBe(AuditAction.Insert);
    }

    [Fact]
    public async Task Entity_adlari_yalnizca_kayitli_olanlari_doner()
    {
        var tenant = NewTenant();
        await SeedAsync(tenant);
        var service = new AuditService(fixture.CreateFactory(tenant));

        (await service.GetEntityNamesAsync()).ShouldBe([nameof(Party)]);
    }
}
