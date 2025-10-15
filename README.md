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