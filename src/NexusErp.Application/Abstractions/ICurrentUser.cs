namespace NexusErp.Application.Abstractions;

/// <summary>created_by / updated_by kolonlarını dolduran kaynak.</summary>
public interface ICurrentUser
{
    string UserName { get; }
}
