# Flowable Proof of Concept

Tento repozitář obsahuje kompletní playground pro Flowable external workery. Docker
Compose sestava spouští Flowable REST engine společně se vzorovými .NET workery,
které demonstrují integrační vzory popsané v projektech ve složce `src`.

## Struktura repozitáře

| Cesta | Popis |
| --- | --- |
| `src/AMCSSZ.NWF.Shared.ExternalFlowableWorker` | Znovupoužitelná knihovna, která obaluje REST API pro Flowable external joby a zajišťuje polling, retry i zpracování chyb pro vlastní handlery. |
| `src/AMCSSZ.NWF.Shared.ExternalFlowableWorkerImplementations` | Kolekce hotových implementací workerů (HTTP handlery, pomocné utility a DI extension metody). |
| `src/AMCSSZ.NWF.Shared.FlowableHttpWorkerUsageExample` | Konzolový host, který referencuje balíček implementací a demonstruje registraci HTTP handlerů. |
| `src/AMCSSZ.NWF.Shared.HttpLogApi` | Minimalistické ASP.NET Core API používané jako cílový endpoint při testování HTTP workerů. |
| `srd-process.bpmn20.xml` | Ukázkový BPMN model, který spouští external task topic konzumovaný workery. |

Každý projekt má vlastní README s detailnějšími informacemi a příklady konfigurace.

## Spuštění sestavy lokálně

Postavte kontejnery a spusťte je na pozadí:

```bash
docker compose up -d --build
```

Během vývoje sledujte logy workeru:

```bash
docker compose logs -f flowable-http-worker-usage-example
```

Užitečné příkazy pro životní cyklus:

```bash
# zastavení/spuštění/restart pouze worker kontejneru
docker compose stop flowable-http-worker-usage-example
docker compose start flowable-http-worker-usage-example
docker compose restart flowable-http-worker-usage-example

# zastavení celé sestavy
docker compose down

# odstranění perzistentního svazku PostgreSQL (smaže data!)
docker volume rm flowableworker_dbdata

# zastavení všeho a odstranění svazků v jednom kroku
docker compose down -v
```

## Diagnostika Flowable REST engine

Sledujte logy kontejneru Flowable REST:

```bash
docker compose logs -f flowable-rest
```

Ověřte verzi enginu publikovanou vzdáleným sandboxem:

```bash
curl -X GET --header 'Accept: application/json' 'https://flowable-pokusy-rest.evidencz.dev/flowable-rest/service/management/engine'
```

Otevřete automaticky generovanou Swagger dokumentaci REST API:

```
http://localhost:8090/flowable-rest/docs/?url=specfile/external-worker/flowable-swagger-external-worker.json#/Acquire_and_Execute
```

## Nasazení a testování ukázkového procesu

Nasaďte BPMN model přiložený v tomto repozitáři:

```bash
curl -u rest-admin:test -X POST http://localhost:8090/flowable-rest/service/repository/deployments -H "Content-Type: multipart/form-data" -F "file=@srd-process.bpmn20.xml"
```

Ověřte nasazení a vypište definice:

```bash
curl -u rest-admin:test http://localhost:8090/flowable-rest/service/repository/process-definitions?key=srdProcess
curl -u rest-admin:test http://localhost:8090/flowable-rest/service/repository/process-definitions
```

Spusťte ruční získání external jobů a zjistěte, co by worker obdržel:

```bash
curl -u rest-admin:test -X POST \
  http://localhost:8090/flowable-rest/external-job-api/acquire/jobs \
  -H "Content-Type: application/json" \
  -d '{"workerId":"test","maxJobs":5,"lockDuration":"PT30S","topic":"srd.call","fetchVariables":true}'
```

Vypište dokončené instance ukázkového procesu:

```bash
curl -u rest-admin:test "http://localhost:8090/flowable-rest/service/runtime/process-instances?processDefinitionKey=srdProcess&finished=true"
```

Prohlédněte si HTTP log API, které funguje jako downstream služba:

```bash
curl -X GET "https://httplogapi.azurewebsites.net/api/events/FLOWABLE_POC_WORKER?from=2025-10-13T22:00:00Z&to=2025-10-14T21:59:59Z"
```

## Tipy pro modelování

Flowable čeká na dokončení každého external service tasku pomocí endpointu `/acquire/jobs/{id}/complete`, než přejde na další uzel. Můžete tedy v BPMN modelu řetězit externí úlohy a engine druhou úlohu načte až ve chvíli, kdy worker potvrdí dokončení první.

```xml
<serviceTask id="externalTask" name="Externí SRD úloha"
             flowable:type="external"
             flowable:topic="srd.call"
             flowable:async="true" />
<serviceTask id="externalTask2" name="Externí SRD úloha 2"
             flowable:type="external"
             flowable:topic="srd.call"
             flowable:async="true" />
```

Pro dlouhotrvající nebo asynchronní práci downstream služeb použijte gateway, která se opakuje, dokud worker nenastaví příznak dokončení pomocí procesních proměnných:

```xml
<serviceTask id="externalTask" ... />
<exclusiveGateway id="waitForWorker" />
<sequenceFlow sourceRef="waitForWorker" targetRef="externalTask" >
  <conditionExpression xsi:type="tFormalExpression">
    <![CDATA[${srdStatus != 'OK'}]]>
  </conditionExpression>
</sequenceFlow>
<sequenceFlow sourceRef="waitForWorker" targetRef="externalTask2" >
  <conditionExpression xsi:type="tFormalExpression">
    <![CDATA[${srdStatus == 'OK'}]]>
  </conditionExpression>
</sequenceFlow>
```

Vzorový worker v `src/AMCSSZ.NWF.Shared.FlowableHttpWorkerUsageExample/Program.cs` již získané joby dokončuje. Pokud handler vyhodí `FlowableJobRetryException`, worker zavolá `acquire/jobs/{job.id}/fail` a umožní Flowable opakovat stejný task podle nakonfigurované back-off strategie.
