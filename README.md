# scrapbook-service

API ASP.NET Core com projetos Api, Application, Domain, Infrastructure e UnitTests. Usa .NET SDK 10.0.401 e um `ScrapBookDbContext` scoped.

```sh
dotnet restore ScrapBook.sln --locked-mode
dotnet build ScrapBook.sln --no-restore
dotnet test ScrapBook.sln --no-build --no-restore
```

Para executar pelo Compose, siga o [runbook local](../scrapbook-infrastructure/README.md). Ao executar diretamente, fornecer a configuração privada `ConnectionStrings__ScrapBook` para a identidade DML `scrapbook_app`. A sondagem padrão tem orçamento de 2 segundos, controlado por `Database__ReadinessTimeoutMs`.

`/health` informa vitalidade e `/health/ready` verifica a base preparada pelo Liquibase. Ambos retornam apenas o estado operacional com correlação. O startup valida a configuração obrigatória e informa somente o nome da opção quando inválida.

A identidade sintética só pode ser ativada em `Development` com `DevelopmentIdentity__Enabled=true`, um `DevelopmentIdentity__AccountId` positivo e um `DevelopmentIdentity__Token` hexadecimal de 64 caracteres. Na execução direta, use endereços de loopback. Em contêiner, a escuta nas interfaces internas também exige `DevelopmentIdentity__AllowContainerBinding=true` e a indicação `DOTNET_RUNNING_IN_CONTAINER` da imagem. A publicação do Compose permanece em loopback. O cliente local apresenta `Authorization: Development <token>` em endpoints autenticados de desenvolvimento. Use contas sintéticas e mantenha o token em configuração privada. [Spec da F01](../scrapbook-docs/F01-base-da-aplicacao-e-ambientes/spec.md)

Ativar o processamento de áudio da F10 requer pelo menos 1 GiB disponível no diretório temporário gravável de cada réplica. As operações simultâneas de confirmação e preparação podem usar até 778.000.000 bytes em arquivos de vídeo, áudio e normalização. A capacidade deve estar disponível após contabilizar os demais consumidores do volume. [Processamento de mídias](../scrapbook-docs/F10-processamento-de-midias/spec.md)
