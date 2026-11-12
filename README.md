# Scrap Book — Backend

Solution `ScrapBook.sln` em .NET 10, seguindo o [guideline de backend](https://github.com/diorgenesbackes/scrapbook-docs/blob/main/guidelines/backend.md). A base implementa a API operacional `/health` e prepara as camadas e os módulos do monólito.

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

Cada módulo terá schema e `DbContext` próprios quando houver modelo físico aprovado. EF Core, PostgreSQL, Cognito e demais integrações ainda não foram configurados. Liquibase continuará sendo o único executor de alterações estruturais; a API não executa migrations.

A [modelagem proposta](https://github.com/diorgenesbackes/scrapbook-docs/blob/main/db/README.md) já documenta entidades, relações, tipos e integridade. Ela aguarda revisão antes da implementação; não adiciona scripts, mappings ou dependências a esta solution.

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

As configurações de proxy e endereços estão no [README do frontend](https://github.com/diorgenesbackes/scrapbook-ui/blob/main/README.md#api-e-configuração). Testes HTTP, navegador, banco e integrações permanecem fora desta solution, no futuro projeto `scrapbook-tests`, com acionamento manual.

## Contribuição

A branch principal é `main`. Seguir o [guia de contribuição](https://github.com/diorgenesbackes/scrapbook-docs/blob/main/AGENTS.md) e o [Git Flow](https://github.com/diorgenesbackes/scrapbook-docs/blob/main/guidelines/git-flow.md). Versionar manifests e lockfiles; dependências instaladas, artefatos gerados e segredos permanecem locais.
