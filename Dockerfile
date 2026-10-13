FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS build

ARG DOTNET_RID=linux-x64

WORKDIR /src
COPY . .

RUN dotnet publish SumECmd/SumECmd.csproj \
    -c Release \
    -f net9.0 \
    -r ${DOTNET_RID} \
    --self-contained true \
    -p:InvariantGlobalization=true \
    -o /app/bin/SumECmd

# Export the complete self-contained directory for Linux or macOS.
FROM scratch AS export
COPY --from=build /app/bin/SumECmd /

# The default target is the runnable Linux image.
FROM ubuntu:24.04 AS runtime
ARG DOTNET_RID=linux-x64
ARG TARGETARCH
RUN case "${TARGETARCH}/${DOTNET_RID}" in \
      amd64/linux-x64|arm64/linux-arm64) ;; \
      *) echo "Match --platform to DOTNET_RID; use --target export for macOS." >&2; exit 1 ;; \
    esac \
    && apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates libstdc++6 libssl3t64 zlib1g \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/bin/SumECmd ./bin/SumECmd/
ENV PATH="/app/bin/SumECmd:${PATH}"
ENTRYPOINT ["SumECmd"]
