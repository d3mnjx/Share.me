FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ShareMe/ShareMe.csproj src/ShareMe/
RUN dotnet restore src/ShareMe/ShareMe.csproj
COPY src/ShareMe/ src/ShareMe/
RUN dotnet publish src/ShareMe/ShareMe.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG TARGETARCH
ARG YT_DLP_VERSION=2026.08.19
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl ffmpeg \
    && case "${TARGETARCH}" in amd64) YTDLP_ASSET=yt-dlp_linux ;; arm64) YTDLP_ASSET=yt-dlp_linux_aarch64 ;; *) exit 1 ;; esac \
    && curl -fSL "https://github.com/yt-dlp/yt-dlp/releases/download/${YT_DLP_VERSION}/${YTDLP_ASSET}" -o "/tmp/${YTDLP_ASSET}" \
    && curl -fSL "https://github.com/yt-dlp/yt-dlp/releases/download/${YT_DLP_VERSION}/SHA2-256SUMS" -o /tmp/SHA2-256SUMS \
    && cd /tmp && grep "  ${YTDLP_ASSET}$" SHA2-256SUMS | sha256sum -c - \
    && mv "/tmp/${YTDLP_ASSET}" /usr/local/bin/yt-dlp && chmod 755 /usr/local/bin/yt-dlp \
    && rm -rf /var/lib/apt/lists/* /tmp/SHA2-256SUMS
# Bundled yt-dlp includes curl_cffi and the YouTube challenge solver. Deno executes
# player challenges locally; no platform JavaScript is served to viewers.
COPY --from=denoland/deno:bin-2.9.6 /deno /usr/local/bin/deno
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p /data && chown app:app /data
ENV ASPNETCORE_HTTP_PORTS=8080 DATA_PATH=/data DENO_DIR=/tmp/deno
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s CMD curl -fsS http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "ShareMe.dll"]
