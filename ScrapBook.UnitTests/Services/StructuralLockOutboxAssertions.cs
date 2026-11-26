using System.Text.Json;
using ScrapBook.Domain.Entities;

namespace ScrapBook.UnitTests.Services;

internal static class StructuralLockOutboxAssertions
{
    public static void Applied(StructuralLockOutboxEntity result, OutboxMessageEntity origin, DateTimeOffset lockedAt)
    {
        AssertMetadata(result, origin, lockedAt, "event.structural-lock.applied.v1");
        using var document = JsonDocument.Parse(result.Payload);
        var payload = document.RootElement;
        Assert.Equal(3, payload.EnumerateObject().Count());
        AssertIdentity(payload, origin);
        AssertInstant(payload, "lockedAt", lockedAt);
    }

    public static void Failed(StructuralLockOutboxEntity result, OutboxMessageEntity origin, DateTimeOffset failedAt,
        string stage, string code)
    {
        AssertMetadata(result, origin, failedAt, "event.structural-lock.failed.v1");
        using var document = JsonDocument.Parse(result.Payload);
        var payload = document.RootElement;
        Assert.Equal(5, payload.EnumerateObject().Count());
        AssertIdentity(payload, origin);
        AssertInstant(payload, "failedAt", failedAt);
        Assert.Equal(stage, payload.GetProperty("stage").GetString());
        Assert.Equal(code, payload.GetProperty("code").GetString());
    }

    private static void AssertMetadata(StructuralLockOutboxEntity result, OutboxMessageEntity origin, DateTimeOffset time, string type)
    {
        Assert.NotEqual(Guid.Empty, result.MessageId);
        Assert.NotEqual(origin.MessageId, result.MessageId);
        Assert.Equal(type, result.Type);
        Assert.Equal(origin.MessageId, result.CausationId);
        Assert.Equal(origin.CorrelationId == Guid.Empty ? origin.MessageId : origin.CorrelationId, result.CorrelationId);
        Assert.Equal(origin.EventId, result.EventId);
        Assert.Equal(time, result.OccurredAt);
        Assert.Equal(TimeSpan.Zero, result.OccurredAt.Offset);
    }

    private static void AssertIdentity(JsonElement payload, OutboxMessageEntity origin)
    {
        Assert.Equal("42", payload.GetProperty("eventId").GetString());
        AssertInstant(payload, "confirmedAt", origin.OccurredAt);
    }

    private static void AssertInstant(JsonElement payload, string name, DateTimeOffset expected)
    {
        var value = payload.GetProperty(name).GetDateTimeOffset();
        Assert.Equal(expected, value);
        Assert.Equal(TimeSpan.Zero, value.Offset);
    }
}
