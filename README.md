# Scrap Book — Backend

Solution `ScrapBook.sln` em .NET 10. A GE-STORY-001 implementa Gestão de Eventos, com identidade simulada exclusivamente local, PostgreSQL, auditoria e concorrência otimista. `/health` continua independente do banco. A refatoração RF-STORY-001 depende dessa entrega e está em feature/RF-STORY-001; sem promoção/publicação.

## Requisitos

- SDK .NET **10.0.401**, registrado em `global.json`, com atualização de patch permitida.
- Runtime ASP.NET Core **10.0.12** validado neste ambiente.
- Pacotes NuGet fixados nos projetos e resolução registrada em `packages.lock.json`.

## Estrutura e dependências

A RF-STORY-001 substitui a organização modular por Clean Architecture de serviço simples. Todas as classes do mesmo conceito ficam juntas na camada correspondente, com uma classe/record por arquivo e sufixo obrigatório. Não há Modules, Endpoints, reservas de domínios nem subpastas funcionais.

```text
ScrapBook.sln
ScrapBook.Api/
  Controllers/            # EventController, HealthController (MVC)
  Middlewares/            # EventRequestMiddleware
  Readers/ Writers/       # HttpJsonBodyReader, HealthHttpResponseWriter
  InputFormatters/        # EventMvcJsonInputFormatter
  DataProtectors/         # EventCursorDataProtector (ASP.NET)
  Validators/ Options/ Extensions/ Transformers/
ScrapBook.Application/
  Interfaces/             # IEventService, IEventRepository, IUnitOfWork
  Services/               # Event, EventCursor, EventTelemetry, DevelopmentCurrentActor
  Commands/ Exceptions/ Scopes/
ScrapBook.Domain/
  Requests/ Responses/    # records de contrato, sem MVC
  Entities/               # EventEntity e POCOs PersistenceEntity
  Mappers/ Readers/       # EventResponseMapper, JsonBodyReader (Stream)
  Builders/ Models/ Policies/ Exceptions/ Enums/ Clocks/
ScrapBook.Infrastructure/
  Repositories/           # EventRepository, AccountRepository
  DbContexts/             # ScrapBookDbContext único/scoped
  Configurations/         # configuração EF por entidade
  Mappers/                # EfPostgresExceptionMapper
  UnitOfWorks/ Extensions/
ScrapBook.UnitTests/       # conceitos testados e Stubs
```

Projetos ficam diretamente na raiz, sem src/test intermediários. Domain usa somente bibliotecas base do .NET, sem pacotes nem referências a outros projetos. Services pertencem exclusivamente a Application, que referencia apenas Domain e define portas e regras de coordenação; Infrastructure implementa persistência; API traduz HTTP e compõe a aplicação. EventController usa IEventService, e EventService usa IEventRepository/IUnitOfWork. Repositórios compartilham o mesmo contexto scoped; cinco configurações explícitas mapeiam as tabelas já utilizadas, sem DDL no startup.

Exemplos obrigatórios de sufixo: Controller, Service, Repository, Entity, Model, Command, Request, Response, Mapper, Validator, Middleware, Configuration, Options, Policy, Clock, Scope, Reader, Writer, Formatter, Transformer, Extensions e Exception. Interfaces usam I e o conceito; enums usam Enum; unitários usam Tests; doubles usam Stub. Program.cs é a entrada/composição convencional.

EF Core 10.0.15, Npgsql/provider 10.0.3 e OpenAPI 10.0.15 permanecem fixados. Os schemas físicos e os propósitos criptográficos/configurações existentes são preservados; não representam módulos de código. A base contém 33 tabelas/47 changesets, sem alteração de histórico nesta refatoração.

