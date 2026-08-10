using NexusErp.Domain.Enums;

namespace NexusErp.Application.Payments;

public sealed record PaymentListItem(
    Guid Id, string? Number, string PartyTitle, DateOnly PaymentDate,
    PaymentMethod Method, decimal Amount, decimal AllocatedAmount,
    string Currency, string? Reference, bool IsCancelled)
{
    public decimal UnallocatedAmount => Amount - AllocatedAmount;
}

public sealed record ManualAllocation(Guid InvoiceId, decimal Amount);

public sealed class PaymentForm
{
    public Guid PartyId { get; set; }
    public DateOnly PaymentDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public PaymentMethod Method { get; set; } = PaymentMethod.BankTransfer;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "TRY";
    public string? Reference { get; set; }
    public string? Notes { get; set; }

    /// <summary>true → açık faturalara vade sırasına göre otomatik dağıt (FIFO).</summary>
    public bool AutoAllocate { get; set; } = true;
    public List<ManualAllocation> Allocations { get; set; } = [];
}

/// <summary>Cari ekstre satırı — yürüyen bakiyeli.</summary>
public sealed record StatementRow(
    DateOnly Date, string Description, string? DocumentNumber,
    decimal Debit, decimal Credit, decimal Balance);

/// <summary>Yaşlandırma raporu satırı.</summary>
public sealed record AgingRow(
    Guid PartyId, string PartyTitle,
    decimal NotDue, decimal Days1To30, decimal Days31To60,
    decimal Days61To90, decimal Over90, decimal Total);
