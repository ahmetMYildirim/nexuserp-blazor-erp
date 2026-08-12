using System.Diagnostics;
using Bogus;
using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Payments;
using NexusErp.Domain.Entities;
using NexusErp.Domain.Enums;
using NexusErp.Infrastructure.Persistence;
using NexusErp.Tests.Infrastructure;
using Shouldly;
using Xunit.Abstractions;

namespace NexusErp.Tests.Performance;

/// <summary>
/// 100.000 fatura üzerinde yaşlandırma raporunu ölçer ve eksik index'in etkisini gösterir.
///
/// ⚠️ Normal test koşusunda ATLANIR (Skip). Ölçüm için:
///     dotnet test --filter Category=Performance
/// Ortalama koşuda 30–60 sn sürer; CI'ı yavaşlatmasın diye ayrı tutuldu.
///
/// Amaç "hızlı" demek değil, ÖLÇMEK: iddia edilen optimizasyon ile ölçülmüş
/// optimizasyon arasındaki fark mülakatta belirleyici oluyor.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class AgingReportBenchmark(DatabaseFixture fixture, ITestOutputHelper output)
{
    private const int PartyCount = 500;
    private const int InvoiceCount = 100_000;

    [Fact(Skip = "Performans ölçümü — elle çalıştır: dotnet test --filter FullyQualifiedName~AgingReportBenchmark")]
    [Trait("Category", "Performance")]
    public async Task Yaslandirma_raporu_100k_faturada_olculur()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = fixture.CreateContext(tenant);

        await SeedAsync(db, tenant);

        var service = new PartyBalanceService(fixture.CreateFactory(tenant));
        var asOf = DateOnly.FromDateTime(DateTime.Today);
        var samplePartyId = await db.Parties.Select(p => p.Id).FirstAsync();

        await db.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS ix_invoices_tenant_id_status_due_date;");
        await db.Database.ExecuteSqlRawAsync("ANALYZE invoices;");

        // ── SENARYO A: Yaşlandırma raporu (TÜM açık faturaları gruplar) ──
        var agingBefore = await MeasureAsync(() => service.GetAgingAsync(asOf));

        // ── SENARYO B: Tek carinin açık faturaları (SEÇİCİ sorgu) ──
        var partyBefore = await MeasureAsync(() => OpenInvoicesOfPartyAsync(db, samplePartyId));

        // Kapsayıcı index ekle
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX ix_invoices_party_open_covering
            ON invoices (tenant_id, party_id, status)
            INCLUDE (grand_total, paid_amount, due_date)
            WHERE is_deleted = false;
            """);
        await db.Database.ExecuteSqlRawAsync("ANALYZE invoices;");

        var agingAfter = await MeasureAsync(() => service.GetAgingAsync(asOf));
        var partyAfter = await MeasureAsync(() => OpenInvoicesOfPartyAsync(db, samplePartyId));

        output.WriteLine("");
        output.WriteLine($"  Fatura sayısı: {InvoiceCount:N0}  ·  Cari sayısı: {PartyCount:N0}");
        output.WriteLine("");
        output.WriteLine("  SENARYO A — Yaşlandırma raporu (tüm tabloyu gruplar)");
        output.WriteLine($"    index'siz : {agingBefore.Median,5:N0} ms");
        output.WriteLine($"    index'le  : {agingAfter.Median,5:N0} ms" +
                         $"   →  {Ratio(agingBefore.Median, agingAfter.Median)}");
        output.WriteLine("");
        output.WriteLine("  SENARYO B — Tek carinin açık faturaları (seçici sorgu)");
        output.WriteLine($"    index'siz : {partyBefore.Median,5:N0} ms");
        output.WriteLine($"    index'le  : {partyAfter.Median,5:N0} ms" +
                         $"   →  {Ratio(partyBefore.Median, partyAfter.Median)}");
        output.WriteLine("");
        output.WriteLine("""
              SONUÇ
              A: Yaşlandırma raporu TÜM açık faturaları gruplayan bir toplama sorgusu.
                 Seçici filtre olmadığı için PostgreSQL doğru şekilde Seq Scan seçiyor;
                 index eklemek okumayı hızlandırmaz, sadece yazma maliyetini artırır.
                 100.000 faturada ~80 ms kabul edilebilir. ~1M satırda materialized view
                 (gece yenilenen) gerekir.
              B: Tek cari sorgusu zaten EF'in FK için ürettiği ix_invoices_party_id'yi
                 kullanıyor ve 2 ms sürüyor. Ek index gereksiz.

              KARAR: kapsayıcı index EKLENMEDİ. Ölçüm hipotezi çürüttü —
              "her yavaş görünen sorguya index at" refleksinin neden yanlış olduğunun örneği.
              """);
        output.WriteLine("  --- Yaşlandırma planı (A) ---");
        output.WriteLine(await ExplainAsync(db, AgingSql));
        output.WriteLine("  --- Cari planı (B) ---");
        output.WriteLine(await ExplainAsync(db, PartySql(samplePartyId)));

        // Hız kadar DOĞRULUK da ölçülmeli
        var rows = await service.GetAgingAsync(asOf);
        rows.Count.ShouldBeGreaterThan(0);
        rows.Sum(r => r.Total).ShouldBeGreaterThan(0m);
    }

    private static string Ratio(long before, long after) =>
        after <= 0
            ? "ölçülemedi"
            : before / (double)after is var r && r >= 1.15
                ? $"{r:N1}x hızlandı"
                : r <= 0.87 ? $"{1 / r:N1}x YAVAŞLADI" : "anlamlı fark yok";

    /// <summary>Seçici sorgu: 100.000 faturanın içinden tek carinin açık olanları.</summary>
    private static Task<List<Guid>> OpenInvoicesOfPartyAsync(AppDbContext db, Guid partyId) =>
        db.Invoices
          .Where(i => i.PartyId == partyId
                   && (i.Status == InvoiceStatus.Issued
                    || i.Status == InvoiceStatus.PartiallyPaid))
          .OrderBy(i => i.DueDate)
          .Select(i => i.Id)
          .ToListAsync();

    private const string AgingSql = """
        SELECT party_id, party_title, SUM(grand_total - paid_amount)
        FROM invoices
        WHERE is_deleted = false AND status IN (1, 2) AND type <> 3
        GROUP BY party_id, party_title;
        """;

    private static string PartySql(Guid partyId) => $"""
        SELECT id FROM invoices
        WHERE is_deleted = false AND party_id = '{partyId}' AND status IN (1, 2)
        ORDER BY due_date;
        """;

    private static async Task<(long Median, long Min)> MeasureAsync<T>(Func<Task<T>> action)
    {
        await action();                       // ısınma (plan önbelleği, buffer cache)

        var samples = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            await action();
            sw.Stop();
            samples.Add(sw.ElapsedMilliseconds);
        }

        samples.Sort();
        return (samples[samples.Count / 2], samples[0]);
    }

    private static async Task<string> ExplainAsync(AppDbContext db, string sql)
    {
        // Üretilen planı görmek, "index kullanıldı mı?" sorusunun TEK doğru cevabı
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "EXPLAIN (ANALYZE, BUFFERS) " + sql;

        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            await cmd.Connection.OpenAsync();

        await using var reader = await cmd.ExecuteReaderAsync();
        var lines = new List<string> { "  EXPLAIN ANALYZE:" };
        while (await reader.ReadAsync())
            lines.Add("    " + reader.GetString(0));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Sayıyı SQL'e güvenle yazar — nokta ondalık ayırıcı, kültürden bağımsız.</summary>
    private static string Sql(decimal value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task SeedAsync(AppDbContext db, Guid tenant)
    {
        var taxRate = new TaxRate
        {
            TenantId = tenant, Code = "KDV20", Name = "KDV %20", Rate = 0.20m,
            ValidFrom = new DateOnly(2023, 7, 10), IsDefault = true
        };
        db.TaxRates.Add(taxRate);

        var partyFaker = new Faker<Party>("tr")
            .RuleFor(p => p.Id, _ => Guid.CreateVersion7())
            .RuleFor(p => p.TenantId, _ => tenant)
            .RuleFor(p => p.Code, f => $"MUS{f.IndexFaker + 1:D5}")
            .RuleFor(p => p.Title, f => f.Company.CompanyName())
            .RuleFor(p => p.Type, _ => PartyType.Customer)
            .RuleFor(p => p.PaymentTermDays, f => f.PickRandom(15, 30, 45, 60))
            .RuleFor(p => p.City, f => f.Address.City())
            .RuleFor(p => p.Currency, _ => "TRY");

        var parties = partyFaker.Generate(PartyCount);
        db.Parties.AddRange(parties);
        await db.SaveChangesAsync();

        // EF Core ile 100.000 satır tek tek eklemek dakikalar sürer.
        // Toplu üretim için ham SQL — testin kendisi hızlı olmalı.
        var today = DateOnly.FromDateTime(DateTime.Today);
        var random = new Random(42);              // tekrarlanabilir sonuç

        const int batchSize = 5_000;
        for (var offset = 0; offset < InvoiceCount; offset += batchSize)
        {
            var values = new List<string>(batchSize);

            for (var i = 0; i < batchSize && offset + i < InvoiceCount; i++)
            {
                var seq = offset + i + 1;
                var party = parties[random.Next(parties.Count)];
                var issue = today.AddDays(-random.Next(0, 400));
                var due = issue.AddDays(party.PaymentTermDays);
                var baseAmount = Math.Round(random.Next(500, 50_000) + 0.99m, 2);
                var tax = Math.Round(baseAmount * 0.20m, 2);
                var grand = baseAmount + tax;
                var paid = random.Next(0, 3) == 0 ? Math.Round(grand * 0.5m, 2) : 0m;
                var status = paid > 0 ? 2 : 1;

                // ⚠️ decimal'ler InvariantCulture ile yazılmak ZORUNDA.
                // Uygulama tr-TR kültürüyle çalıştığı için düz interpolasyon
                // "1234,99" üretir ve PostgreSQL virgülü DEĞER AYIRICI sanar:
                //   "INSERT has more expressions than target columns"
                // Kural: kullanıcı girdisi kültüre duyarlı, MAKİNE VERİSİ invariant.
                values.Add($"('{Guid.CreateVersion7()}','{tenant}','NEX2026{seq:D9}','NEX',2026,{seq}," +
                           $"'{Guid.NewGuid()}',1,{status},'{party.Id}','{party.Title.Replace("'", "''")}'," +
                           $"'{issue:yyyy-MM-dd}','{due:yyyy-MM-dd}','TRY',1,0,0," +
                           $"{Sql(baseAmount)},0,{Sql(baseAmount)},{Sql(tax)},0," +
                           $"{Sql(grand)},{Sql(paid)}," +
                           $"now(),'bench',false)");
            }

            // EF1002: interpolated string doğrudan SQL'e giriyor.
            // Burada BİLİNÇLİ bastırılıyor: değerlerin tamamı bu testin içinde
            // üretiliyor (Guid, sayı, sabit tarih), dış girdi yok. Uygulama kodunda
            // bu bastırma ASLA yapılmamalı.
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync($"""
                INSERT INTO invoices
                    (id, tenant_id, number, series, year, sequence, ettn, type, status,
                     party_id, party_title, issue_date, due_date, currency, exchange_rate,
                     document_discount_type, document_discount_value,
                     gross_total, discount_total, tax_base_total, tax_total,
                     withholding_total, grand_total, paid_amount,
                     created_at, created_by, is_deleted)
                VALUES {string.Join(",", values)};
                """);
#pragma warning restore EF1002
        }
    }
}
