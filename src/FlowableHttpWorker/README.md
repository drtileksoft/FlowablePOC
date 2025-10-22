# Flowable HTTP Worker Sample Host

Tento projekt slouží jako jednoduchá konzolová ukázka, jak hostovat Flowable external workery
pomocí `Host.CreateApplicationBuilder` a knihovny `FlowableExternalWorker`.
Vlastní implementace HTTP handlerů je nyní umístěna v projektu
[`FlowableExternalWorkerImplementations`](../FlowableExternalWorkerImplementations/README.md).

## Registrace workerů

Registrace handlerů probíhá v [`Program.cs`](./Program.cs):

```csharp
builder.Services
    .AddHttpExternalTaskHandlerWorker(builder.Configuration)
    .AddHttpExternalTaskHandler2Worker(builder.Configuration);
```

Tyto extension metody pochází z projektu `FlowableExternalWorkerImplementations`
a zajistí registraci všech nezbytných služeb včetně `FlowableExternalWorkerService<THandler>`.

## Konfigurace

Každý handler očekává vlastní sekci v `appsettings.json`. Ukázka konfigurace pro
`HttpExternalTaskHandler2`:

```json
"HttpExternalTaskHandler2": {
  "Flowable": {
    "WorkerId": "FLOWABLE_POC_WORKER",
    "Topic": "srd.call",
    "LockDuration": "PT30S",
    "MaxJobsPerTick": 5,
    "MaxDegreeOfParallelism": 2,
    "PollPeriodSeconds": 5,
    "Retry": {
      "InitialDelaySeconds": 10,
      "BackoffMultiplier": 2,
      "MaxDelaySeconds": 300,
      "JitterSeconds": 5
    }
  },
  "Http": {
    "TargetUrl": "https://httplogapi.azurewebsites.net/api/events/FLOWABLE_POC_WORKER",
    "TimeoutSeconds": 30
  }
}
```

Sekce `Flowable` odpovídá `FlowableWorkerOptions` (identifikace workera, téma, retry),
sekce `Http` pak specifikuje cílový endpoint a timeout.

## Spuštění

Projekt je čistý konzolový host. Spuštění probíhá standardním způsobem (`dotnet run`
nebo v rámci docker-compose). Při startu se načte konfigurace, nakonfigurují loggery
a zaregistrují se oba ukázkové workery z knihovny implementací.
