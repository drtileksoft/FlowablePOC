# ========= build =========
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Clean any local build outputs that might have been copied from the workspace.
RUN for project in AMCSSZ.NWF.Shared.ExternalFlowableWorker AMCSSZ.NWF.Shared.ExternalFlowableWorkerImplementations AMCSSZ.NWF.Shared.FlowableHttpWorkerUsageExample AMCSSZ.NWF.Shared.HttpLogApi; do \
        rm -rf "./src/${project}/bin" "./src/${project}/obj"; \
    done

# Restore all projects via the solution to leverage caching.
COPY ./src/FlowableWorker.sln ./
COPY ./src/AMCSSZ.NWF.Shared.ExternalFlowableWorker/AMCSSZ.NWF.Shared.ExternalFlowableWorker.csproj ./AMCSSZ.NWF.Shared.ExternalFlowableWorker/
COPY ./src/AMCSSZ.NWF.Shared.ExternalFlowableWorkerImplementations/AMCSSZ.NWF.Shared.ExternalFlowableWorkerImplementations.csproj ./AMCSSZ.NWF.Shared.ExternalFlowableWorkerImplementations/
COPY ./src/AMCSSZ.NWF.Shared.FlowableHttpWorkerUsageExample/AMCSSZ.NWF.Shared.FlowableHttpWorkerUsageExample.csproj ./AMCSSZ.NWF.Shared.FlowableHttpWorkerUsageExample/
COPY ./src/AMCSSZ.NWF.Shared.HttpLogApi/AMCSSZ.NWF.Shared.HttpLogApi.csproj ./AMCSSZ.NWF.Shared.HttpLogApi/
RUN dotnet restore FlowableWorker.sln

# Copy the remaining source and publish the worker + local HttpLogApi.
COPY ./src ./
RUN dotnet publish AMCSSZ.NWF.Shared.FlowableHttpWorkerUsageExample/AMCSSZ.NWF.Shared.FlowableHttpWorkerUsageExample.csproj -c Release -o /app/publish/AMCSSZ.NWF.Shared.FlowableHttpWorkerUsageExample --no-restore \
    && dotnet publish AMCSSZ.NWF.Shared.HttpLogApi/AMCSSZ.NWF.Shared.HttpLogApi.csproj -c Release -o /app/publish/AMCSSZ.NWF.Shared.HttpLogApi --no-restore

# ========= runtime =========
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    TZ=Europe/Prague \
    HTTP_LOG_API_PORT=5005

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish/AMCSSZ.NWF.Shared.FlowableHttpWorkerUsageExample ./AMCSSZ.NWF.Shared.FlowableHttpWorkerUsageExample/
COPY --from=build /app/publish/AMCSSZ.NWF.Shared.HttpLogApi ./AMCSSZ.NWF.Shared.HttpLogApi/
COPY docker-entrypoint.sh ./
RUN sed -i 's/\r$//' docker-entrypoint.sh \
    && chmod +x docker-entrypoint.sh

# Expose the local HttpLogApi for debugging if needed.
EXPOSE 5005

WORKDIR /app
ENTRYPOINT ["/app/docker-entrypoint.sh"]
