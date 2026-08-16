using NexusErp.Domain.Enums;

namespace NexusErp.Infrastructure.Persistence.Seed;

/// <summary>
/// Tek Düzen Hesap Planı tohum şablonu.
///
/// Hesap planı KOD DEĞİL VERİDİR: burada tek bir listede duruyor, iş mantığının
/// içine dağılmış değil. Yeni tenant açıldığında bu liste kopyalanıyor ve
/// işletme kendi alt hesaplarını (120.01 gibi) üzerine ekleyebiliyor.
///
/// Kapsam bilinçli olarak sınırlı: ön muhasebe + abonelik faturalandırması bir
/// KOBİ'nin fiilen kullandığı hesaplar. Tam TDHP 700'ün üzerinde hesap içerir;
/// tamamını tohumlamak kullanılmayan yüzlerce satırla hesap planı ekranını
/// okunamaz hale getirir. Eksik hesap kullanıcı tarafından eklenebilir.
/// </summary>
public static class ChartOfAccountsTemplate
{
    /// <param name="Code">TDHP kodu — hiyerarşi bu kodun uzunluğundan türetilir.</param>
    /// <param name="IsSystem">Otomatik fişlerin kullandığı hesap; silinemez.</param>
    public sealed record Item(
        string Code, string Name, AccountType Type, bool IsSystem = false);

