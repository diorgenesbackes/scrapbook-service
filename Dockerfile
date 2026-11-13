FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY ScrapBook.Api/ScrapBook.Api.csproj ScrapBook.Api/packages.lock.json ScrapBook.Api/
COPY ScrapBook.Application/ScrapBook.Application.csproj ScrapBook.Application/packages.lock.json ScrapBook.Application/
COPY ScrapBook.Domain/ScrapBook.Domain.csproj ScrapBook.Domain/packages.lock.json ScrapBook.Domain/
COPY ScrapBook.Infrastructure/ScrapBook.Infrastructure.csproj ScrapBook.Infrastructure/packages.lock.json ScrapBook.Infrastructure/
RUN dotnet restore ScrapBook.Api/ScrapBook.Api.csproj --locked-mode
COPY ScrapBook.Api/ ScrapBook.Api/
COPY ScrapBook.Application/ ScrapBook.Application/
COPY ScrapBook.Domain/ ScrapBook.Domain/
COPY ScrapBook.Infrastructure/ ScrapBook.Infrastructure/
RUN dotnet publish ScrapBook.Api/ScrapBook.Api.csproj --configuration Release --no-restore --output /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Events__CursorKeyPath=/var/lib/scrapbook/cursor-keys
RUN mkdir -p /var/lib/scrapbook/cursor-keys && chown "$APP_UID" /var/lib/scrapbook/cursor-keys
COPY --from=build /publish/ ./
COPY docker/healthcheck.sh ./healthcheck.sh
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=5 CMD ["bash", "/app/healthcheck.sh"]
ENTRYPOINT ["dotnet", "ScrapBook.Api.dll"]
