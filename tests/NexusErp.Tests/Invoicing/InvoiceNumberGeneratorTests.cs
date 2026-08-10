using NexusErp.Infrastructure.Invoicing;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Invoicing;

[Collection(nameof(DatabaseCollection))]
public sealed class InvoiceNumberGeneratorTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task Numara_gib_formatinda_uretilir()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = fixture.CreateContext(tenant);
        var generator = new InvoiceNumberGenerator(db, fixture.CreateTenantContext(tenant));

        var (number, sequence) = await generator.NextAsync("NEX", 2026);

        number.ShouldBe("NEX2026000000001");    // 3 harf + 4 hane yıl + 9 hane sıra
        sequence.ShouldBe(1);
        number.Length.ShouldBe(16);
    }

    [Fact]
    public async Task Ardisik_numaralar_boslukssuz_ilerler()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = fixture.CreateContext(tenant);
        var generator = new InvoiceNumberGenerator(db, fixture.CreateTenantContext(tenant));

        for (var i = 1; i <= 5; i++)
        {
            var (number, _) = await generator.NextAsync("NEX", 2026);
            number.ShouldBe($"NEX2026{i:D9}");
        }
    }

    [Fact]
    public async Task Seri_ve_yil_ayri_sayac_kullanir()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = fixture.CreateContext(tenant);
        var generator = new InvoiceNumberGenerator(db, fixture.CreateTenantContext(tenant));

        (await generator.NextAsync("NEX", 2026)).Number.ShouldBe("NEX2026000000001");
        (await generator.NextAsync("ABN", 2026)).Number.ShouldBe("ABN2026000000001");
        (await generator.NextAsync("NEX", 2027)).Number.ShouldBe("NEX2027000000001");
        (await generator.NextAsync("NEX", 2026)).Number.ShouldBe("NEX2026000000002");
    }

    [Fact]
    public async Task Tenantlar_ayri_seri_yurutur()
    {
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();

        await using var dbA = fixture.CreateContext(tenantA);
        await using var dbB = fixture.CreateContext(tenantB);

        var genA = new InvoiceNumberGenerator(dbA, fixture.CreateTenantContext(tenantA));
        var genB = new InvoiceNumberGenerator(dbB, fixture.CreateTenantContext(tenantB));

        (await genA.NextAsync("NEX", 2026)).Sequence.ShouldBe(1);
        (await genA.NextAsync("NEX", 2026)).Sequence.ShouldBe(2);
        (await genB.NextAsync("NEX", 2026)).Sequence.ShouldBe(1);   // B kendi sayacı
    }

    /// <summary>
    /// ADR-007'nin kanıtı. "Eşzamanlılığı düşündüm" demek ile 50 paralel görevin
    /// çakışmadığını GÖSTERMEK arasında büyük fark var.
    /// </summary>
    [Fact]
    public async Task Elli_paralel_istek_elli_farkli_numara_alir()
    {
        const int concurrency = 50;
        var tenant = Guid.CreateVersion7();

        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            // ⚠️ Her görev KENDİ DbContext'ini kullanmalı — DbContext thread-safe DEĞİLDİR
            await using var db = fixture.CreateContext(tenant);
            var generator = new InvoiceNumberGenerator(db, fixture.CreateTenantContext(tenant));
            var (number, _) = await generator.NextAsync("PAR", 2026);
            return number;
        });

        var numbers = await Task.WhenAll(tasks);

        numbers.Distinct().Count().ShouldBe(concurrency);          // hiç tekrar yok
        numbers.ShouldAllBe(n => n.StartsWith("PAR2026"));

        // Boşluk da olmamalı: 1..50 kesintisiz (mevzuat boşluksuz seri ister)
        var sequences = numbers.Select(n => long.Parse(n[7..])).OrderBy(x => x).ToArray();
        sequences.ShouldBe(Enumerable.Range(1, concurrency).Select(i => (long)i).ToArray());
    }
}
