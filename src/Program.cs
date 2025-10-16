using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// ---- Configuration: JSON + ENV ----
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// ---- Logging ----
var configuredLevel = builder.Configuration["Worker:LogLevel"];
var minLevel = Enum.TryParse<LogLevel>(configuredLevel, true, out var lvl) ? lvl : LogLevel.Debug;

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";
});
builder.Logging.SetMinimumLevel(minLevel);

// ---- Optional SSL skipping for DEV ----
bool allowInsecure = builder.Configuration.GetValue("AllowInsecureSsl", false);
HttpClientHandler? InsecureHandler()
    => allowInsecure ? new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true } : null;

// ---- HTTP clients ----
builder.Services.AddHttpClient("flowable", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigureHttpClient((sp, c) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["Flowable:BaseUrl"] ?? throw new("Flowable:BaseUrl missing");
    c.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

    var user = cfg["Flowable:User"] ?? throw new("Flowable:User missing");
    var pass = cfg["Flowable:Pass"] ?? throw new("Flowable:Pass missing");
    var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
    c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
})
.ConfigurePrimaryHttpMessageHandler(_ => InsecureHandler() ?? new HttpClientHandler());

builder.Services.AddHttpClient("srd", (sp, c) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    c.Timeout = TimeSpan.FromSeconds(int.Parse(cfg["SRD:HttpTimeoutSeconds"] ?? "10"));
})
.ConfigurePrimaryHttpMessageHandler(_ => InsecureHandler() ?? new HttpClientHandler());

// ---- Hosted service orchestration ----
builder.Services.Configure<HostOptions>(options =>
{
    options.ServicesStartConcurrently = true;
    options.ServicesStopConcurrently = true;
});

// ---- Workers ----
var workerOptions = builder.Configuration
    .GetSection("Flowable:Workers")
    .Get<List<WorkerOptions>>()
    ?? new List<WorkerOptions>();

if (workerOptions.Count == 0)
{
    throw new InvalidOperationException("No workers configured. Please define Flowable:Workers in configuration.");
}

foreach (var options in workerOptions)
{
    builder.Services.AddSingleton<IHostedService>(sp =>
        ActivatorUtilities.CreateInstance<ExternalWorkerService>(sp, options));
}

await builder.Build().RunAsync();

