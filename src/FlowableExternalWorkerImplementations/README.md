# Flowable External Worker Implementations

Tento projekt obsahuje sdílené implementace Flowable external workerů, které lze využít
v libovolném hostu. Primárně jej používá ukázková konzolová aplikace
[`FlowableHttpWorker`](../FlowableHttpWorker/README.md).

## Struktura projektu

- `Http/` – obsahuje samotné handler implementace pro HTTP integraci
  - `HttpExternalTaskHandler` – jednoduchý HTTP worker, který volá zadaný endpoint
    a vrací odpověď zpět do Flowable.
  - `HttpExternalTaskHandler2` – rozšířená varianta s podporou business chyb,
    incidentů a parsování odpovědi.
  - `HttpExternalWorkerRegistrationExtensions` – extension metody pro snadnou
    registraci HTTP workerů do DI.
  - `HttpTaskEndpointOptions` – konfigurační třída pro HTTP endpoint.
- `Helpers/` – společné pomocné utility
  - `HttpResponseContentInspector` – pomocné metody pro práci s HTTP odpověďmi
    (detekce JSON/XML, parsování, převod hlaviček).
  - `JsonContentNavigator` – utility pro robustní práci se strukturami JSON
    (dekódování, vyhledávání, navigace pomocí cesty).

## Použití v host aplikaci

1. Přidejte referenci na projekt `FlowableExternalWorkerImplementations`.
2. V registraci služeb zavolejte jednu z extension metod, např.

   ```csharp
   services.AddHttpExternalTaskHandler2Worker(configuration);
   ```

3. Přidejte odpovídající sekci do konfigurace (viz ukázka v README host projektu).

Každý handler je standardní implementace `IFlowableJobHandler`, takže jej lze
použít i mimo ukázkový host nebo kombinovat s dalšími vlastními worker implementacemi.