A lógica do cursor está em Application/EventCursorService, com proteção via IEventCursorProtector e adaptador API/EventCursorDataProtector; propósito Events.List.v1 e validade de 24h preservados. Leitura JSON pura usa Stream em Domain; HttpJsonBodyReader mantém cabeçalhos/limites e tradução dos erros na API. Formatação MVC, OpenAPI, escrita HTTP de health e tradução de exceções EF/PostgreSQL ficam nas camadas técnicas, explicitadas nos nomes. Os testes LayerDependencyTests impedem regressões de dependência/localização dos Services.

A API usa AddControllers/MapControllers com rotas por atributos. Um input formatter mantém JSON estrito, limites e Problem Details. Contratos de Events/health, transações, versão e autorização mantêm a semântica existente. [Decisão e convenções](https://github.com/diorgenesbackes/scrapbook-docs/blob/feature/RF-STORY-001/adr/ADR-001-backend-clean-architecture.md).

## Restaurar, compilar e testar

A partir de `scrapbook-service/`:

```bash
dotnet restore ScrapBook.sln --locked-mode
dotnet build ScrapBook.sln --no-restore
dotnet test ScrapBook.sln --no-build --no-restore
```

Somente unitários no projeto de testes desta solution. xUnit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1 e runner 3.1.4 estão fixados. Ao alterar dependências intencionalmente, atualizar os lockfiles com `dotnet restore ScrapBook.sln --force-evaluate` e revisar o diff.

## Imagem Docker

A partir deste repositório: `docker build -t scrapbook-service:local .`. O Dockerfile restaura dependências com lockfile e publica em Release no estágio SDK; o runtime ASP.NET 10.0.12/noble executa como app/1654 na porta 8080, com healthcheck GET /health e suporte ao fuso de Brasília. Imagens base estão fixadas por digest. `.dockerignore` exclui Git, bin/obj, configurações locais e chaves.

A stack de `scrapbook-infrastructure/compose.yaml` publica a API em 127.0.0.1:5080 e conecta internamente a postgres:5432, usando somente o runtime DML. O volume cursor_keys preserva Data Protection em /var/lib/scrapbook/cursor-keys. Não há migrations no startup. Preparação/subida/parada: [infraestrutura](https://github.com/diorgenesbackes/scrapbook-infrastructure/blob/feature/6/README.md).

Identidade simulada continua desativada por padrão. Para binding 0.0.0.0 no container local, exige Enabled, AllowContainerBinding e DOTNET_RUNNING_IN_CONTAINER, além de Development, fixture/conexão e origens válidas. O helper stack_local.py aplica esse opt-in por flag; fora de Development, startup é recusado. Isso não habilita identidade de produção.

## Executar a API

```bash
dotnet run --project ScrapBook.Api --launch-profile http
```

O perfil local usa `http://127.0.0.1:5080`, sem abrir navegador automaticamente. HTTP local é destinado ao desenvolvimento; a implantação pública seguirá HTTPS no ALB.

Para consultar:

```bash
curl -i http://127.0.0.1:5080/health
```

## Contrato de saúde

- `GET /health`, fora do prefixo de negócio `/api/v1`.
- `200 application/json` com `{"status":"up"}` quando saudável.
- Resultados degraded/unhealthy são mapeados para `503` com `{"status":"down"}`.
- Resposta sem cache e sem detalhes de dependências, configuração ou exceções.

Nesta etapa não há verificações de dependências registradas: o endpoint indica que a API responde. Prontidão para banco, filas ou AWS exigirá verificações próprias; não usar este resultado como comprovação dessas integrações. Em produção, controlar a exposição operacional conforme a rede e o ALB definidos no HLD.

## Validar pelo frontend

Com a API executando, em outro terminal:

```bash
cd scrapbook-ui
npm run dev
```

Esse comando parte da raiz do workspace. Abra `http://127.0.0.1:5173` e clique em **Verificar backend**. O proxy Vite encaminha `/health` para a API local. Sem a API, a interface informa a falha e permite tentar novamente; cada consulta tem timeout de 5 segundos.

As configurações de proxy estão no [frontend](https://github.com/diorgenesbackes/scrapbook-ui/blob/feature/GE-STORY-001/README.md). HTTP, PostgreSQL, navegador e carga ficam em [scrapbook-tests](https://github.com/diorgenesbackes/scrapbook-tests/blob/feature/RF-STORY-001/README.md), com acionamento manual.

## Executar Gestão de Eventos

Após restaurar/compilar, a partir de `scrapbook-infrastructure/`:

```bash
docker compose up -d --wait --wait-timeout 60 postgres
python3 scripts/liquibase_local.py update
python3 scripts/events_local.py provision-runtime
python3 scripts/events_local.py bootstrap-actor --actor owner
python3 scripts/events_local.py run-api --enable-development-identity --no-build
```

O helper usa o ID real retornado, injeta somente a conexão DML por ambiente e vincula a API a `127.0.0.1:5080`. Sem a flag, Events retorna `401`; ativá-la fora de Development impede startup. O cliente não escolhe conta por header/body/query. Origens permitidas: `DevelopmentIdentity:AllowedOrigins`; padrão `http://127.0.0.1:5173`. Usar o proxy Vite. Requisições locais sem Origin são admitidas.

| Método / rota | Operação |
| --- | --- |
| `POST /api/v1/events` | Criar evento + slot 1 + auditoria no mesmo commit; `201` e Location. |
| `GET /api/v1/events?limit=20&cursor=...` | Listar vínculos, ordem created_at/id desc, limite 1–50. |
| `GET /api/v1/events/{id}` | Detalhe autorizado; ausente/alheio recebem `404`. |
| `PUT /api/v1/events/{id}` | Nome, startsAt, welcomeMessage e expectedVersion. |
| `PUT /api/v1/events/{id}/access` | OpeningMode, openingDate e expectedVersion. |

Contrato executável: `GET /openapi/v1.json`, somente em Development. IDs/versões são strings bigint; datas civis não recebem offset. Body estrito JSON UTF-8 até 32 KiB; erros Problem Details, correlação UUID e no-store. Orçamento compartilhado da API: 5s, lock 3s e comando SQL 4s; não há retry de escrita. Após resposta perdida, consultar antes de repetir.

## Diagnóstico e recuperação

Logs JSON usam UTC, catálogo 4000/4002/4003 e propriedades estruturadas; nunca serializam exceção, SQL, body ou nome/mensagem. EF sensível/detalhado permanece desligado. `ActivitySource`/`Meter` se chamam `ScrapBook.Events`; unitários usam `MeterListener` para conferir coleta/dimensões. `Events:TraceSamplingRate` admite 0–1, default 1 em Development e 0 fora dele. Não há exportador/coletor AWS configurado.

Chaves de cursor em `Events:CursorKeyPath` (helper: `.local/cursor-keys`) devem ser persistidas/privadas. Cursor protegido dura 24h e é vinculado à conta/ordem; não concede acesso. Não apagar chaves como recuperação habitual.

`Ctrl+C` encerra a API. Reiniciar sem `--enable-development-identity` desativa o acesso local. `revoke-runtime` desativa o LOGIN sem excluir dados; reverter o código também preserva eventos/schema. `rollback-actor` só remove fixture sem referências; FKs recusam conta já usada. Não remover volume nem executar rollback da base populada.

## Contribuição

A branch principal é `main`. Seguir o [guia de contribuição](https://github.com/diorgenesbackes/scrapbook-docs/blob/main/AGENTS.md) e o [Git Flow](https://github.com/diorgenesbackes/scrapbook-docs/blob/main/guidelines/git-flow.md). Versionar manifests e lockfiles; dependências instaladas, artefatos gerados e segredos permanecem locais.

## Acesso público ao evento (PP-STORY-001)

`GET /api/v1/public/events/{code}` consulta anonimamente o código já persistido. Retorna somente `{ "code": "…" }`; código inválido ou inexistente retorna `404 RESOURCE_NOT_FOUND`. Não exige identidade local e não grava dados. Códigos são base64url canônicos de 32 bytes, 43 caracteres, sensíveis a maiúsculas. PK não substitui código nem autoriza gestão.

OpenAPI em Development inclui `GetPublicEvent`. Orçamento de 5s, cancelamento propagado e `Cache-Control: no-store`; correlação segura nos headers/erros. O caminho privado `/api/v1/events` preserva suas regras. Scopes automáticos do console ficam desativados para não incluir RequestPath utilizável; os campos explícitos de correlação/tracing permanecem.

Contrato e evidências: [FDD](https://github.com/diorgenesbackes/scrapbook-docs/blob/feature/PP-STORY-001/fdd/FDD-Acesso-Publico-do-Evento/FDD-Acesso-Publico-do-Evento.md). Sem nova migration ou dependência. Nome/capa/mensagem e disponibilidade pertencem às próximas histórias.

## Apresentação pública (PP-STORY-002)

GET /api/v1/public/events/{code} mantém o acesso anônimo e adiciona name, effectiveWelcomeMessage e cover pública permitida. O serviço reutiliza a política de mensagem efetiva da gestão e a referência padrão; o repositório projeta só code/name/welcome_message, sem carregar registros, membros ou mídia privada. O cliente anterior pode continuar usando somente code. A nova UI requer a API atualizada primeiro. Nenhuma migration ou dependência nova.

## Compartilhamento (PP-STORY-003)

GET /api/v1/events/{eventId}/share retorna code/name/publicUrl somente para responsável vinculado. Os dois slots têm o mesmo acesso. Código público não substitui PK ou identidade. PublicSite:Origin define a origem canônica, nunca Host/Forwarded; HTTPS externo, HTTP loopback com opt-in AllowHttpLoopback. Configuração ausente: 503 SHARING_NOT_CONFIGURED no endpoint privado e publicUrl=null na apresentação pública, sem links inventados. A origem inválida impede startup.

Domain mantém modelo/política/respostas puros; serviço e portas em Application, projeção mínima EF em Infrastructure. Sem nova dependency ou DDL. API/configuração devem ser publicadas antes da UI; não houve publicação externa nesta feature.

## Disponibilidade pública dos envios (PP-STORY-004)

GET público acrescenta serverTime UTC e submissionWindow com state (not_open/open/closed), opensAt/closesAt civis de Brasília, timeZone e revalidateAfterMs (0–30000). PublicEventService amostra BusinessClock.UtcNow uma vez após a consulta; SubmissionWindowPolicy reutiliza EventPolicy.Window. Abertura inclusiva duas horas antes; encerramento exclusivo para novos envios ao meio-dia seguinte. Entre 12h e 13h continua closed: tolerância exige tentativa previamente autorizada, ainda não implementada.

A consulta projeta somente code/name/welcome_message/starts_at e não depende do modo/data da galeria. No-store e orçamento de 5s preservados. Estado público é informativo, nunca autorização de upload. Sem configuração de relógio em produção ou DDL novo. Testes temporais HTTP usam exclusivamente o host de probes em scrapbook-tests; publicar API antes da UI que exige o novo contrato.

## Erros e recuperação pública (PP-STORY-005)

GET público sem código, com segmento inválido ou caminhos extras recebe o mesmo 404 RESOURCE_NOT_FOUND/Problem Details de código ausente. Rota simples válida tem precedência sobre catch-all; código malformado não consulta o banco. Headers no-store e correlação UUID seguem o middleware, com operação nominal events.public_get também nos handlers inválidos.

Persistência/conexão/timeout reconhecidos retornam 503 DEPENDENCY_UNAVAILABLE; falha inesperada retorna 500 UNEXPECTED_ERROR. Corpo/logs não incluem mensagem de exceção, SQL, credenciais ou código do evento. ClientAborted não vira Problem Details de negócio. URI/encoding/tamanho recusados pelo transporte antes da API podem ter corpo não JSON e não recebem garantia dos headers da aplicação. Nenhuma dependência/DDL nova; API/Domain/Application mantêm a separação existente.
