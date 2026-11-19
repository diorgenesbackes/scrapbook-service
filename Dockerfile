FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY ScrapBook.Api/ScrapBook.Api.csproj ScrapBook.Api/packages.lock.json ./ScrapBook.Api/
COPY ScrapBook.Application/ScrapBook.Application.csproj ScrapBook.Application/packages.lock.json ./ScrapBook.Application/
COPY ScrapBook.Domain/ScrapBook.Domain.csproj ScrapBook.Domain/packages.lock.json ./ScrapBook.Domain/
COPY ScrapBook.Infrastructure/ScrapBook.Infrastructure.csproj ScrapBook.Infrastructure/packages.lock.json ./ScrapBook.Infrastructure/
RUN dotnet restore ScrapBook.Api/ScrapBook.Api.csproj --locked-mode
COPY ScrapBook.Api/ ScrapBook.Api/
COPY ScrapBook.Application/ ScrapBook.Application/
COPY ScrapBook.Domain/ ScrapBook.Domain/
COPY ScrapBook.Infrastructure/ ScrapBook.Infrastructure/
RUN dotnet publish ScrapBook.Api/ScrapBook.Api.csproj --configuration Release --no-restore --output /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir /app/keys && chown app /app/keys && chmod 700 /app/keys
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ScrapBook.Api.dll"]
