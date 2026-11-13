using ScrapBook.Domain.Requests;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ScrapBook.Api.Transformers;

public sealed class EventOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "ScrapBook — Gestão de Eventos";
        document.Info.Version = "v1";
        document.Info.Description = "Identidade simulada exclusivamente local, desabilitada por padrão. Não implementa login. " +
            "IDs e versões são strings decimais bigint. Datas de negócio são civis em America/Sao_Paulo, sem offset. " +
            "Escritas não têm retry automático nem chave de idempotência; após resposta perdida, consultar antes de repetir.";
        foreach (var (path, item) in document.Paths.Where(entry => entry.Key.StartsWith("/api/v1/events", StringComparison.Ordinal)))
            foreach (var operation in item.Operations?.Values.AsEnumerable() ?? [])
            {
                operation.Responses ??= new OpenApiResponses();
                operation.Responses.TryAdd("500", new OpenApiResponse
                {
                    Description = "UNEXPECTED_ERROR; falha sanitizada. Conferir estado antes de repetir uma escrita.",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/problem+json"] = new()
                        { Schema = new OpenApiSchemaReference("ProblemDetails", document) }
                    }
                });
                operation.Description = "Exige responsável vinculado; recurso alheio e ausente retornam o mesmo 404. " +
                    "Orçamento de 5s incluindo dependências. Respostas sem cache. Nenhum dado de recebimentos é exposto. " +
                    (operation.OperationId == "GetEventSharing" ? "Consulta de compartilhamento somente leitura; origem configurada, nunca Host/Forwarded. 503 SHARING_NOT_CONFIGURED se ausente. " : "") +
                    (operation.OperationId == "CreateEvent" ? "Criação confirma evento, membro e auditoria na mesma transação; 201 somente após commit. " : "") +
                    (operation.RequestBody is not null ? "JSON UTF-8 de até 32768 bytes. Campos desconhecidos são rejeitados. " : "") +
                    (operation.OperationId?.StartsWith("Update", StringComparison.Ordinal) == true ? "expectedVersion obrigatório; 409 exige reconciliação. " : "");
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "X-Correlation-Id",
                    In = ParameterLocation.Header,
                    Required = false,
                    Description = "Um UUID canônico; ausente, inválido ou múltiplo é substituído.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
                });
                foreach (var parameter in operation.Parameters.OfType<OpenApiParameter>())
                {
                    if (parameter.Name == "eventId") parameter.Schema = Decimal(true);
                    if (parameter.Name == "cursor") parameter.Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        MaxLength = 2048,
                        Description = "Protegido por 24h, vinculado à conta e à ordem created_at DESC, id DESC. Não concede autorização."
                    };
                    if (parameter.Name == "limit") parameter.Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "50", Default = JsonValue.Create(20) };
                }
                foreach (var response in operation.Responses?.Values.OfType<OpenApiResponse>() ?? [])
                {
                    response.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    response.Headers["X-Correlation-Id"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" } };
                    response.Headers["Cache-Control"] = new OpenApiHeader { Description = "no-store", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
                }
                if (operation.OperationId == "CreateEvent" && operation.Responses?["201"] is OpenApiResponse created)
                    created.Headers!["Location"] = new OpenApiHeader { Description = "/api/v1/events/{id}", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
            }
        foreach (var (name, value) in document.Components?.Schemas ?? new Dictionary<string, IOpenApiSchema>())
        {
            if (value is not OpenApiSchema schema || schema.Properties is null) continue;
            if (name == "ProblemDetails")
            {
                schema.Properties["code"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Código estável: REQUEST_VALIDATION_FAILED, IDENTITY_AUTHENTICATION_REQUIRED, REQUEST_ORIGIN_NOT_ALLOWED, RESOURCE_NOT_FOUND, EVENT_LOCKED, EVENT_OPENING_LOCKED, EVENT_VERSION_CONFLICT, REQUEST_TOO_LARGE, UNSUPPORTED_MEDIA_TYPE, DEPENDENCY_UNAVAILABLE, SHARING_NOT_CONFIGURED ou UNEXPECTED_ERROR." };
                schema.Properties["correlationId"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" };
                schema.Properties["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Trace W3C real quando disponível." };
            }
            if (name.EndsWith("Request", StringComparison.Ordinal)) schema.AdditionalPropertiesAllowed = false;
            foreach (var (field, property) in schema.Properties)
            {
                if (property is not OpenApiSchema member) continue;
                if (field is "id" or "version" or "expectedVersion") { member.Pattern = "^[0-9]+$"; member.Description = "String decimal entre 0 e 9223372036854775807; id deve ser positivo."; }
                if (field == "name") { member.MinLength = 1; member.MaxLength = 120; member.Description = "Após trim Unicode; 1–120 valores escalares, sem NUL ou surrogate isolado."; }
                if (field == "welcomeMessage") { member.MaxLength = 2000; member.Description = "Até 2000 valores escalares; NULL ou branco restaura a mensagem padrão v1."; }
                if (field == "openingMode") { member.Enum = [JsonValue.Create("immediate"), JsonValue.Create("scheduled")]; if (name == nameof(CreateEventRequest)) member.Default = JsonValue.Create("scheduled"); }
                if (field is "startsAt" or "opensAt" or "closesAt" or "completionDeadlineAt") { member.Pattern = "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}$"; member.Description = "Data civil de Brasília yyyy-MM-ddTHH:mm:ss, sem Z, offset ou frações."; }
                if (field == "openingDate") { member.Format = "date"; member.Description = "NULL no modo immediate. scheduled exige data futura maior que o encerramento; na criação, omissão sugere startsAt + 1 ano (29/02 → 28/02)."; }
                if (field == "serverTime") { member.Format = "date-time"; member.Description = "Referência UTC do servidor com Z."; }
            }
        }
        return Task.CompletedTask;
    }

    private static OpenApiSchema Decimal(bool positive) => new()
    {
        Type = JsonSchemaType.String,
        Pattern = "^[0-9]+$",
        Description = positive ? "Bigint positivo em string decimal, até 9223372036854775807." : "Bigint não negativo em string decimal."
    };
}
