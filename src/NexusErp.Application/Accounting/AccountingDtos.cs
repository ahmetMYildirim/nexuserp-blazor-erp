using NexusErp.Domain.Enums;

namespace NexusErp.Application.Accounting;

// ----------------------------------------------------------------- hesap planı

public sealed record AccountListItem(
    Guid Id, string Code, string Name, AccountType Type,
    int Level, bool IsPostable, bool IsActive, bool IsSystem);

public sealed record AccountOption(Guid Id, string Code, string Name)
{
    public string Display => $"{Code} — {Name}";
}

public sealed class AccountForm
{
    public Guid? Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AccountType Type { get; set; } = AccountType.Asset;
    public string? Description { get; set; }
}

// ------------------------------------------------------------------- fiş

public sealed record JournalEntryListItem(
    Guid Id, string? Number, DateOnly EntryDate, string Description,
    JournalSourceType SourceType, string? SourceDocumentNumber,
    decimal DebitTotal, decimal CreditTotal, bool IsPosted);

public sealed class JournalLineForm
{
    public Guid? AccountId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string? Description { get; set; }
}

public sealed class JournalEntryForm
{
    public Guid? Id { get; set; }
    public DateOnly EntryDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string Description { get; set; } = string.Empty;
    public List<JournalLineForm> Lines { get; set; } = [];

    public decimal DebitTotal => Lines.Sum(l => l.Debit);
    public decimal CreditTotal => Lines.Sum(l => l.Credit);
    public decimal Difference => DebitTotal - CreditTotal;
    public bool IsBalanced => Difference == 0 && DebitTotal > 0;
}

public sealed record JournalEntryDetail(
    Guid Id, string? Number, DateOnly EntryDate, string Description,
    JournalSourceType SourceType, string SourceText, string? SourceDocumentNumber,
    bool IsPosted, decimal DebitTotal, decimal CreditTotal,
    IReadOnlyList<JournalLineDetail> Lines);

public sealed record JournalLineDetail(
    int LineNumber, string AccountCode, string AccountName,
    decimal Debit, decimal Credit, string? Description);

// -------------------------------------------------------------- mali tablolar

/// <summary>Mizan satırı: hesabın dönem içi borç/alacak toplamı ve bakiyesi.</summary>
public sealed record TrialBalanceRow(
    string Code, string Name, AccountType Type,
    decimal Debit, decimal Credit)
{
    /// <summary>
    /// Bakiye borç tarafındaysa pozitif. Hesabın doğal yönü ne olursa olsun
    /// mizan ham borç−alacak farkını gösterir; yorumlama bilançoda yapılır.
    /// </summary>
    public decimal Balance => Debit - Credit;

    public decimal DebitBalance => Balance > 0 ? Balance : 0m;
    public decimal CreditBalance => Balance < 0 ? -Balance : 0m;
}

public sealed record TrialBalance(
    DateOnly From, DateOnly To, IReadOnlyList<TrialBalanceRow> Rows)
{
    public decimal TotalDebit => Rows.Sum(r => r.Debit);
    public decimal TotalCredit => Rows.Sum(r => r.Credit);
    public decimal TotalDebitBalance => Rows.Sum(r => r.DebitBalance);
    public decimal TotalCreditBalance => Rows.Sum(r => r.CreditBalance);

    /// <summary>Mizanın olmazsa olmazı: borç toplamı = alacak toplamı.</summary>
    public bool IsBalanced => TotalDebit == TotalCredit;
}

public sealed record StatementLine(string Code, string Name, decimal Amount);

public sealed record StatementGroup(
    string Title, IReadOnlyList<StatementLine> Lines)
{
    public decimal Total => Lines.Sum(l => l.Amount);
}

public sealed record BalanceSheet(
    DateOnly AsOf,
    StatementGroup Assets,
    StatementGroup Liabilities,
    StatementGroup Equity,
    decimal PeriodResult)
{
    public decimal TotalAssets => Assets.Total;

    /// <summary>
    /// Pasif toplam = yabancı kaynak + özkaynak + DÖNEM SONUCU.
    ///
    /// ⚠️ Dönem kâr/zararı ayrı gösteriliyor çünkü gelir ve gider hesapları
    /// (6xx/7xx) dönem içinde kapatılmadan bilançoya girmez; kapanış yapılmamış
    /// bir dönemde aktif = pasif eşitliği ancak dönem sonucu pasife eklenirse
    /// sağlanır. Aksi halde tablo "denk değil" görünür ve kullanıcı olmayan bir
    /// hata arar.
    /// </summary>
    public decimal TotalLiabilitiesAndEquity =>
        Liabilities.Total + Equity.Total + PeriodResult;

    public decimal Difference => TotalAssets - TotalLiabilitiesAndEquity;
    public bool IsBalanced => Difference == 0m;
}

public sealed record IncomeStatement(
    DateOnly From, DateOnly To,
    StatementGroup Revenues,
    StatementGroup Expenses)
{
    public decimal TotalRevenue => Revenues.Total;
    public decimal TotalExpense => Expenses.Total;
    public decimal NetResult => TotalRevenue - TotalExpense;
    public bool IsProfit => NetResult >= 0;
}

/// <summary>
/// Bir ayın gelir / gider / sonuç üçlüsü. Grafik için: yıllık toplam tek rakamdır,
/// asıl bilgi hangi ayın kâr hangi ayın zarar ettiğindedir.
/// </summary>
public sealed record MonthlyResult(int Year, int Month, decimal Revenue, decimal Expense)
{
    public decimal Result => Revenue - Expense;
    public bool IsProfit => Result >= 0;

    /// <summary>Grafik ekseni için kısa ay adı: Oca, Şub…</summary>
    public string Label =>
        new DateOnly(Year, Month, 1).ToString("MMM",
            System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
}
