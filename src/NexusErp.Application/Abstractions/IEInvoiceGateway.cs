namespace NexusErp.Application.Abstractions;

public enum EInvoiceProfile
{
    /// <summary>Temel fatura — alıcı sistem üzerinden itiraz edemez.</summary>
    Temel,
    /// <summary>Ticari fatura — alıcı kabul/ret yanıtı gönderebilir.</summary>
    Ticari,
    /// <summary>e-Arşiv — alıcı e-Fatura mükellefi değilse.</summary>
    EArsiv
}

public sealed record EInvoiceDocument(
    Guid InvoiceId,
    string Number,
    Guid Ettn,
    EInvoiceProfile Profile,
    string Xml);

public sealed record EInvoiceSendResult(
    bool Success, string? TrackingId, string? Message);

/// <summary>UBL-TR 1.2 XML üretir.</summary>
public interface IUblInvoiceBuilder
{
    Task<EInvoiceDocument> BuildAsync(Guid invoiceId, CancellationToken ct = default);
}

/// <summary>
/// e-Fatura entegratörü (Uyumsoft, Sovos, Nes, İdea...).
///
/// ⚠️ Gerçek entegratör bağlantısı ticari sözleşme ve GİB test ortamı gerektiriyor.
/// Bu arayüz o sınırı çiziyor: XML üretimi ve iş akışı BİZDE, gönderim sağlayıcıda.
/// Demo'da MockEInvoiceGateway kullanılıyor — sözleşme imzalandığında yalnızca bu
/// arayüzün gerçek implementasyonu yazılır, uygulamanın geri kalanı değişmez.
/// </summary>
public interface IEInvoiceGateway
{
    Task<EInvoiceSendResult> SendAsync(EInvoiceDocument document, CancellationToken ct = default);
}
