namespace NexusErp.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>
    /// ⚠️ SONDA EĞİK ÇİZGİ OLMAMALI. AMQP URI'sinde ".../" vhost adının BOŞ DİZE
    /// olduğu anlamına gelir; varsayılan vhost ise "/" karakteridir ve URI'de
    /// "%2F" diye kodlanması gerekir. Sondaki çizgiyle bağlanmaya çalışırsan
    /// broker "ACCESS_REFUSED - Login was refused" der ve saatlerce parolayı
    /// kontrol edersin. En temizi vhost'u hiç yazmamak.
    /// </summary>
    public string Uri { get; set; } = "amqp://nexus:nexus_dev_2026@localhost:5673";

    /// <summary>Topic exchange — tüketiciler tek olayı ya da hepsini (nexuserp.*) dinleyebilsin.</summary>
    public string Exchange { get; set; } = "nexuserp.events";

    /// <summary>Ölü mektup exchange'i: 5 kez işlenemeyen mesaj buraya düşer.</summary>
    public string DeadLetterExchange { get; set; } = "nexuserp.events.dlx";

    /// <summary>Broker kapalıysa uygulama yine de açılsın; işçi tekrar dener.</summary>
    public bool Enabled { get; set; } = true;
}
