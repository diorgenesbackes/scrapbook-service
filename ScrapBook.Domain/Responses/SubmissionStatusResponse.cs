using System.Text.Json.Serialization;
using ScrapBook.Domain.Models;

namespace ScrapBook.Domain.Responses;

public sealed record SubmissionStatusResponse(
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ConfirmedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CompletionDeadlineAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? PendingPositions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PhotoPositionProblemModel[]? Problems = null);
