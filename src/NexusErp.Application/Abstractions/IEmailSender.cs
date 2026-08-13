namespace NexusErp.Application.Abstractions;

/// <summary>
/// E-posta gönderimi. Application katmanı SMTP'yi bilmez; gerçeklemesi
/// Infrastructure'da (geliştirmede MailHog, üretimde gerçek sunucu).
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}
