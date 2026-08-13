using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexusErp.Application.Abstractions;

namespace NexusErp.Infrastructure.Messaging;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = "localhost";

    /// <summary>Geliştirmede MailHog: 1025. Üretimde genelde 587.</summary>
    public int Port { get; set; } = 1025;

    public bool UseSsl { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }

    public string FromAddress { get; set; } = "bildirim@nexuserp.local";
    public string FromName { get; set; } = "NexusERP";

    /// <summary>
    /// Kapalıysa e-posta gönderilmez, yalnızca loglanır. Broker ya da SMTP
    /// yokken uygulamanın ayakta kalmasını sağlar.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// ⚠️ System.Net.Mail.SmtpClient .NET'te "obsolete for new development" olarak
/// işaretli; modern TLS/OAuth senaryolarında MailKit önerilir. Burada bilinçli
/// tercih: demo MailHog'a düz SMTP ile bağlanıyor, ek paket gerekmiyor.
/// Gerçek bir sağlayıcıya (SendGrid, Amazon SES, Office365) geçerken yalnızca
/// bu sınıf değişir — arayüz aynı kalır.
/// </summary>
public sealed class SmtpEmailSender(
    IOptions<SmtpOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body,
                                CancellationToken ct = default)
    {
        var o = options.Value;

        if (!o.Enabled)
        {
            logger.LogInformation("SMTP kapalı — e-posta gönderilmedi: {To} / {Subject}",
                                  to, subject);
            return;
        }

        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.UseSsl };

        if (!string.IsNullOrWhiteSpace(o.UserName))
            client.Credentials = new System.Net.NetworkCredential(o.UserName, o.Password);

        using var message = new MailMessage
        {
            From = new MailAddress(o.FromAddress, o.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };
        message.To.Add(to);

        await client.SendMailAsync(message, ct);
        logger.LogInformation("E-posta gönderildi: {To} / {Subject}", to, subject);
    }
}
