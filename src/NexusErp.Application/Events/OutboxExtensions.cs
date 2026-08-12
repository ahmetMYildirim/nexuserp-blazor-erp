using System.Text.Encodings.Web;
using System.Text.Json;
using NexusErp.Application.Abstractions;
using NexusErp.Domain.Entities;

namespace NexusErp.Application.Events;

public static class OutboxExtensions
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void AddEvent<T>(this IAppDbContext db, T payload, DateTimeOffset now) where T : notnull
    {
        db.OutboxMessages.Add(new OutBoxMessage
        {
            Type = typeof(T).Name,
            Payload = JsonSerializer.Serialize(payload, Options),
            OccuredAt = now
        });
    }
}
