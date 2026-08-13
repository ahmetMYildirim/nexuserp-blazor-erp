namespace NexusErp.Domain.Enums;

/// <summary>
/// Abonelik iptal sebebi. Churn oranını hesaplamak kolay; asıl değerli olan
/// NEDEN kaybedildiğini bilmek — fiyat sorunu ile ürün sorunu farklı aksiyon ister.
/// </summary>
public enum CancellationReason
{
    /// <summary>Sebep sorulmadı / eski kayıt.</summary>
    Unspecified = 0,

    /// <summary>Fiyat yüksek geldi.</summary>
    TooExpensive = 1,

    /// <summary>Kullanmıyor, ihtiyaç kalmadı.</summary>
    NotUsing = 2,

    /// <summary>Rakip ürüne geçti.</summary>
    SwitchedToCompetitor = 3,

    /// <summary>Eksik özellik / beklentiyi karşılamadı.</summary>
    MissingFeatures = 4,

    /// <summary>Geçici ara — geri dönmeyi düşünüyor.</summary>
    TemporaryPause = 5,

    /// <summary>Müşteri kapandı / faaliyeti bitti.</summary>
    BusinessClosed = 6,

    /// <summary>Ödeme alınamadı (dunning sonucu).</summary>
    PaymentFailure = 7,

    Other = 99
}
