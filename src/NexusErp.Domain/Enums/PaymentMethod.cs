namespace NexusErp.Domain.Enums;

public enum PaymentMethod
{
    Cash = 1,           // nakit
    BankTransfer = 2,   // havale / EFT
    CreditCard = 3,
    Cheque = 4,         // çek
    PromissoryNote = 5, // senet
    Other = 9
}
