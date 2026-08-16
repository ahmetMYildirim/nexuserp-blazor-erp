using NexusErp.Application.Abstractions;
using NexusErp.Application.Accounting;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Infrastructure.Invoicing;
using NexusErp.Infrastructure.Persistence.Seed;
using NexusErp.Tests.Infrastructure;

namespace NexusErp.Tests.Accounting;

/// <summary>
/// Muhasebe testleri için ortak kurulum: hesap planı + cari + KDV oranı.
///
/// Hesap planı HER tenant için ayrı tohumlanıyor — testler farklı tenant'larla
/// aynı şemayı paylaştığı için biri diğerinin hesaplarını görmemeli. Zaten
/// tenant izolasyonu testlerinden biri tam olarak bunu doğruluyor.
/// </summary>
public abstract class AccountingTestBase(DatabaseFixture fixture)
{
    protected DatabaseFixture Fixture { get; } = fixture;

    protected sealed record Seed(Guid CustomerId, Guid SupplierId, Guid TaxRateId);

    protected static Guid NewTenant() => Guid.CreateVersion7();

    protected async Task<Seed> SeedAsync(Guid tenant)
    {
        await using var db = Fixture.CreateContext(tenant);
        await ChartOfAccountsSeeder.EnsureAsync(db, tenant);

        var taxRate = new TaxRate
        {
            TenantId = tenant, Code = "KDV20", Name = "KDV %20", Rate = 0.20m,
            ValidFrom = new DateOnly(2020, 1, 1), IsDefault = true
        };
        var customer = new Party
        {
            TenantId = tenant, Code = "MUS9001", Title = "Muhasebe Test Müşterisi",
            Type = PartyType.Customer, PaymentTermDays = 30
        };
        var supplier = new Party
        {
            TenantId = tenant, Code = "TED9001", Title = "Muhasebe Test Tedarikçisi",
            Type = PartyType.Supplier, PaymentTermDays = 45
        };

        db.TaxRates.Add(taxRate);
        db.Parties.AddRange(customer, supplier);
        await db.SaveChangesAsync();

        return new Seed(customer.Id, supplier.Id, taxRate.Id);
    }

    protected IInvoiceNumberGenerator Numbers(Guid tenant) =>
        new InvoiceNumberGenerator(
            Fixture.CreateContext(tenant), Fixture.CreateTenantContext(tenant));

    protected AutoPostingService Posting(Guid tenant) => new(Numbers(tenant));

    protected JournalService Journals(Guid tenant) =>
        new(Fixture.CreateFactory(tenant), Numbers(tenant), TimeProvider.System);

    protected ChartOfAccountsService Accounts(Guid tenant) =>
        new(Fixture.CreateFactory(tenant));

    protected AccountingReportService Reports(Guid tenant) =>
        new(Fixture.CreateFactory(tenant));

    protected async Task<Guid> AccountIdAsync(Guid tenant, string code)
    {
        var list = await Accounts(tenant).PostableAsync();
        return list.First(a => a.Code == code).Id;
    }
}
