# ========= build =========
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore all projects via the solution to leverage caching.
COPY ./src/FlowableWorker.sln ./
COPY ./src/FlowableExternalWorker/FlowableExternalWorker.csproj ./FlowableExternalWorker/
COPY ./src/FlowableHttpWorker/FlowableHttpWorker.csproj ./FlowableHttpWorker/
COPY ./src/HttpLogApi/HttpLogApi.csproj ./HttpLogApi/
RUN dotnet restore FlowableWorker.sln

# Copy the remaining source and publish the worker + local HttpLogApi.
COPY ./src ./
RUN dotnet publish FlowableHttpWorker/FlowableHttpWorker.csproj -c Release -o /app/publish/FlowableHttpWorker --no-restore \
    && dotnet publish HttpLogApi/HttpLogApi.csproj -c Release -o /app/publish/HttpLogApi --no-restore

# ========= runtime =========
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    TZ=Europe/Prague \
    HTTP_LOG_API_PORT=5005

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish/FlowableHttpWorker ./FlowableHttpWorker/
COPY --from=build /app/publish/HttpLogApi ./HttpLogApi/
COPY docker-entrypoint.sh ./
RUN sed -i 's/\r$//' docker-entrypoint.sh \
    && chmod +x docker-entrypoint.sh

# Expose the local HttpLogApi for debugging if needed.
EXPOSE 5005

WORKDIR /app
ENTRYPOINT ["/app/docker-entrypoint.sh"]
