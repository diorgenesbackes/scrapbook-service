# Scrap Book — Backend

Solution `ScrapBook.sln` em .NET 10. A feature `GE-STORY-001` implementa Gestão de Eventos, com identidade simulada exclusivamente local, PostgreSQL, auditoria e concorrência otimista. `/health` continua independente do banco. Esta branch ainda não foi promovida nem publicada.

## Requisitos

- SDK .NET **10.0.401**, registrado em `global.json`, com atualização de patch permitida.
- Runtime ASP.NET Core **10.0.12** validado neste ambiente.
- Pacotes NuGet fixados nos projetos e resolução registrada em `packages.lock.json`.

## Estrutura e dependências

```text
ScrapBook.sln
ScrapBook.Api/
  Endpoints/                  # health e tradução HTTP
  Modules/                    # Identity, Events, Records, Notifications
  Properties/                 # perfis locais de execução
ScrapBook.Application/
  Modules/                    # casos de uso, portas e resultados por módulo
ScrapBook.Domain/
  Modules/                    # entidades, valores e invariantes por módulo
ScrapBook.Infrastructure/
  Modules/                    # persistência e adaptadores por módulo
ScrapBook.UnitTests/
  Endpoints/                  # unitários da resposta de saúde
  Modules/                    # unitários por módulo
```

Todos os projetos ficam diretamente na raiz de `scrapbook-service`, junto da solution, sem diretórios intermediários `src/`, `test/` ou `tests/`.

Todos os projetos contêm os diretórios `Modules/Identity`, `Modules/Events`, `Modules/Records` e `Modules/Notifications`. Os arquivos `.gitkeep` preservam esses espaços até a implementação das funcionalidades; não representam código de negócio entregue.

| Projeto | Referências |
| --- | --- |
| Domain | Nenhum outro projeto ou framework de infraestrutura. |
| Application | Domain. |
| Infrastructure | Application e Domain. |
| API | Application e Infrastructure, no ponto de composição. |
| UnitTests | API e framework ASP.NET Core para testar componentes isolados, sem hospedar servidor HTTP. |

Events possui `EventsDbContext`, store e transações próprios; Identity consulta somente a conta ativa por porta interna e contexto separado. EF Core **10.0.15**, Npgsql/provider **10.0.3** e Microsoft.AspNetCore.OpenApi **10.0.15** estão fixados nos projetos/lockfiles. Liquibase é o único executor de DDL; o startup não migra nem prepara dados.

A base física existente contém 33 tabelas/47 changesets. Esta feature usa essa base sem alterar migrations. Cognito, convites, Records, galeria, capa customizada e integrações AWS permanecem fora desta entrega.

## Restaurar, compilar e testar

A partir de `scrapbook-service/`:

```bash
dotnet restore ScrapBook.sln --locked-mode
dotnet build ScrapBook.sln --no-restore
dotnet test ScrapBook.sln --no-build --no-restore
```

Somente unitários no projeto de testes desta solution. xUnit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1 e runner 3.1.4 estão fixados. Ao alterar dependências intencionalmente, atualizar os lockfiles com `dotnet restore ScrapBook.sln --force-evaluate` e revisar o diff.

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

As configurações de proxy estão no [frontend](https://github.com/diorgenesbackes/scrapbook-ui/blob/feature/GE-STORY-001/README.md). HTTP, PostgreSQL, navegador e carga ficam em [scrapbook-tests](https://github.com/diorgenesbackes/scrapbook-tests/blob/feature/GE-STORY-001/README.md), com acionamento manual.

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
