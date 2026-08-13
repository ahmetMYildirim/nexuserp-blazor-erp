namespace NexusErp.Application.Events
{
    public sealed record InvoiceIssued(Guid InvoiceId, string Number, Guid PartyId, string PartyTitle, decimal GrandTotal, string Currency, DateOnly IssueDate);
    public sealed record PaymentReceived(Guid PaymentId, string Number, Guid PartyId, decimal Amount, string Currency, DateOnly PaymentDate);
    public sealed record SubscriptionCancelled(Guid SubscriptionId, Guid PartyId, DateOnly CancelledOn, bool Immediately);

    /// <summary>Aboneliğin ödenmemiş vadesi geçmiş faturası tespit edildi.</summary>
    public sealed record SubscriptionPastDue(
        Guid SubscriptionId, Guid PartyId, string PartyTitle,
        decimal OverdueAmount, string Currency, DateOnly Since);

    /// <summary>
    /// Ödeme hatırlatması. Level 1/2/3 sırasıyla 3, 7 ve 14 gün gecikmeye karşılık gelir.
    /// Tüketici e-posta/SMS gönderir; tonu seviyeye göre sertleşir.
    /// </summary>
    public sealed record SubscriptionPaymentReminder(
        Guid SubscriptionId, Guid PartyId, string PartyTitle,
        int Level, int DaysPastDue, decimal OverdueAmount, string Currency);

    /// <summary>Ödeme gelmediği için hizmet durduruldu. İptal değil — borç kapanınca açılır.</summary>
    public sealed record SubscriptionSuspended(
        Guid SubscriptionId, Guid PartyId, string PartyTitle,
        int DaysPastDue, decimal OverdueAmount, string Currency, DateOnly SuspendedOn);

    /// <summary>Borç kapandı, abonelik normale döndü.</summary>
    public sealed record SubscriptionRecovered(
        Guid SubscriptionId, Guid PartyId, string PartyTitle, DateOnly RecoveredOn);
}
