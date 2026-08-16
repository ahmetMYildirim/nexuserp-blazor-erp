namespace NexusErp.Domain.Enums;

/// <summary>
/// Hesap türü. Tek Düzen Hesap Planı'nda ilk hane bunu belirler:
/// 1-2 aktif, 3-5 pasif, 6 gelir tablosu, 7 maliyet.
///
/// ⚠️ Bu enum yalnızca sınıflandırma değil, BAKİYE YÖNÜNÜ de belirler
/// (<see cref="AccountTypeExtensions.IsDebitBalanced"/>). Yön yanlış olursa
/// mizan tutar ama bilanço ters çıkar: varlık negatif, borç pozitif görünür.
/// </summary>
public enum AccountType
{
    Asset = 1,        // Varlık   → borç bakiyeli (aktif)
    Liability = 2,    // Yabancı kaynak → alacak bakiyeli (pasif)
    Equity = 3,       // Özkaynak → alacak bakiyeli (pasif)
    Revenue = 4,      // Gelir    → alacak bakiyeli
    Expense = 5       // Gider    → borç bakiyeli
}

public static class AccountTypeExtensions
{
    /// <summary>
    /// Hesabın doğal bakiyesi borç tarafında mı? Varlık ve gider hesapları
    /// borçla artar; kaynak, özkaynak ve gelir hesapları alacakla artar.
    /// Bakiye hesabı bu yöne göre işaretlenir.
    /// </summary>
    public static bool IsDebitBalanced(this AccountType type) =>
        type is AccountType.Asset or AccountType.Expense;

    /// <summary>Bilançoda mı yer alır, gelir tablosunda mı?</summary>
    public static bool IsBalanceSheet(this AccountType type) =>
        type is AccountType.Asset or AccountType.Liability or AccountType.Equity;

    public static string Text(this AccountType type) => type switch
    {
        AccountType.Asset => "Varlık",
        AccountType.Liability => "Yabancı Kaynak",
        AccountType.Equity => "Özkaynak",
        AccountType.Revenue => "Gelir",
        AccountType.Expense => "Gider",
        _ => "?"
    };
}
