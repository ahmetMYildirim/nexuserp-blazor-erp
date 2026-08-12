using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Parties;
using NexusErp.Domain.Entities;

namespace NexusErp.Application.Auditing;

public sealed record AuditListItem(
    Guid Id, string EntityName, string EntityId, AuditAction Action,
    string UserName, DateTimeOffset OccurredAt, string Changes);

/// <summary>Bir alanın eski/yeni değeri — JSON'un okunabilir hâli.</summary>
public sealed record AuditChange(string Field, string? Before, string? After);

public sealed record AuditQuery(
    string? EntityName = null,
    string? UserName = null,
    AuditAction? Action = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int Page = 0,
    int PageSize = 25);

public sealed class AuditService(IAppDbContextFactory factory)
{
    public async Task<PagedResult<AuditListItem>> SearchAsync(
        AuditQuery q, CancellationToken ct = default)
    {
        await using var db = factory.Create();

        var query = db.AuditEntries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q.EntityName))
            query = query.Where(a => a.EntityName == q.EntityName);

        if (!string.IsNullOrWhiteSpace(q.UserName))
            query = query.Where(a => a.UserName == q.UserName);

        if (q.Action is not null)
            query = query.Where(a => a.Action == q.Action.Value);

        // DateOnly → gün sınırı. To dahil olmalı, bu yüzden ertesi günün başlangıcından KÜÇÜK.
        if (q.From is { } from)
        {
            var fromTs = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(a => a.OccurredAt >= fromTs);
        }

        if (q.To is { } to)
        {
            var toTs = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(a => a.OccurredAt < toTs);
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id)
            .Skip(q.Page * q.PageSize)
            .Take(q.PageSize)
            .Select(a => new AuditListItem(
                a.Id, a.EntityName, a.EntityId, a.Action,
                a.UserName, a.OccurredAt, a.Changes))
            .ToListAsync(ct);

        return new PagedResult<AuditListItem>(items, total);
    }

    /// <summary>Filtre dropdown'ı için — yalnızca gerçekten kaydı olan entity adları.</summary>
    public async Task<IReadOnlyList<string>> GetEntityNamesAsync(CancellationToken ct = default)
    {
        await using var db = factory.Create();
        return await db.AuditEntries.AsNoTracking()
            .Select(a => a.EntityName)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Ham JSON'u tabloya açar. Insert kaydında değer tek başına duruyor
    /// ({"Title":"..."}), Update kaydında {"eski":..., "yeni":...} nesnesi var —
    /// ikisini tek biçime indiriyoruz ki sayfa iki ayrı şablon taşımasın.
    /// </summary>
    public static IReadOnlyList<AuditChange> ParseChanges(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];

            var rows = new List<AuditChange>();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("eski", out var before)
                    && prop.Value.TryGetProperty("yeni", out var after))
                {
                    rows.Add(new AuditChange(prop.Name, Text(before), Text(after)));
                }
                else
                {
                    rows.Add(new AuditChange(prop.Name, null, Text(prop.Value)));
                }
            }

            return rows;
        }
        catch (JsonException)
        {
            // Denetim sayfası bozuk bir kayıt yüzünden komple çökmesin
            return [];
        }
    }

    private static string? Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        _ => e.ToString()
    };
}
