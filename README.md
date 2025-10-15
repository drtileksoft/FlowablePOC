docker compose up -d --build
docker compose logs -f flowable-http-worker
docker compose stop flowable-http-worker
docker compose start flowable-http-worker
docker compose restart flowable-http-worker

# vypnout všechno v projektu
docker compose down
# smazat data
docker volume rm flowableworker_dbdata

# vypnout + zahodit svázované volume (pozor na data!)
docker compose down -v

# logy flowable
docker compose logs -f flowable-rest

# logy sluzby
docker compose logs -f flowable-http-worker

# verify jobs working
curl -u rest-admin:test -X POST http://localhost:8090/flowable-rest/external-job-api/acquire/jobs -H "Content-Type: application/json" -d '{"workerId":"test","maxJobs":5,"lockDuration":"PT30S","topic":"srd.call","fetchVariables":true}'

# deploy process
curl -u rest-admin:test -X POST http://localhost:8090/flowable-rest/service/repository/deployments -H "Content-Type: multipart/form-data" -F "file=@srd-process.bpmn20.xml"

{"id":"17e91bb4-a90c-11f0-a192-0242ac120004","name":"srd-process","deploymentTime":"2025-10-14T14:43:05.434Z","category":null,"parentDeploymentId":"17e91bb4-a90c-11f0-a192-0242ac120004","url":"http://localhost:8090/flowable-rest/service/repository/deployments/17e91bb4-a90c-11f0-a192-0242ac120004","tenantId":""}

# verify
curl -u rest-admin:test http://localhost:8090/flowable-rest/service/repository/process-definitions?key=srdProcess

# list events
curl -X GET "https://httplogapi.azurewebsites.net/api/events/FLOWABLE_POC_WORKER?from=2025-10-13T22:00:00Z&to=2025-10-14T21:59:59Z"

# list process defs
curl -u rest-admin:test http://localhost:8090/flowable-rest/service/repository/process-definitions

# finished
curl -u rest-admin:test "http://localhost:8090/flowable-rest/service/runtime/process-instances?processDefinitionKey=srdProcess&finished=true"

# swagger
http://localhost:8090/flowable-rest/docs/?url=specfile/external-worker/flowable-swagger-external-worker.json#/Acquire_and_Execute
## External worker sequencing example

Flowable waits for an external service task to be completed (via the `/acquire/jobs/{id}/complete` REST call) before it continues to the next step in the process. That means you can model two consecutive external tasks and the second one will only be acquired after the worker finished the first one successfully.

```xml
<serviceTask id="externalTask" name="External SRD Task"
             flowable:type="external"
             flowable:topic="srd.call"
             flowable:async="true" />
<serviceTask id="externalTask2" name="External SRD Task 2"
             flowable:type="external"
             flowable:topic="srd.call"
             flowable:async="true" />
```

If you need to model explicit waiting (for example when the worker calls an external system asynchronously and you want to resume the process later), you can set a process variable when completing the job and use an exclusive gateway or intermediate catching event to loop until the flag indicates that the work is finished.

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

The worker sample in `src/Program.cs` already calls `acquire/jobs/{job.id}/complete`, which is exactly what Flowable requires in order to move the execution token past the external task. If the worker fails instead, calling `acquire/jobs/{job.id}/fail` will release the lock and allow the engine to retry the same task.
