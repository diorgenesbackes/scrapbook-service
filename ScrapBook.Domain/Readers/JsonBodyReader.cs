using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScrapBook.Domain.Readers;

public static class JsonBodyReader
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };

    public static ValueTask<object?> ReadAsync(Stream body, Type modelType, CancellationToken cancellationToken)
        => JsonSerializer.DeserializeAsync(body, modelType, InputJson, cancellationToken);
}
