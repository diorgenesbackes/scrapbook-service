using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScrapBook.Domain.Requests;

public sealed record MediaUploadAuthorizationRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement PartNumber = default);
