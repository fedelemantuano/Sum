FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG TARGETARCH
ARG DOTNET_RID
WORKDIR /src
COPY . .
RUN set -eu; \
    if [ -z "${DOTNET_RID:-}" ]; then \
      case "${TARGETARCH}" in amd64) DOTNET_RID=linux-x64 ;; arm64) DOTNET_RID=linux-arm64 ;; *) echo "Unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; esac; \
    fi; \
    dotnet publish SumECmd/SumECmd.csproj -c Release -f net9.0 \
      -r "${DOTNET_RID}" --self-contained true \
      -p:InvariantGlobalization=true -o /app/bin/SumECmd

FROM build AS test
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-venv \
    && rm -rf /var/lib/apt/lists/*
RUN python3 -m venv /opt/sum-python \
    && /opt/sum-python/bin/pip install --no-cache-dir -r SumData/python/requirements.txt
ENV SUM_PYTHON=/opt/sum-python/bin/python
RUN /opt/sum-python/bin/python -m unittest discover -s SumTest/python -v \
    && dotnet test SumTest/SumTest.csproj -c Release \
    && dotnet build SumECmd/SumECmd.csproj -c Release

# Binary-only export; running outside the image still needs Python + requirements.
FROM scratch AS export
COPY --from=build /app/bin/SumECmd /

# Last stage is the runnable image, including the non-Windows ESE reader.
FROM ubuntu:24.04 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-venv ca-certificates \
       libstdc++6 libssl3t64 zlib1g \
    && rm -rf /var/lib/apt/lists/*
COPY SumData/python/requirements.txt /opt/sum-python-requirements.txt
RUN python3 -m venv /opt/sum-python \
    && /opt/sum-python/bin/pip install --no-cache-dir -r /opt/sum-python-requirements.txt
WORKDIR /app
COPY --from=build /app/bin/SumECmd ./bin/SumECmd/
ENV SUM_PYTHON=/opt/sum-python/bin/python \
    PYTHONDONTWRITEBYTECODE=1 \
    PATH="/app/bin/SumECmd:${PATH}"
ENTRYPOINT ["SumECmd"]
