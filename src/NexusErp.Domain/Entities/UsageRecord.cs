using NexusErp.Domain.Common;

namespace NexusErp.Domain.Entities;

/// <summary>
/// Tek bir kullanım olayı (100 SMS, 4,5 GB trafik, 3 API çağrısı…).
///
/// ⚠️ TOPLAM değil OLAY saklanıyor. Aboneliğin üzerinde "bu ay 4.312 SMS" diye
/// bir sayaç tutsaydık: (1) faturayı denetleyemezdik — müşteri "bu 4.312 nereden
/// çıktı" dediğinde gösterecek bir şey olmazdı, (2) geç gelen kayıtları ayırt
/// edemezdik, (3) sayaç sıfırlama anı ile fatura kesme anı arasındaki her hata
/// veriyi geri döndürülemez biçimde bozardı. Olay kaydı büyür ama doğrudur.
/// </summary>
public sealed class UsageRecord : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    public Guid SubscriptionId { get; set; }
    public Subscription Subscription { get; set; } = default!;

    /// <summary>Kullanımın GERÇEKLEŞTİĞİ gün — kaydedildiği gün değil.</summary>
    public DateOnly OccurredOn { get; set; }

    public decimal Quantity { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Dış sistemin kendi kayıt kimliği. Entegrasyon aynı çağrıyı tekrarlarsa
    /// (ağ hatası, yeniden deneme) ikinci kayıt (tenant, subscription, external_id)
    /// unique index'ine takılır. Kullanımın iki kez sayılması müşteriye fazladan
    /// fatura çıkarır — parasal sonucu olan bir hatadır.
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>Faturalandığında damgalanır. Null = henüz faturalanmadı.</summary>
    public Guid? InvoiceId { get; set; }

    public bool IsBilled => InvoiceId is not null;

    /// <summary>
    /// Faturalanmış kullanım DEĞİŞTİRİLEMEZ — fatura tutarı ona dayanıyor.
    /// Düzeltme gerekiyorsa ters işaretli yeni bir kayıt girilir (storno).
    /// </summary>
    public void EnsureEditable()
    {
        if (IsBilled)
            throw new DomainException(
                "Faturalanmış kullanım kaydı değiştirilemez. Düzeltme için ters " +
                "kayıt girin.");
    }

    public void MarkBilled(Guid invoiceId)
    {
        if (IsBilled)
            throw new DomainException("Kullanım kaydı zaten faturalanmış.");

        InvoiceId = invoiceId;
    }
}
