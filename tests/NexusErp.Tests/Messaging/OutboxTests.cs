using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Accounting;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Events;
using NexusErp.Application.Invoicing;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Infrastructure.Invoicing;
using NexusErp.Tests.Infrastructure;
using Shouldly;

namespace NexusErp.Tests.Messaging;

/// <summary>
/// Outbox deseni. Kritik testler: olayın iş verisiyle AYNI transaction'da
/// yazıldığı, işçinin bekleyenleri doğru seçtiği ve hata durumunda mesajın
/// kaybolmadığı.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class OutboxTests(DatabaseFixture fixture)
{
    private static Guid NewTenant() => Guid.CreateVersion7();
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Broker yerine geçen sahte yayıncı — testler RabbitMQ'ya bağlanmaz.</summary>
    private sealed class FakePublisher : IEventPublisher
    {
        public List<(string Type, Guid MessageId, Guid TenantId)> Sent { get; } = [];
        public bool ShouldFail { get; set; }

        public Task PublishAsync(string type, string payload, Guid messageId,
                                 Guid tenantId, CancellationToken ct = default)
        {
            if (ShouldFail) throw new InvalidOperationException("Broker kapalı.");
            Sent.Add((type, messageId, tenantId));
            return Task.CompletedTask;
        }
    }

    private async Task<(Guid PartyId, InvoiceService Service)> SetupAsync(Guid tenant)
    {
        await using var db = fixture.CreateContext(tenant);
        var party = new Party
        {
            TenantId = tenant, Code = "MUS7101", Title = "Outbox Testi A.Ş.",
            Type = PartyType.Customer, PaymentTermDays = 30
        };
        db.Parties.Add(party);
        await db.SaveChangesAsync();

        var generator = new InvoiceNumberGenerator(
            fixture.CreateContext(tenant), fixture.CreateTenantContext(tenant));

        fixture.SeedChartOfAccounts(tenant);

        return (party.Id, new InvoiceService(
            fixture.CreateFactory(tenant), generator, TimeProvider.System,
            new AutoPostingService(generator)));
    }

    private static InvoiceForm Form(Guid partyId, decimal amount) => new()
    {
        PartyId = partyId,
        Series = "NEX",
        IssueDate = Today,
        Lines =
        [
            new InvoiceLineForm
            {
                ProductCode = "HZM", ProductName = "Hizmet", Unit = "Adet",
                Quantity = 1m, UnitPrice = amount, TaxRate = 0m
            }
        ]
    };

    [Fact]
    public async Task Fatura_kesilince_outbox_satiri_olusur()
    {
        var tenant = NewTenant();
        var (partyId, service) = await SetupAsync(tenant);

        var id = await service.SaveDraftAsync(Form(partyId, 1_000m));
        await service.IssueAsync(id);

        await using var db = fixture.CreateContext(tenant);
        var msg = await db.OutboxMessages.SingleAsync();

        msg.Type.ShouldBe(nameof(InvoiceIssued));
        msg.ProcessedAt.ShouldBeNull();          // işçi henüz çalışmadı
        msg.AttemptCount.ShouldBe(0);
        msg.TenantId.ShouldBe(tenant);           // SaveChanges otomatik atadı
        msg.Payload.ShouldContain("\"Number\"");
    }

    [Fact]
    public async Task Taslak_kaydi_olay_uretmez()
    {
        var tenant = NewTenant();
        var (partyId, service) = await SetupAsync(tenant);

        await service.SaveDraftAsync(Form(partyId, 500m));   // kesilmedi

        await using var db = fixture.CreateContext(tenant);
        (await db.OutboxMessages.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Turkce_karakterler_kacis_dizisine_donusmez()
    {
        var tenant = NewTenant();
        await using (var seed = fixture.CreateContext(tenant))
        {
            seed.Parties.Add(new Party
            {
                TenantId = tenant, Code = "MUS7102", Title = "Çiğdem Öz Şirketi",
                Type = PartyType.Customer, PaymentTermDays = 30
            });
            await seed.SaveChangesAsync();
        }

        await using var db2 = fixture.CreateContext(tenant);
        var party = await db2.Parties.FirstAsync(p => p.Code == "MUS7102");

        var generator = new InvoiceNumberGenerator(
            fixture.CreateContext(tenant), fixture.CreateTenantContext(tenant));
        fixture.SeedChartOfAccounts(tenant);

        var service = new InvoiceService(
            fixture.CreateFactory(tenant), generator, TimeProvider.System,
            new AutoPostingService(generator));

        var id = await service.SaveDraftAsync(Form(party.Id, 100m));
        await service.IssueAsync(id);

        await using var db = fixture.CreateContext(tenant);
        var msg = await db.OutboxMessages.SingleAsync();

        msg.Payload.ShouldContain("Çiğdem Öz Şirketi");
        msg.Payload.ShouldNotContain("\\u");
    }

    /// <summary>
    /// Outbox satırı denetim kaydı üretmemeli. Bu koruma unutulursa hiçbir test
    /// kırılmaz, hiçbir hata çıkmaz — audit_entries tablosu sessizce şişer.
    /// </summary>
    [Fact]
    public async Task Outbox_satiri_denetim_kaydi_uretmez()
    {
        var tenant = NewTenant();
        var (partyId, service) = await SetupAsync(tenant);

        var id = await service.SaveDraftAsync(Form(partyId, 1_000m));
        await service.IssueAsync(id);

        await using var db = fixture.CreateContext(tenant);
        (await db.AuditEntries.CountAsync(a => a.EntityName == nameof(OutBoxMessage)))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Isci_bekleyen_mesaji_yayinlar_ve_isaretler()
    {
        var tenant = NewTenant();
        var (partyId, service) = await SetupAsync(tenant);

        var id = await service.SaveDraftAsync(Form(partyId, 2_500m));
        await service.IssueAsync(id);

        var publisher = new FakePublisher();
        await RunWorkerOnceAsync(tenant, publisher);

        publisher.Sent.ShouldHaveSingleItem().Type.ShouldBe(nameof(InvoiceIssued));

        await using var db = fixture.CreateContext(tenant);
        var msg = await db.OutboxMessages.SingleAsync();
        msg.ProcessedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Yayin_hata_verirse_deneme_sayaci_artar_mesaj_kaybolmaz()
    {
        var tenant = NewTenant();
        var (partyId, service) = await SetupAsync(tenant);

        var id = await service.SaveDraftAsync(Form(partyId, 900m));
        await service.IssueAsync(id);

        var publisher = new FakePublisher { ShouldFail = true };
        await RunWorkerOnceAsync(tenant, publisher);

        publisher.Sent.ShouldBeEmpty();

        await using var db = fixture.CreateContext(tenant);
        var msg = await db.OutboxMessages.SingleAsync();
        msg.ProcessedAt.ShouldBeNull();          // yayınlanmadı
        msg.AttemptCount.ShouldBe(1);
        msg.LastError.ShouldNotBeNull().ShouldContain("Broker kapalı");
    }

    [Fact]
    public async Task Islenmis_mesaj_tekrar_alinmaz()
    {
        var tenant = NewTenant();
        var (partyId, service) = await SetupAsync(tenant);

        var id = await service.SaveDraftAsync(Form(partyId, 700m));
        await service.IssueAsync(id);

        var first = new FakePublisher();
        await RunWorkerOnceAsync(tenant, first);
        first.Sent.Count.ShouldBe(1);

        var second = new FakePublisher();
        await RunWorkerOnceAsync(tenant, second);
        second.Sent.ShouldBeEmpty();             // ProcessedAt dolu, tekrar seçilmiyor
    }

    /// <summary>
    /// İşçinin sorgusu IgnoreQueryFilters kullanıyor: tek tur TÜM tenant'ların
    /// mesajlarını basmalı. Filtre açık kalsaydı yalnızca varsayılan tenant giderdi.
    /// </summary>
    [Fact]
    public async Task Farkli_tenantlarin_mesajlari_ayni_turda_yayinlanir()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();

        foreach (var t in new[] { tenantA, tenantB })
        {
            var (partyId, service) = await SetupAsync(t);
            var id = await service.SaveDraftAsync(Form(partyId, 400m));
            await service.IssueAsync(id);
        }

        // A tenant'ının context'iyle çalışıyoruz ama iki tenant'ın mesajı da gelmeli
        var publisher = new FakePublisher();
        await RunWorkerOnceAsync(tenantA, publisher, tenantA, tenantB);

        publisher.Sent.Count.ShouldBe(2);
        publisher.Sent.Select(s => s.TenantId).ShouldBe([tenantA, tenantB], ignoreOrder: true);
    }

    // ------------------------------------------------------------------
    // İşçinin sorgusunu birebir taklit eder. Asıl amacı FOR UPDATE SKIP LOCKED
    // içeren ham SQL'in EF Core tarafından çalıştırılabildiğini kanıtlamak:
    // IgnoreQueryFilters olmadan "non-composable SQL" hatası alınır.
    // ------------------------------------------------------------------
    /// <param name="scope">
    /// ⚠️ Testler aynı veri tabanını paylaşıyor ve işçinin sorgusu tenant filtresi
    /// TANIMIYOR — başka testlerin bıraktığı satırları da toplar. Deterministik
    /// olsun diye sorguya test tenant'ları ekleniyor. IgnoreQueryFilters yine
    /// kanıtlanıyor: filtre açık kalsaydı listede olsa bile ikinci tenant'ın
    /// satırları görünmezdi.
    /// </param>
    private async Task RunWorkerOnceAsync(Guid contextTenant, IEventPublisher publisher,
                                          params Guid[] scope)
    {
        var tenants = scope.Length == 0 ? [contextTenant] : scope;

        await using var db = fixture.CreateContext(contextTenant);
        await using var tx = await db.Database.BeginTransactionAsync();

        var messages = await db.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE processed_at IS NULL
                  AND attempt_count < 5
                  AND tenant_id = ANY({tenants})
                ORDER BY occured_at
                LIMIT 20
                FOR UPDATE SKIP LOCKED
                """)
            .IgnoreQueryFilters()
            .ToListAsync();

        foreach (var msg in messages)
        {
            try
            {
                await publisher.PublishAsync(msg.Type, msg.Payload, msg.Id, msg.TenantId);
                msg.ProcessedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                msg.AttemptCount++;
                msg.LastError = ex.Message;
            }
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
