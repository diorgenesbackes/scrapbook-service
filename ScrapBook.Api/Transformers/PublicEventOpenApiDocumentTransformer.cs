using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using ScrapBook.Domain.Policies;

namespace ScrapBook.Api.Transformers;

public sealed class PublicEventOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "ScrapBook — Eventos";
        if (!document.Paths.TryGetValue("/api/v1/public/events/{code}", out var path)) return Task.CompletedTask;
        foreach (var operation in path.Operations?.Values.AsEnumerable() ?? [])
        {
            operation.Description = "Consulta anônima e somente leitura. Código base64url canônico de 32 bytes (43 caracteres), " +
                "sensível a maiúsculas, sem trim. PK não substitui código. Inválido ou ausente: RESOURCE_NOT_FOUND (404). " +
                "Resposta pública com code, name, effectiveWelcomeMessage, cover padrão e publicUrl canônica (NULL se a origem não foi configurada). " +
                "serverTime UTC e submissionWindow são calculados uma vez pelo servidor em America/Sao_Paulo: " +
                "not_open antes do início, open no intervalo [abertura, meio-dia seguinte), closed a partir de 12h, inclusive entre 12h e 13h. " +
                "opensAt/closesAt são horários civis sem offset; revalidateAfterMs (0–30000) é o prazo máximo para nova consulta, " +
                "descontando o tempo decorrido da requisição; valor vencido exige revalidação, sem anunciar disponibilidade. " +
                "Sem dados privados ou autorização de envio. Mensagem efetiva calculada pelo servidor. " +
                "Sem sessão/cookie obrigatório. Query e body não fazem parte do contrato. Orçamento total 5s; no-store.";
            operation.Security = [];
            foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
                if (parameter.Name == "code") parameter.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String, MinLength = 43, MaxLength = 43,
                    Pattern = "^[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$",
                    Description = "32 bytes em base64url sem padding; decodificar o segmento uma vez, sem alterar caixa."
                };
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "X-Correlation-Id", In = ParameterLocation.Header, Required = false,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
                Description = "UUID canônico único; ausente, inválido ou múltiplo é substituído."
            });
            foreach (var response in operation.Responses?.Values.OfType<OpenApiResponse>() ?? [])
            {
                response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                response.Headers["X-Correlation-Id"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" } };
                response.Headers["Cache-Control"] = new OpenApiHeader { Description = "no-store", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
            }
            if (operation.Responses?["200"] is OpenApiResponse success && success.Content is not null)
                foreach (var media in success.Content.Values) media.Examples = new Dictionary<string, IOpenApiExample>
                {
                    ["before"] = new OpenApiExample { Value = Example("not_open", "2030-06-15T18:00:00.000Z") },
                    ["during"] = new OpenApiExample { Value = Example("open", "2030-06-15T19:00:00.000Z") },
                    ["after"] = new OpenApiExample { Value = Example("closed", "2030-06-16T15:00:00.000Z") }
                };
        }
        if (document.Components?.Schemas?.TryGetValue("PublicEventResponse", out var value) == true && value is OpenApiSchema schema)
        {
            schema.AdditionalPropertiesAllowed = false;
            schema.Required = new HashSet<string> { "code", "name", "effectiveWelcomeMessage", "cover", "publicUrl", "serverTime", "submissionWindow" };
            if (schema.Properties?["effectiveWelcomeMessage"] is OpenApiSchema message)
            {
                message.MinLength = 1;
                message.MaxLength = 2000;
                message.Description = "Texto seguro de até 2000 valores escalares Unicode; padrão do servidor quando a configuração é NULL. Não interpretar como HTML.";
            }
        }
        if (document.Components?.Schemas?.TryGetValue("PublicSubmissionWindowResponse", out var windowValue) == true && windowValue is OpenApiSchema window)
        {
            window.AdditionalPropertiesAllowed = false;
            window.Required = new HashSet<string> { "state", "opensAt", "closesAt", "timeZone", "revalidateAfterMs" };
            foreach (var (field, property) in window.Properties ?? new Dictionary<string, IOpenApiSchema>())
            {
                if (property is not OpenApiSchema member) continue;
                if (field == "state") member.Enum = [JsonValue.Create("not_open")!, JsonValue.Create("open")!, JsonValue.Create("closed")!];
                if (field is "opensAt" or "closesAt")
                {
                    member.Format = null;
                    member.Pattern = "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}$";
                    member.Description = "Horário civil em America/Sao_Paulo, sem offset; não interpretar no fuso do dispositivo.";
                }
                if (field == "timeZone") member.Enum = [JsonValue.Create("America/Sao_Paulo")!];
                if (field == "revalidateAfterMs") { member.Minimum = "0"; member.Maximum = "30000"; }
            }
        }
        return Task.CompletedTask;
    }

    private static JsonObject Example(string state, string serverTime) => new()
    {
        ["code"] = new string('A', 43), ["name"] = "Encontro entre amigos",
        ["effectiveWelcomeMessage"] = EventPolicy.DefaultWelcomeMessage, ["publicUrl"] = null,
        ["serverTime"] = serverTime,
        ["submissionWindow"] = new JsonObject
        {
            ["state"] = state, ["opensAt"] = "2030-06-15T16:00:00", ["closesAt"] = "2030-06-16T12:00:00",
            ["timeZone"] = "America/Sao_Paulo", ["revalidateAfterMs"] = 30000
        },
        ["cover"] = new JsonObject { ["kind"] = "default", ["assetKey"] = EventPolicy.DefaultCoverAssetKey }
    };
}
