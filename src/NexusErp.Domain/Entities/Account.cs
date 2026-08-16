using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

namespace NexusErp.Domain.Entities;

/// <summary>
/// Hesap planı kalemi. Tek Düzen Hesap Planı hiyerarşiktir:
/// 1 (Dönen Varlıklar) → 12 (Ticari Alacaklar) → 120 (Alıcılar)
///
/// Hiyerarşi ParentId ile kuruluyor, koda gömülü DEĞİL: kullanıcı kendi alt
/// hesabını (120.01 gibi) açabilmeli. Kod alanı string çünkü "120.01" gibi
/// noktalı alt kırılımlar sayı değildir ve baştaki sıfırlar anlamlıdır.
/// </summary>
public sealed class Account : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public AccountType Type { get; set; }

    public Guid? ParentId { get; set; }
    public Account? Parent { get; set; }

    /// <summary>
    /// Hiyerarşideki derinlik: 1 = ana grup (1), 2 = grup (12), 3 = hesap (120).
    /// Raporlarda girinti ve toplama seviyesi için tutuluyor; her seferinde
    /// ağacı yürüyerek hesaplamak mizan sorgusunu gereksiz yere ağırlaştırır.
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Bu hesaba fiş satırı yazılabilir mi? YALNIZCA yaprak hesaplar
    /// (genelde 3 haneli ve altı) hareket görür.
    ///
    /// ⚠️ Ara toplam hesabına (örn. 12 Ticari Alacaklar) hareket yazılırsa
    /// mizan iki kez toplar: hem hareketin kendisi hem alt hesap toplamı.
    /// Bu yüzden kural veri tabanı seviyesinde değil, fiş kapatılırken
    /// domain tarafından zorlanıyor (<see cref="EnsurePostable"/>).
    /// </summary>
    public bool IsPostable { get; set; } = true;

    public bool IsActive { get; set; } = true;

    /// <summary>Sistem hesabı: otomatik fişler buna yazar, kullanıcı silemez.</summary>
    public bool IsSystem { get; set; }

    public string? Description { get; set; }

    public string DisplayName => $"{Code} — {Name}";

    /// <summary>Doğal bakiye yönü. Bakiye işareti bununla belirlenir.</summary>
    public bool IsDebitBalanced => Type.IsDebitBalanced();

    public void EnsurePostable()
    {
        if (!IsActive)
            throw new DomainException($"{DisplayName} hesabı pasif, fiş satırı yazılamaz.");

        if (!IsPostable)
            throw new DomainException(
                $"{DisplayName} bir üst grup hesabıdır, doğrudan hareket kaydedilemez. " +
                "Alt hesaplardan birini seçin.");
    }
}