    /// <summary>
    /// ⚠️ Sıra ÖNEMLİ: üst hesap kendi alt hesabından önce gelmeli, yoksa
    /// ParentId çözümlenemez. Kod uzunluğuna göre artan sırada duruyor.
    /// </summary>
    public static IReadOnlyList<Item> Items { get; } =
    [
        // ============================ 1 — DÖNEN VARLIKLAR ============================
        new("1", "Dönen Varlıklar", AccountType.Asset),
        new("10", "Hazır Değerler", AccountType.Asset),
        new("100", "Kasa", AccountType.Asset, IsSystem: true),
        new("101", "Alınan Çekler", AccountType.Asset),
        new("102", "Bankalar", AccountType.Asset, IsSystem: true),
        new("103", "Verilen Çekler ve Ödeme Emirleri (-)", AccountType.Asset),
        new("108", "Diğer Hazır Değerler", AccountType.Asset),

        new("12", "Ticari Alacaklar", AccountType.Asset),
        new("120", "Alıcılar", AccountType.Asset, IsSystem: true),
        new("121", "Alacak Senetleri", AccountType.Asset),
        new("126", "Verilen Depozito ve Teminatlar", AccountType.Asset),
        new("128", "Şüpheli Ticari Alacaklar", AccountType.Asset),
        new("129", "Şüpheli Ticari Alacaklar Karşılığı (-)", AccountType.Asset),

        new("13", "Diğer Alacaklar", AccountType.Asset),
        new("136", "Diğer Çeşitli Alacaklar", AccountType.Asset),

        new("15", "Stoklar", AccountType.Asset),
        new("153", "Ticari Mallar", AccountType.Asset, IsSystem: true),
        new("157", "Diğer Stoklar", AccountType.Asset),

        new("18", "Gelecek Aylara Ait Giderler ve Gelir Tahakkukları", AccountType.Asset),
        new("180", "Gelecek Aylara Ait Giderler", AccountType.Asset),
        new("181", "Gelir Tahakkukları", AccountType.Asset),

        new("19", "Diğer Dönen Varlıklar", AccountType.Asset),
        new("191", "İndirilecek KDV", AccountType.Asset, IsSystem: true),
        new("193", "Peşin Ödenen Vergiler ve Fonlar", AccountType.Asset),
        new("196", "Personel Avansları", AccountType.Asset),

        // ========================== 2 — DURAN VARLIKLAR ==============================
        new("2", "Duran Varlıklar", AccountType.Asset),
        new("25", "Maddi Duran Varlıklar", AccountType.Asset),
        new("253", "Tesis, Makine ve Cihazlar", AccountType.Asset),
        new("255", "Demirbaşlar", AccountType.Asset),
        new("257", "Birikmiş Amortismanlar (-)", AccountType.Asset),

        new("26", "Maddi Olmayan Duran Varlıklar", AccountType.Asset),
        new("260", "Haklar", AccountType.Asset),
        new("268", "Birikmiş Amortismanlar (-)", AccountType.Asset),

        // ====================== 3 — KISA VADELİ YABANCI KAYNAKLAR ====================
        new("3", "Kısa Vadeli Yabancı Kaynaklar", AccountType.Liability),
        new("30", "Mali Borçlar", AccountType.Liability),
        new("300", "Banka Kredileri", AccountType.Liability),

        new("32", "Ticari Borçlar", AccountType.Liability),
        new("320", "Satıcılar", AccountType.Liability, IsSystem: true),
        new("321", "Borç Senetleri", AccountType.Liability),
        new("326", "Alınan Depozito ve Teminatlar", AccountType.Liability),

        new("33", "Diğer Borçlar", AccountType.Liability),
        new("335", "Personele Borçlar", AccountType.Liability),
        new("336", "Diğer Çeşitli Borçlar", AccountType.Liability),

        new("34", "Alınan Avanslar", AccountType.Liability),
        new("340", "Alınan Sipariş Avansları", AccountType.Liability),

        new("36", "Ödenecek Vergi ve Diğer Yükümlülükler", AccountType.Liability),
        new("360", "Ödenecek Vergi ve Fonlar", AccountType.Liability),
        new("361", "Ödenecek Sosyal Güvenlik Kesintileri", AccountType.Liability),

        new("38", "Gelecek Aylara Ait Gelirler ve Gider Tahakkukları", AccountType.Liability),
        new("380", "Gelecek Aylara Ait Gelirler", AccountType.Liability),
        new("381", "Gider Tahakkukları", AccountType.Liability),

        new("39", "Diğer Kısa Vadeli Yabancı Kaynaklar", AccountType.Liability),
        new("391", "Hesaplanan KDV", AccountType.Liability, IsSystem: true),
        new("397", "Sayım ve Tesellüm Fazlaları", AccountType.Liability),

        // ====================== 4 — UZUN VADELİ YABANCI KAYNAKLAR ====================
        new("4", "Uzun Vadeli Yabancı Kaynaklar", AccountType.Liability),
        new("40", "Mali Borçlar", AccountType.Liability),
        new("400", "Banka Kredileri", AccountType.Liability),

        // ============================== 5 — ÖZKAYNAKLAR =============================
        new("5", "Özkaynaklar", AccountType.Equity),
        new("50", "Ödenmiş Sermaye", AccountType.Equity),
        new("500", "Sermaye", AccountType.Equity),

        new("54", "Kâr Yedekleri", AccountType.Equity),
        new("540", "Yasal Yedekler", AccountType.Equity),

        new("57", "Geçmiş Yıllar Kârları", AccountType.Equity),
        new("570", "Geçmiş Yıllar Kârları", AccountType.Equity),
        new("580", "Geçmiş Yıllar Zararları (-)", AccountType.Equity),

        new("59", "Dönem Net Kârı (Zararı)", AccountType.Equity),
        new("590", "Dönem Net Kârı", AccountType.Equity),
        new("591", "Dönem Net Zararı (-)", AccountType.Equity),

        // ========================= 6 — GELİR TABLOSU HESAPLARI =======================
        new("6", "Gelir Tablosu Hesapları", AccountType.Revenue),
        new("60", "Brüt Satışlar", AccountType.Revenue),
        new("600", "Yurtiçi Satışlar", AccountType.Revenue, IsSystem: true),
        new("601", "Yurtdışı Satışlar", AccountType.Revenue),
        new("602", "Diğer Gelirler", AccountType.Revenue),

        new("61", "Satış İndirimleri (-)", AccountType.Revenue),
        new("610", "Satıştan İadeler (-)", AccountType.Revenue, IsSystem: true),
        new("611", "Satış İskontoları (-)", AccountType.Revenue),

        new("62", "Satışların Maliyeti (-)", AccountType.Expense),
        new("621", "Satılan Ticari Mallar Maliyeti (-)", AccountType.Expense),

        new("63", "Faaliyet Giderleri (-)", AccountType.Expense),
        new("630", "Araştırma ve Geliştirme Giderleri (-)", AccountType.Expense),
        new("631", "Pazarlama, Satış ve Dağıtım Giderleri (-)", AccountType.Expense),
        new("632", "Genel Yönetim Giderleri (-)", AccountType.Expense),

        new("64", "Diğer Faaliyetlerden Olağan Gelir ve Kârlar", AccountType.Revenue),
        new("642", "Faiz Gelirleri", AccountType.Revenue),
        new("646", "Kambiyo Kârları", AccountType.Revenue),

        new("65", "Diğer Faaliyetlerden Olağan Gider ve Zararlar (-)", AccountType.Expense),
        new("656", "Kambiyo Zararları (-)", AccountType.Expense),

        new("66", "Finansman Giderleri (-)", AccountType.Expense),
        new("660", "Kısa Vadeli Borçlanma Giderleri (-)", AccountType.Expense),

        // =========================== 7 — MALİYET HESAPLARI ==========================
        new("7", "Maliyet Hesapları", AccountType.Expense),
        new("77", "Genel Yönetim Giderleri", AccountType.Expense),
        new("770", "Genel Yönetim Giderleri", AccountType.Expense, IsSystem: true)
    ];
}
