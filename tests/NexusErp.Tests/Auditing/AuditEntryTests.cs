using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Auditing;

[Collection(nameof(DatabaseCollection))]
public sealed class AuditEntryTests(DatabaseFixture fixture)
{
    private static Guid NewTenant() => Guid.CreateVersion7();

    private static Party NewParty(Guid tenant, string title = "İstanbul Lojistik A.Ş.") => new()
    {
        TenantId = tenant, Code = "MUS7001", Title = title,
        Type = PartyType.Customer, PaymentTermDays = 30
    };

    [Fact]
    public async Task Kayit_eklenince_insert_denetimi_yazilir()
    {
        var tenant = NewTenant();
        await using var db = fixture.CreateContext(tenant);

        var party = NewParty(tenant);
        db.Parties.Add(party);
        await db.SaveChangesAsync();

        var audit = await db.AuditEntries.AsNoTracking()
            .SingleAsync(a => a.EntityId == party.Id.ToString());

        audit.Action.ShouldBe(AuditAction.Insert);
        audit.EntityName.ShouldBe(nameof(Party));
        audit.TenantId.ShouldBe(tenant);
        audit.UserName.ShouldBe("test");
        audit.OccurredAt.ShouldNotBe(default);
        audit.CreatedAt.ShouldNotBe(default);      // ikinci turda elle dolduruluyor

        var changes = JsonSerializer.Deserialize<JsonElement>(audit.Changes);
        changes.GetProperty("Title").GetString().ShouldBe("İstanbul Lojistik A.Ş.");
    }

    [Fact]
    public async Task Guncelleme_eski_ve_yeni_degeri_kaydeder()
    {
        var tenant = NewTenant();
        await using var db = fixture.CreateContext(tenant);

        var party = NewParty(tenant, "Eski Unvan");
        db.Parties.Add(party);
        await db.SaveChangesAsync();

        party.Title = "Yeni Unvan";
        await db.SaveChangesAsync();

        var audit = await db.AuditEntries.AsNoTracking()
            .Where(a => a.EntityId == party.Id.ToString() && a.Action == AuditAction.Update)
            .SingleAsync();

        var changes = JsonSerializer.Deserialize<JsonElement>(audit.Changes);
        var title = changes.GetProperty("Title");
        title.GetProperty("eski").GetString().ShouldBe("Eski Unvan");
        title.GetProperty("yeni").GetString().ShouldBe("Yeni Unvan");
    }

    /// <summary>Remove() soft delete'e dönüşüyor; denetimde Delete olarak görünmeli.</summary>
    [Fact]
    public async Task Soft_delete_silme_olarak_kaydedilir()
    {
        var tenant = NewTenant();
        await using var db = fixture.CreateContext(tenant);

        var party = NewParty(tenant);
        db.Parties.Add(party);
        await db.SaveChangesAsync();

        db.Parties.Remove(party);
        await db.SaveChangesAsync();

        var audit = await db.AuditEntries.AsNoTracking()
            .Where(a => a.EntityId == party.Id.ToString() && a.Action == AuditAction.Delete)
            .SingleAsync();

        audit.EntityName.ShouldBe(nameof(Party));
    }

    /// <summary>
    /// Denetim kaydının kendisi denetlenirse her SaveChanges yeni kayıt doğurur
    /// ve tablo sonsuza kadar büyür. Bu testin kırılması sessiz bir felakettir.
    /// </summary>
    [Fact]
    public async Task Denetim_kaydinin_kendisi_denetlenmez()
    {
        var tenant = NewTenant();
        await using var db = fixture.CreateContext(tenant);

        db.Parties.Add(NewParty(tenant));
        await db.SaveChangesAsync();

        (await db.AuditEntries.CountAsync(a => a.EntityName == nameof(AuditEntry)))
            .ShouldBe(0);
        (await db.AuditEntries.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Turkce_karakterler_json_icinde_kacis_dizisine_donusmez()
    {
        var tenant = NewTenant();
        await using var db = fixture.CreateContext(tenant);

        var party = NewParty(tenant, "Çığır Öğüt Şirketi ÜÎ");
        db.Parties.Add(party);
        await db.SaveChangesAsync();

        var audit = await db.AuditEntries.AsNoTracking()
            .SingleAsync(a => a.EntityId == party.Id.ToString());

        audit.Changes.ShouldContain("Çığır Öğüt Şirketi ÜÎ");
        audit.Changes.ShouldNotContain("\\u");
    }

    [Fact]
    public async Task Baska_tenantin_denetim_kaydi_gorunmez()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();

        await using (var dbA = fixture.CreateContext(tenantA))
        {
            dbA.Parties.Add(NewParty(tenantA));
            await dbA.SaveChangesAsync();
        }

        await using var dbB = fixture.CreateContext(tenantB);
        (await dbB.AuditEntries.CountAsync()).ShouldBe(0);
    }
}
