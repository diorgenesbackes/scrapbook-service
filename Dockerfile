FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
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
ARG SCRAPBOOK_RELEASE_VERSION=local
RUN dotnet publish ScrapBook.Api/ScrapBook.Api.csproj --configuration Release --no-restore --output /out \
    -p:UseAppHost=false -p:InformationalVersion="$SCRAPBOOK_RELEASE_VERSION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
WORKDIR /app
COPY --from=build /out/ ./
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg=7:6.1.1-3ubuntu5 \
    && rm -rf /var/lib/apt/lists/*
RUN mkdir -m 0755 /app/certs
ADD --chmod=0444 --checksum=sha256:b1711d12bae51838581281e23b6cb97b1074016873b4dafc80ed14002462dd77 https://truststore.pki.rds.amazonaws.com/us-east-1/us-east-1-bundle.pem /app/certs/rds-us-east-1.pem
RUN mkdir /app/keys && chown app /app/keys && chmod 700 /app/keys
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ScrapBook.Api.dll"]
