using NexusErp.Domain.Enums;

namespace NexusErp.Application.Parties;

/// <summary>Liste satırı — record: değişmez, değer eşitliği.</summary>
public sealed record PartyListItem(
    Guid Id,
    string Code,
    string Title,
    PartyType Type,
    string? TaxNumber,
    string? City,
    string? Phone,
    int PaymentTermDays,
    bool IsActive);

/// <summary>
/// Oluşturma ve güncelleme aynı şekli kullanır; Id null ise yeni kayıt.
/// class çünkü Blazor'un @bind-Value üzerine yazacağı mutable nesne olmak zorunda.
/// </summary>
public sealed class PartyForm
{
    public Guid? Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public PartyType Type { get; set; } = PartyType.Customer;
    public string? TaxNumber { get; set; }
    public string? TaxOffice { get; set; }
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? District { get; set; }
    public string? City { get; set; }
    public int PaymentTermDays { get; set; } = 30;
    public decimal CreditLimit { get; set; }
    public string Currency { get; set; } = "TRY";
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

/// <summary>Fatura başlığındaki cari seçici için hafif model.</summary>
public sealed record PartyLookupItem(
    Guid Id, string Code, string Title, int PaymentTermDays, string Currency)
{
    public override string ToString() => $"{Code} — {Title}";
}

/// <summary>Sunucu tarafı sayfalama sonucu.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

public sealed record PartyQuery(
    string? Search = null,
    PartyType? Type = null,
    bool? IsActive = true,
    int Page = 0,
    int PageSize = 25,
    string SortBy = nameof(PartyListItem.Code),
    bool Descending = false);
