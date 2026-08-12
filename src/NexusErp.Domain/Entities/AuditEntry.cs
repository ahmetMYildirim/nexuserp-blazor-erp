using NexusErp.Domain.Common;

namespace NexusErp.Domain.Entities;

public enum AuditAction { Insert = 1, Update = 2, Delete = 3 }

public sealed class AuditEntry : AuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    public string EntityName { get; set; } = default!;
    public string EntityId { get; set; } = default!;
    public AuditAction Action { get; set; }

    public string UserName { get; set; } = default!;
    public DateTimeOffset OccurredAt { get; set; }

    public string Changes { get; set; } = "{}";
}
