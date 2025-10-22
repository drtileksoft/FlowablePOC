# Flowable External Worker – BPMN Error Handling

Tento dokument shrnuje, jak externí worker vyřizuje neúspěšné úlohy, jaké akce může handler spouštět a kdy použít BPMN error místo incidentu. Zároveň doplňuje odkazy na části kódu, kde se jednotlivé kroky implementují.

## Řídicí tok selhání

1. Worker v metodě [`ExecuteAsync`](./FlowableExternalWorkerService.cs#L47-L127) pravidelně získává úlohy z REST API Flowable a předává je do [`ProcessJobAsync`](./FlowableExternalWorkerService.cs#L142-L177).
2. V `ProcessJobAsync` se výsledek z handleru vyhodnocuje podle typu výjimky:
   * úspěšné dokončení -> [`CompleteAsync`](./FlowableExternalWorkerService.cs#L179-L196)
   * `FlowableJobRetryException` -> [`HandleRetryAsync`](./FlowableExternalWorkerService.cs#L198-L235)
   * `FlowableJobFinalException` -> [`HandleFinalFailureAsync`](./FlowableExternalWorkerService.cs#L237-L259)
3. `HandleFinalFailureAsync` vyhodnotí, zda má úloha skončit jako incident, dokončení nebo BPMN error, a podle toho volá [`ThrowBpmnErrorAsync`](./FlowableExternalWorkerService.cs#L261-L274) či [`FailForIncidentAsync`](./FlowableExternalWorkerService.cs#L276-L289).

## Jak z handleru vyvolat BPMN error

* Implementace `IFlowableJobHandler.HandleAsync` by měla pro očekávané "business" chyby vyhodit `FlowableJobFinalException` s akcí `FlowableFinalFailureAction.BpmnError(...)` (viz [`FlowableJobHandler.cs`](./FlowableJobHandler.cs#L1-L74)).
* `FlowableFinalFailureAction` umožňuje připojit k BPMN erroru kód (`errorCode`), zprávu (`errorMessage`) a kolekci proměnných (`FlowableVariable`). Worker je v `ThrowBpmnErrorAsync` předá Flowable enginu, který je vloží do běhu procesu.

## Rozlišení typů chyb

* Dočasné chyby (timeouty, 5xx) signalizujte `FlowableJobRetryException`, což udržuje retry logiku (`HandleRetryAsync`).
* Neopravitelné technické chyby nahlašujte jako incident pomocí `FlowableFinalFailureAction.Incident(...)` v `FlowableJobFinalException`.
* Validované business chyby použijte pro `FlowableFinalFailureAction.BpmnError`, aby BPMN model přešel do větve s boundary error eventem.

## Úprava BPMN modelu

* K servisní úloze (externímu tasku) přidejte boundary error event s kódem, který odpovídá `errorCode` z `FlowableFinalFailureAction.BpmnError`.
* Tok navazující na boundary event implementuje kompenzační nebo informační logiku očekávané business chyby.

## Audit a proměnné

* `FlowableExternalWorkerService` předává kontext do `IFlowableJobHandler.HandleFinalFailureAsync`, takže můžete logovat či ukládat metadata selhání na jednom místě.
* Doporučujeme do proměnných BPMN erroru doplnit diagnostické informace, které se pak zobrazí ve Flowable UI nebo poslouží dalším krokům procesu.

