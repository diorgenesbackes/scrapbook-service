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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime

FROM runtime AS video-tools
WORKDIR /video-tools
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential gpg meson nasm ninja-build pkg-config xz-utils \
        libopus-dev libvpx-dev libx264-dev zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
ADD --checksum=sha256:686616b7c69eb88d44459391ab25cac13b6647a3b288835c5784e71c1514a5c5 https://download.videolan.org/pub/videolan/dav1d/1.5.4/dav1d-1.5.4.tar.xz dav1d.tar.xz
ADD --checksum=sha256:1a4d394ae63d190a526cd052eb4415c156b5c5e2df37efab41a7de0b8fde2433 https://ffmpeg.org/releases/ffmpeg-9.1.tar.xz ffmpeg.tar.xz
ADD --checksum=sha256:8c0bf6017492434e11766c6cc841b30e4a6b31e896eaf813de0acd05757fd50e https://ffmpeg.org/releases/ffmpeg-9.1.tar.xz.asc ffmpeg.tar.xz.asc
ADD --checksum=sha256:397b3becedcd5a98769967ff1ff8501ddc89f8368b8f766e4701377d7dbaabe5 https://ffmpeg.org/ffmpeg-devel.asc ffmpeg-devel.asc
RUN gpg --dearmor --output ffmpeg.gpg ffmpeg-devel.asc \
    && gpgv --status-fd 1 --keyring /video-tools/ffmpeg.gpg ffmpeg.tar.xz.asc ffmpeg.tar.xz > ffmpeg-signature \
    && grep -Eq '^\[GNUPG:\] VALIDSIG FCF986EA15E6E293A5644F10B4322F04D67658D8 ' ffmpeg-signature \
    && tar -xJf ffmpeg.tar.xz \
    && tar -xJf dav1d.tar.xz
RUN meson setup dav1d-build dav1d-1.5.4 --prefix /opt/dav1d --libdir lib --default-library static \
        -Denable_tests=false -Denable_tools=false \
    && meson compile -C dav1d-build -j 4 \
    && meson install -C dav1d-build --no-rebuild
WORKDIR /video-tools/ffmpeg-9.1
RUN PKG_CONFIG_PATH=/opt/dav1d/lib/pkgconfig ./configure --disable-autodetect --disable-doc --disable-ffplay \
        --disable-network --disable-protocols --enable-protocol=file \
        --disable-demuxers --enable-demuxer=mov,matroska,mp3,wav,ogg \
        --enable-gpl --enable-libdav1d --enable-libopus --enable-libvpx --enable-libx264 --enable-zlib \
    && make -j4 ffmpeg ffprobe \
    && mkdir -p /out/bin /out/licenses \
    && install -m 0755 ffmpeg ffprobe /out/bin/ \
    && install -m 0644 COPYING.GPLv2 /out/licenses/ffmpeg.txt \
    && install -m 0644 ../dav1d-1.5.4/COPYING /out/licenses/dav1d.txt

FROM runtime
WORKDIR /app
COPY --from=build /out/ ./
RUN apt-get update \
    && apt-get install -y --no-install-recommends libopus0 libvpx9 libx264-164 zlib1g \
    && rm -rf /var/lib/apt/lists/*
COPY --from=video-tools /out/bin/ /usr/bin/
COPY --from=video-tools /out/licenses/ /usr/share/doc/scrapbook-video-tools/
RUN mkdir -m 0755 /app/certs
ADD --chmod=0444 --checksum=sha256:b1711d12bae51838581281e23b6cb97b1074016873b4dafc80ed14002462dd77 https://truststore.pki.rds.amazonaws.com/us-east-1/us-east-1-bundle.pem /app/certs/rds-us-east-1.pem
RUN mkdir /app/keys && chown app /app/keys && chmod 700 /app/keys
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ScrapBook.Api.dll"]
