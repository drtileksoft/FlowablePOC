# Flowable HTTP Worker Sample

Tento projekt ukazuje, jak pomocí knihovny `FlowableExternalWorker` vytvořit hostovanou
.NET aplikaci, která zpracovává Flowable external jobs a předává je dál na HTTP endpoint.

## Spuštění

Aplikace je konzolový host založený na `Host.CreateApplicationBuilder`.
Registrace handlerů probíhá v [`Program.cs`](./Program.cs):

```csharp
builder.Services
    .AddHttpExternalTaskHandlerWorker(builder.Configuration)
    .AddHttpExternalTaskHandler2Worker(builder.Configuration);
```

Každý handler má vlastní konfigurační sekci v `appsettings.json`. Knihovna `AddHttpWorker`
načte nastavení a zaregistruje `FlowableExternalWorkerService<THandler>` s konkrétním handlerem.

## Konfigurace

Ukázková konfigurace pro `HttpExternalTaskHandler2`:

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

## Princip handleru

[`HttpExternalTaskHandler2`](./HttpExternalTaskHandler2.cs) připraví JSON payload z Flowable
proměnných a odešle jej na konfigurovaný HTTP endpoint. Na základě výsledku volání vrací proměnné,
nebo vyhazuje specifické výjimky, které ovládají reakci Flowable enginu:

- **Dočasné chyby** (timeout klienta, HTTP 5xx, síťové výpadky) vyvolají
  `FlowableJobRetryException`, aby Flowable úlohu retryoval.
- **Neopravitelné technické chyby** (např. 401/403) jsou nahlášeny jako incident přes
  `FlowableFinalFailureAction.Incident(...)` uvnitř `FlowableJobFinalException`.
- **Validované business chyby** – pokud backend vrátí `422` s JSONem obsahujícím
  `businessErrorCode`, handler vytvoří `FlowableFinalFailureAction.BpmnError`. Flowable
  pak aktivuje boundary error event a proces může pokračovat alternativní větví.

Každý typ chyb je v kódu doplněn komentářem, který popisuje typický scénář vyvolání.

## Přidání vlastního handleru

1. Implementujte třídu `IFlowableJobHandler` a zaregistrujte ji v DI pomocí
   `services.AddFlowableExternalWorker<THandler>(...)` nebo kopií metody `AddHttpWorker`.
2. Přidejte sekci do `appsettings.json` se jménem handleru.
3. V `Program.cs` zavolejte odpovídající extension metodu, která zaregistruje služby.

Handler má k dispozici `FlowableJobContext` s proměnnými z BPMN procesu, může tedy
připravit payload přesně podle potřeby. Výsledné proměnné se předávají do Flowable
při dokončení jobu (`acquire/jobs/{id}/complete`).
