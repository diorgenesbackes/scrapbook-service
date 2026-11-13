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
                "Resposta pública com code, name, effectiveWelcomeMessage e cover padrão permitida. " +
                "Sem janela, dados privados ou autorização de envio. Mensagem efetiva calculada pelo servidor. " +
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
                foreach (var media in success.Content.Values) media.Example = new JsonObject
                {
                    ["code"] = new string('A', 43), ["name"] = "Encontro entre amigos",
                    ["effectiveWelcomeMessage"] = EventPolicy.DefaultWelcomeMessage,
                    ["cover"] = new JsonObject { ["kind"] = "default", ["assetKey"] = EventPolicy.DefaultCoverAssetKey }
                };
        }
        if (document.Components?.Schemas?.TryGetValue("PublicEventResponse", out var value) == true && value is OpenApiSchema schema)
        {
            schema.AdditionalPropertiesAllowed = false;
            schema.Required = new HashSet<string> { "code", "name", "effectiveWelcomeMessage", "cover" };
            if (schema.Properties?["effectiveWelcomeMessage"] is OpenApiSchema message)
            {
                message.MinLength = 1;
                message.MaxLength = 2000;
                message.Description = "Texto seguro de até 2000 valores escalares Unicode; padrão do servidor quando a configuração é NULL. Não interpretar como HTML.";
            }
        }
        return Task.CompletedTask;
    }
}
