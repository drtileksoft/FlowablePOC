# ========= build =========
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ./src ./src
RUN dotnet restore ./src/FlowableHttpWorker.csproj
RUN dotnet publish ./src/FlowableHttpWorker.csproj -c Release -o /app/publish --no-restore

# ========= runtime =========
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
COPY --from=build /app/publish ./
# optional: default timezone inside container (neřeší kód používající Europe/Prague, ale hodí se)
ENV TZ=Europe/Prague
ENTRYPOINT ["dotnet", "FlowableHttpWorker.dll"]