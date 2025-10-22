# Flowable External Worker Library

Tento projekt zapouzdřuje veškerou infrastrukturu potřebnou pro psaní .NET workerů,
kteří zpracovávají Flowable external jobs. Balík poskytuje hotovou hostovanou službu
(`FlowableExternalWorkerService<THandler>`) a kontrakty pro vlastní implementace handlerů.

## Co knihovna řeší

- **Získávání úloh** – periodicky volá REST API (`external-job-api/acquire/jobs`) a respektuje
  nakonfigurovaná omezení (`MaxJobsPerTick`, `MaxDegreeOfParallelism`).
- **Řízení retry logiky** – `FlowableWorkerOptions.Retry` definuje exponenciální back-off,
  který se aplikuje na `FlowableJobRetryException`.
- **Finální selhání** – podle akce v `FlowableJobFinalException` knihovna volá Flowable REST API
  pro incident, dokončení nebo BPMN error.
- **Časová okna** – `TimeWindow` umožňuje worker pauzovat v definovaném časovém rozsahu.

Klíčové třídy najdete v:

- [`FlowableExternalWorkerService.cs`](./FlowableExternalWorkerService.cs)
- [`FlowableJobHandler.cs`](./FlowableJobHandler.cs) – kontrakty a výjimky
- [`FlowableWorkerOptions.cs`](./FlowableWorkerOptions.cs)
- [`ServiceCollectionExtensions.cs`](./ServiceCollectionExtensions.cs)

## Konfigurace

`FlowableWorkerOptions` nastavuje identifikaci workera, téma (`topic`), velikost
zámku (`lockDuration`) a další parametry. Vzorek konfigurace najdete v souboru
`src/FlowableHttpWorker/appsettings.json`.

```json
{
  "WorkerId": "example.worker",
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
}
```

`FlowableExternalWorkerService` získává instance `IFlowableJobHandler` přes DI, takže
handler může využívat další služby (HTTP klienty, databázi apod.).

## Životní cyklus jobu

1. Worker v metodě [`ExecuteAsync`](./FlowableExternalWorkerService.cs#L47-L127) pravidelně získává úlohy
   z REST API Flowable a předává je do [`ProcessJobAsync`](./FlowableExternalWorkerService.cs#L142-L177).
2. V `ProcessJobAsync` se výsledek z handleru vyhodnocuje podle typu výjimky:
   * úspěšné dokončení -> [`CompleteAsync`](./FlowableExternalWorkerService.cs#L179-L196)
   * `FlowableJobRetryException` -> [`HandleRetryAsync`](./FlowableExternalWorkerService.cs#L198-L235)
   * `FlowableJobFinalException` -> [`HandleFinalFailureAsync`](./FlowableExternalWorkerService.cs#L237-L259)
3. `HandleFinalFailureAsync` vyhodnotí, zda má úloha skončit jako incident, dokončení nebo BPMN error,
   a podle toho volá [`ThrowBpmnErrorAsync`](./FlowableExternalWorkerService.cs#L261-L274) či
   [`FailForIncidentAsync`](./FlowableExternalWorkerService.cs#L276-L289).

## Jak z handleru vyvolat BPMN error

* Implementace `IFlowableJobHandler.HandleAsync` by měla pro očekávané "business" chyby vyhodit
  `FlowableJobFinalException` s akcí `FlowableFinalFailureAction.BpmnError(...)`
  (viz [`FlowableJobHandler.cs`](./FlowableJobHandler.cs#L1-L74)).
* `FlowableFinalFailureAction` umožňuje připojit k BPMN erroru kód (`errorCode`), zprávu
  (`errorMessage`) a kolekci proměnných (`FlowableVariable`). Worker je v `ThrowBpmnErrorAsync`
  předá Flowable enginu, který je vloží do běhu procesu.

## Rozlišení typů chyb

* Dočasné chyby (timeouty, 5xx) signalizujte `FlowableJobRetryException`, což udržuje retry logiku
  (`HandleRetryAsync`).
* Neopravitelné technické chyby nahlašujte jako incident pomocí
  `FlowableFinalFailureAction.Incident(...)` v `FlowableJobFinalException`.
* Validované business chyby použijte pro `FlowableFinalFailureAction.BpmnError`, aby BPMN model přešel
  do větve s boundary error eventem.

### Ukázka handleru

Základní handler vrací výsledek přes `FlowableJobHandlerResult` a proměnné:

```csharp
public sealed class SampleHandler : IFlowableJobHandler
{
    public async Task<FlowableJobHandlerResult> HandleAsync(FlowableJobContext context, CancellationToken cancellationToken)
    {
        try
        {
            // ... call external system ...
            return new FlowableJobHandlerResult(new[]
            {
                new FlowableVariable("result", "OK", "string")
            });
        }
        catch (TimeoutException ex)
        {
            throw new FlowableJobRetryException("External call timed out", ex);
        }
        catch (KnownBusinessException ex)
        {
            throw new FlowableJobFinalException(
                FlowableFinalFailureAction.BpmnError("BUSINESS_VALIDATION", ex.Message),
                ex.Message,
                ex);
        }
        catch (Exception ex)
        {
            throw new FlowableJobFinalException(
                FlowableFinalFailureAction.Incident("Unhandled technical failure"),
                ex.Message,
                ex);
        }
    }
}
```

## Úprava BPMN modelu

* K servisní úloze (externímu tasku) přidejte boundary error event s kódem, který odpovídá
  `errorCode` z `FlowableFinalFailureAction.BpmnError`.
* Tok navazující na boundary event implementuje kompenzační nebo informační logiku očekávané
  business chyby.

## Audit a proměnné

* `FlowableExternalWorkerService` předává kontext do `IFlowableJobHandler.HandleFinalFailureAsync`,
  takže můžete logovat či ukládat metadata selhání na jednom místě.
* Doporučujeme do proměnných BPMN erroru doplnit diagnostické informace, které se pak zobrazí ve
  Flowable UI nebo poslouží dalším krokům procesu.
