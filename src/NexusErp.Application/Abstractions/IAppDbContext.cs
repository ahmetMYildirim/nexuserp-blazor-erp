using Microsoft.EntityFrameworkCore;
using NexusErp.Domain.Entities;

namespace NexusErp.Application.Abstractions;


public interface IAppDbContext : IAsyncDisposable
{

    Guid CurrentTenantId { get; }

    DbSet<Tenant> Tenants { get; }
    DbSet<Party> Parties { get; }
    DbSet<TaxRate> TaxRates { get; }
    DbSet<Product> Products { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<InvoiceLine> InvoiceLines { get; }
    DbSet<Plan> Plans { get; }
    DbSet<Subscription> Subscriptions { get; }
    DbSet<UsageRecord> UsageRecords { get; }
    DbSet<Payment> Payments { get; }
    DbSet<PaymentAllocation> PaymentAllocations { get; }
    DbSet<PartyLedgerEntry> PartyLedgerEntries { get; }
    DbSet<Account> Accounts { get; }
    DbSet<JournalEntry> JournalEntries { get; }
    DbSet<JournalLine> JournalLines { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<OutBoxMessage> OutboxMessages { get; }
    DbSet<ProcessedMessage> ProcessedMessages { get; }


    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
