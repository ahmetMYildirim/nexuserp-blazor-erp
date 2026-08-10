using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Infrastructure.Persistence;

namespace NexusErp.Infrastructure.Invoicing;

public sealed class InvoiceNumberGenerator(AppDbContext db, ITenantContext tenant)
    : IInvoiceNumberGenerator
{
    public async Task<(string Number, long Sequence)> NextAsync(
        string series, int year, CancellationToken ct = default)
    {
        var tenantId = tenant.TenantId;
        var now = DateTimeOffset.UtcNow;
        var id = Guid.CreateVersion7();

        // ADR-007: TEK ifade, TEK kilit, yarış koşulu yok.
        //
        // Neden "SELECT MAX(no)+1" değil?
        //   İş A: SELECT → 5      İş B: SELECT → 5
        //   İş A: INSERT 6        İş B: INSERT 6      ← AYNI NUMARA ✗
        //
        // Neden ON CONFLICT DO UPDATE?
        //   "kayıt var mı, yoksa oluştur, sonra artır" üç adımdır ve adımlar arasında
        //   başka bir istek araya girebilir. UPSERT tek atomik işlemdir.
        //
        // ⚠️ AS "Value" çift tırnaklı olmak ZORUNDA: EF Core'un SqlQuery<T> metodu
        // skaler tipler için kolonun "Value" adında olmasını bekler; tırnaksız
        // yazarsan PostgreSQL kolonu "value" yapar ve EF bulamaz.
        // ⚠️ ToListAsync — SingleAsync/FirstAsync DEĞİL.
        // Bu LINQ operatörleri sorguyu alt sorgu içine sarar ("SELECT ... FROM (...) LIMIT 2")
        // ama INSERT ... RETURNING "composable" değildir; EF Core şu hatayı verir:
        // "'FromSql' or 'SqlQuery' was called with non-composable SQL".
        // ToListAsync sarmalamadan doğrudan çalıştırır.
        var rows = await db.Database
            .SqlQuery<long>($"""
                INSERT INTO invoice_counters
                    (id, tenant_id, series, year, last_number,
                     created_at, created_by, is_deleted)
                VALUES
                    ({id}, {tenantId}, {series}, {year}, 1, {now}, 'system', false)
                ON CONFLICT (tenant_id, series, year)
                DO UPDATE SET last_number = invoice_counters.last_number + 1
                RETURNING last_number AS "Value"
                """)
            .ToListAsync(ct);

        var sequence = rows[0];
        return ($"{series}{year}{sequence:D9}", sequence);
    }
}
