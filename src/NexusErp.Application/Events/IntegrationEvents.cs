namespace NexusErp.Application.Events
{
    public sealed record InvoiceIssued(Guid InvoiceId, string Number, Guid PartyId, string PartyTitle, decimal GrandTotal, string Currency, DateOnly IssueDate);
    public sealed record PaymentReceived(Guid PaymentId, string Number, Guid PartyId, decimal Amount, string Currency, DateOnly PaymentDate);
    public sealed record SubscriptionCancelled(Guid SubscriptionId, Guid PartyId, DateOnly CancelledOn, bool Immediately);
}