public sealed class ExternalWorkerService : BackgroundService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<ExternalWorkerService> _log;
    private readonly WorkerOptions _options;

    public ExternalWorkerService(
        WorkerOptions options,
        IHttpClientFactory http,
        IConfiguration cfg,
        ILogger<ExternalWorkerService> log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    // DTOs pro external-job-api
    private static readonly JsonSerializerOptions OutgoingJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private record AcquireRequest(string workerId, int maxJobs, string lockDuration, string topic, bool fetchVariables = true);
    private record Variable(string name, object value, string type);


    /*
     * [
  {
    "id": "196c5fa3-aa7a-11f0-b5fc-2ef252b3fdd9",
    "url": "http://localhost:8090/flowable-rest/external-job-api/jobs/196c5fa3-aa7a-11f0-b5fc-2ef252b3fdd9",
    "correlationId": "196c5fa2-aa7a-11f0-b5fc-2ef252b3fdd9",
    "processInstanceId": "195ccf39-aa7a-11f0-b5fc-2ef252b3fdd9",
    "processDefinitionId": "srdProcess:1:7122b52a-aa79-11f0-b5fc-2ef252b3fdd9",
    "executionId": "195ccf3a-aa7a-11f0-b5fc-2ef252b3fdd9",
    "scopeId": null,
    "subScopeId": null,
    "scopeDefinitionId": null,
    "scopeType": null,
    "elementId": "externalTask",
    "elementName": "External SRD Task",
    "retries": 3,
    "exceptionMessage": null,
    "dueDate": null,
    "createTime": "2025-10-16T10:23:03.775Z",
    "tenantId": "",
    "lockOwner": "flowable-http-worker-1",
    "lockExpirationTime": "2025-10-16T10:26:18.823Z",
    "variables": []
  }
]
     */

    private record AcquiredJob(
    string id,
    string url,
    string correlationId,
    string processInstanceId,
    string processDefinitionId,
    string executionId,
    string? scopeId,
    string? subScopeId,
    string? scopeDefinitionId,
    string? scopeType,
    string elementId,
    string elementName,
    int retries,
    string? exceptionMessage,
    string? dueDate,              // ISO8601 nebo null (např. "2025-10-16T10:23:03.775Z")
    string createTime,            // ISO8601 (např. "2025-10-16T10:23:03.775Z")
    string tenantId,
    string? lockOwner,
    string lockExpirationTime,    // ISO8601 (např. "2025-10-16T10:26:18.823Z")
    List<Variable>? variables
)
    {
        // Pohodlný převod proměnných jobu na dictionary
        public Dictionary<string, object> VariablesAsDictionary =>
            variables?.ToDictionary(v => v.name, v => v.value) ?? new();

        // Volitelné: parsované časy (když se hodí)
        public DateTimeOffset? CreateTimeParsed =>
            DateTimeOffset.TryParse(createTime, out var dto) ? dto : null;

        public DateTimeOffset? LockExpirationParsed =>
            DateTimeOffset.TryParse(lockExpirationTime, out var dto) ? dto : null;

        public DateTimeOffset? DueDateParsed =>
            !string.IsNullOrWhiteSpace(dueDate) && DateTimeOffset.TryParse(dueDate, out var dto) ? dto : null;

        // Je (ještě) zamknutý?
        public bool IsLocked(DateTimeOffset nowUtc) =>
            LockExpirationParsed is { } exp && exp > nowUtc;
    }

    private sealed record CompleteVariable(string name, object? value, string? type = null);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // --- Nastavení ---
        var tz = TimeZoneInfo.FindSystemTimeZoneById(_cfg["Windows:Timezone"] ?? "Europe/Prague");
        int pauseFrom = int.Parse(_cfg["Windows:PauseFromHour"] ?? "14");
        int pauseToExcl = int.Parse(_cfg["Windows:PauseToHourExclusive"] ?? "15");

        var topic = _options.Topic ?? _cfg["Flowable:Topic"] ?? "srd.call";
        var workerId = _options.WorkerId ?? _cfg["Flowable:WorkerId"] ?? "srd-worker-1";
        var lockDuration = _options.LockDuration ?? _cfg["Flowable:LockDuration"] ?? "PT30S";
        int maxJobs = _options.MaxJobsPerTick ?? int.Parse(_cfg["Flowable:MaxJobsPerTick"] ?? "5");
        int pollSec = _options.PollPeriodSeconds ?? int.Parse(_cfg["Flowable:PollPeriodSeconds"] ?? "3");
        int mdop = _options.MaxDegreeOfParallelism ?? int.Parse(_cfg["Flowable:MaxDegreeOfParallelism"] ?? "2");

        var srdUrl = _options.TargetUrl ?? _cfg["SRD:Url"] ?? throw new("SRD:Url missing");
        bool insecure = _cfg.GetValue("AllowInsecureSsl", false);

        var flowable = _http.CreateClient("flowable");
        var srd = _http.CreateClient("srd");
        var rnd = new Random();
        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Startup log (bez hesel)
        _log.LogInformation("Worker starting. topic={Topic} mdop={MDOP} maxJobs/tick={MaxJobs} poll={Poll}s lock={Lock} timeZone={TZ} pause={PauseFrom}-{PauseTo} srdUrl={SrdUrl} insecureSsl={Insecure} logLevel={Level}",
            topic, mdop, maxJobs, pollSec, lockDuration, tz.Id, pauseFrom, pauseToExcl, srdUrl, insecure, GetMinLogLevel());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
                var hour = nowLocal.Hour;

                // Pauza v okně
                if (hour >= pauseFrom && hour < pauseToExcl)
                {
                    _log.LogInformation("Paused by window {From}-{To} localTime={Now}", pauseFrom, pauseToExcl, nowLocal.ToString("HH:mm:ss"));
                    await Task.Delay(TimeSpan.FromSeconds(pollSec), stoppingToken);
                    continue;
                }

                // Acquire
                var req = new AcquireRequest(workerId, maxJobs, lockDuration, topic, fetchVariables: true);
                var json = JsonSerializer.Serialize(req);
                _log.LogDebug("Acquire request: {Json}", json);
                using var acqBody = new StringContent(json, Encoding.UTF8, "application/json");

                var swAcquire = Stopwatch.StartNew();
                using var acqResp = await flowable.PostAsync("acquire/jobs", acqBody, stoppingToken);
                swAcquire.Stop();

                if (!acqResp.IsSuccessStatusCode)
                {
                    var errorContent = await acqResp.Content.ReadAsStringAsync(stoppingToken);
                    _log.LogWarning("Acquire failed: status={StatusCode} elapsedMs={Elapsed} response={Error}", (int)acqResp.StatusCode, swAcquire.ElapsedMilliseconds, errorContent);
                    await Task.Delay(TimeSpan.FromSeconds(pollSec), stoppingToken);
                    continue;
                }

                var text = await acqResp.Content.ReadAsStringAsync(stoppingToken);
                _log.LogInformation("Acquire response: {Response}", text);
                /*
                flowable-http-worker-1  | 2025-10-14 18:04:01.743 +02:00 info: ExternalWorkerService[0] Acquire response: [{"id":"657d0784-a917-11f0-ac2c-0242ac120004","url":"http://flowable-rest:8080/flowable-rest/external-job-api/jobs/657d0784-a917-11f0-ac2c-0242ac120004","correlationId":"657d0783-a917-11f0-ac2c-0242ac120004","processInstanceId":"65790fda-a917-11f0-ac2c-0242ac120004","processDefinitionId":"srdProcess:1:1844f756-a90c-11f0-a192-0242ac120004","executionId":"65790fdb-a917-11f0-ac2c-0242ac120004","scopeId":null,"subScopeId":null,"scopeDefinitionId":null,"scopeType":null,"elementId":"externalTask","elementName":"External SRD Task","retries":3,"exceptionMessage":null,"dueDate":null,"createTime":"2025-10-14T16:04:00.052Z","tenantId":"","lockOwner":"flowable-http-worker-1","lockExpirationTime":"2025-10-14T16:04:31.731Z","variables":[]}]
                */
                var jobs = JsonSerializer.Deserialize<List<AcquiredJob>>(text, jsonOpts) ?? new();

                _log.LogInformation("Acquire OK: jobs={Count} elapsedMs={Elapsed}", jobs.Count, swAcquire.ElapsedMilliseconds);

                if (jobs.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollSec), stoppingToken);
                    continue;
                }

                // Zpracování s omezeným paralelismem
                using var throttler = new SemaphoreSlim(mdop);
                var tasks = jobs.Select(job => ProcessJob(job, throttler, flowable, srd, workerId, srdUrl, rnd, stoppingToken)).ToList();
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _log.LogInformation("Cancellation requested. Stopping worker loop.");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Worker loop error: {Message}", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSec), stoppingToken);
        }

        _log.LogInformation("Worker stopped.");
    }

    private async Task ProcessJob(
        AcquiredJob job,
        SemaphoreSlim throttler,
        HttpClient flowable,
        HttpClient srd,
        string workerId,
        string srdUrl,
        Random rnd,
        CancellationToken ct)
    {
        await throttler.WaitAsync(ct);
        using var scope = _log.BeginScope(new Dictionary<string, object?>
        {
            ["jobId"] = job.id,
            ["pi"] = job.processInstanceId,
            ["exec"] = job.executionId
        });

        try
        {
            var clientTs = DateTimeOffset.Now.ToString("o");
            object? forwarded = null;
            if (job.VariablesAsDictionary.TryGetValue("JsonPayload", out var forwardedValue))
            {
                forwarded = forwardedValue;
            }

            var payload = new
            {
                id = workerId,
                clientTs,
                data = new
                {
                    note = "external-worker",
                    jobId = job.id,
                    pi = job.processInstanceId,
                    exec = job.executionId,
                    variables = job.VariablesAsDictionary,
                    forwardedResult = forwarded
                }
            };

            var sw = Stopwatch.StartNew();
            using var content = new StringContent(JsonSerializer.Serialize(payload, OutgoingJsonOptions), Encoding.UTF8, "application/json");
            _log.LogInformation("Calling SRD... url={Url}", srdUrl);

            using var resp = await srd.PostAsync(srdUrl, content, ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                var backoff = BackoffSeconds(rnd);
                _log.LogWarning("SRD call failed: status={Status} elapsedMs={Elapsed} backoff={Backoff}s", (int)resp.StatusCode, sw.ElapsedMilliseconds, backoff);
                await FailWithRetry(flowable, job.id, workerId, backoff, ct);
                return;
            }

            _log.LogInformation("SRD call OK: status={Status} elapsedMs={Elapsed}", (int)resp.StatusCode, sw.ElapsedMilliseconds);

            var responseBody = await resp.Content.ReadAsStringAsync(ct);

            var elementIdentifier = job.elementId!;
            var contentType = resp.Content.Headers.ContentType?.MediaType;
            var trimmed = responseBody.Trim();

            var responseType = "string";
            object responseValue = responseBody;
            JsonElement? parsedJson = null;

            if (!string.IsNullOrEmpty(responseBody))
            {
                if (IsJsonContentType(contentType) || LooksLikeJson(trimmed))
                {
                    if (TryParseJson(responseBody, out var jsonElement))
                    {
                        responseType = "json";
                        responseValue = jsonElement;
                        parsedJson = jsonElement;
                    }
                }
                else if (IsXmlContentType(contentType) || LooksLikeXml(trimmed))
                {
                    responseType = "xml";
                }
            }

            var headersDict = resp.Headers
                .Select(h => (h.Key, Values: h.Value))
                .Concat(resp.Content.Headers.Select(h => (h.Key, Values: h.Value)))
                .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.SelectMany(v => v.Values).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            var variableList = new List<CompleteVariable>
            {
                new($"{elementIdentifier}_statusCode", (int)resp.StatusCode, "integer"),
                new($"{elementIdentifier}_response_type", responseType, "string"),
                new($"{elementIdentifier}_response", responseValue, responseType == "json" ? "json" : "string"),
                new($"{elementIdentifier}_headers", headersDict, "json"),

                new($"JsonResponsePayload", responseValue, responseType == "json" ? "json" : "string"),
            };

            var completePayload = new
            {
                workerId,
                variables = variableList
            };

            using var completeContent = new StringContent(JsonSerializer.Serialize(completePayload, OutgoingJsonOptions), Encoding.UTF8, "application/json");
            var swComplete = Stopwatch.StartNew();
            using var completeResp = await flowable.PostAsync($"acquire/jobs/{job.id}/complete", completeContent, ct);
            swComplete.Stop();
            completeResp.EnsureSuccessStatusCode();

            _log.LogInformation("Job completed in {Elapsed} ms (complete call {CompleteMs} ms)", sw.ElapsedMilliseconds, swComplete.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogWarning("Job cancelled.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Local error while processing job. Will fail+retry with 60s.");
            try
            {
                await FailWithRetry(flowable, job.id, workerId, 60, ct);
            }
            catch (Exception ex2)
            {
                _log.LogError(ex2, "FailWithRetry also failed.");
            }
        }
        finally
        {
            throttler.Release();
        }
    }

    private int BackoffSeconds(Random rnd)
    {
        var initial = int.Parse(_cfg["Retry:InitialDelaySeconds"] ?? "60");
        var max = int.Parse(_cfg["Retry:MaxDelaySeconds"] ?? "900");
        var jitter = int.Parse(_cfg["Retry:JitterSeconds"] ?? "5");
        var next = Math.Min(max, initial * 2); // jednoduchý 2x backoff pro demo
        return next + rnd.Next(0, jitter + 1);
    }

    private async Task FailWithRetry(HttpClient flowable, string jobId, string workerId, int retryHintSeconds, CancellationToken ct)
    {
        var payload = new
        {
            workerId = workerId,
            retries = 1,
            retryTimeout = $"PT{retryHintSeconds}S",
            errorMessage = "SRD call failed"
        };
        using var cnt = new StringContent(JsonSerializer.Serialize(payload, OutgoingJsonOptions), Encoding.UTF8, "application/json");
        using var resp = await flowable.PostAsync($"acquire/jobs/{jobId}/fail", cnt, ct);
        _log.LogInformation("Job failed with retry timeout {Retry}s => status={Status}", retryHintSeconds, (int)resp.StatusCode);
    }

    private static bool TryParseJson(string input, out JsonElement element)
    {
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(input);
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private static bool LooksLikeJson(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        var firstChar = input.FirstOrDefault();
        return firstChar is '{' or '[';
    }

    private static bool LooksLikeXml(string input)
        => !string.IsNullOrEmpty(input) && input.StartsWith('<');

    private static bool IsJsonContentType(string? contentType)
        => !string.IsNullOrEmpty(contentType) && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static bool IsXmlContentType(string? contentType)
        => !string.IsNullOrEmpty(contentType) && contentType.Contains("xml", StringComparison.OrdinalIgnoreCase);

    private LogLevel GetMinLogLevel()
        => (LogLevel)Enum.Parse(typeof(LogLevel), Environment.GetEnvironmentVariable("WORKER__LOGLEVEL") ?? "Information", true);
}

public sealed record WorkerOptions
{
    public string? Topic { get; init; }
    public string? WorkerId { get; init; }
    public string? LockDuration { get; init; }
    public int? MaxJobsPerTick { get; init; }
    public int? PollPeriodSeconds { get; init; }
    public int? MaxDegreeOfParallelism { get; init; }
    public string? TargetUrl { get; init; }

}
