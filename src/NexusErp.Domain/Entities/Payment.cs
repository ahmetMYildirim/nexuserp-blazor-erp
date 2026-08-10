using NexusErp.Domain.Common;
using NexusErp.Domain.Enums;

namespace NexusErp.Domain.Entities;

public sealed class Payment : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    public string? Number { get; set; }              // THS2026000000001
    public Guid PartyId { get; set; }
    public Party Party { get; set; } = default!;

    public DateOnly PaymentDate { get; set; }
    public PaymentMethod Method { get; set; } = PaymentMethod.BankTransfer;

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "TRY";

    /// <summary>Faturalara dağıtılmış kısım. Amount − Allocated = avans.</summary>
    public decimal AllocatedAmount { get; set; }

    public string? Reference { get; set; }           // dekont no, çek no
    public string? Notes { get; set; }
    public bool IsCancelled { get; set; }

    public List<PaymentAllocation> Allocations { get; set; } = [];

    public decimal UnallocatedAmount => Amount - AllocatedAmount;

    public void EnsureCanAllocate(decimal amount)
    {
        if (IsCancelled)
            throw new DomainException("İptal edilmiş tahsilat eşleştirilemez.");
        if (amount <= 0)
            throw new DomainException("Eşleştirme tutarı sıfırdan büyük olmalıdır.");
        if (amount > UnallocatedAmount)
            throw new DomainException(
                $"Eşleştirilecek tutar, tahsilatın kalan bakiyesini ({UnallocatedAmount:N2}) aşamaz.");
    }

    public string MethodText => Method switch
    {
        PaymentMethod.Cash => "Nakit",
        PaymentMethod.BankTransfer => "Havale/EFT",
        PaymentMethod.CreditCard => "Kredi Kartı",
        PaymentMethod.Cheque => "Çek",
        PaymentMethod.PromissoryNote => "Senet",
        _ => "Diğer"
    };
}
