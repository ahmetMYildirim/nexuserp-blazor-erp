namespace NexusErp.Domain.Accounting;

/// <summary>
/// Otomatik fiş üretiminin kullandığı Tek Düzen Hesap Planı kodları.
///
/// Hesap planının TAMAMI tohum verisiyle geliyor (bkz. ChartOfAccountsTemplate);
/// burada yalnızca otomatik kaydın hangi hesaba yazacağı sabitlenmiş. Kodlar
/// servis içine dağıtılmış string'ler olarak değil tek yerde duruyor: bir
/// işletme 600 yerine 601'e yazmak isterse değiştirilecek tek nokta burası.
///
/// ⚠️ Bu kodlar tohum verisindeki hesaplarla birebir aynı olmak zorunda.
/// Eşleşmezse otomatik fiş "hesap bulunamadı" ile patlar — bunu sistem testi
/// yakalıyor (Muhasebe kategorisi).
/// </summary>
public static class TdhpAccounts
{
    // --- Aktif ---
    public const string Kasa = "100";                    // Kasa
    public const string Bankalar = "102";                // Bankalar
    public const string Alicilar = "120";                // Alıcılar (müşteri cari)
    public const string IndirilecekKdv = "191";          // İndirilecek KDV
    public const string TicariMallar = "153";            // Ticari Mallar

    // --- Pasif ---
    public const string Saticilar = "320";               // Satıcılar (tedarikçi cari)
    public const string HesaplananKdv = "391";           // Hesaplanan KDV

    // --- Gelir ---
    public const string YurtIciSatislar = "600";         // Yurtiçi Satışlar
    public const string SatistanIadeler = "610";         // Satıştan İadeler (-)

    // --- Gider ---
    public const string GenelYonetimGideri = "770";      // Genel Yönetim Giderleri
}
