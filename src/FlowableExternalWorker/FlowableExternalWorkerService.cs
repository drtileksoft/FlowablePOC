using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AMCSSZ.NWF.Shared.ExternalFlowableWorker;

public sealed class FlowableExternalWorkerService<THandler> : BackgroundService
    where THandler : IFlowableJobHandler
{
    

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FlowableExternalWorkerService<THandler>> _logger;
    private readonly FlowableWorkerOptions _options;
    private readonly THandler _handler;

    public FlowableExternalWorkerService(
        IHttpClientFactory httpClientFactory,
        ILogger<FlowableExternalWorkerService<THandler>> logger,
        FlowableWorkerOptions options,
        THandler handler)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        if (string.IsNullOrWhiteSpace(_options.Topic))
        {
            throw new ArgumentException("Topic must be provided", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(_options.WorkerId))
        {
            throw new ArgumentException("WorkerId must be provided", nameof(options));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var flowableClient = _httpClientFactory.CreateClient(FlowableClientOptions.DefaultFlowableHttpClientName);
        var topic = _options.Topic;
        var workerId = _options.WorkerId;
        var lockDuration = _options.LockDuration;
        var maxJobs = _options.MaxJobsPerTick;
        var pollSeconds = _options.PollPeriodSeconds;
        var mdop = _options.MaxDegreeOfParallelism;
        var timeZoneId = _options.TimeWindow.TimeZoneId;
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        _logger.LogInformation(
            "Worker starting topic={Topic} worker={WorkerId} poll={Poll}s mdop={Mdop} maxJobs={MaxJobs} lock={Lock}",
            topic, workerId, pollSeconds, mdop, maxJobs, lockDuration);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (ShouldPause(tz))
                {
                    _logger.LogInformation($"Worker {workerId} paused due to configured window");
                    await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
                    continue;
                }

                var acquireRequest = new FlowableAcquireRequest(workerId, maxJobs, lockDuration, topic, true);
                using var requestContent = JsonContent.Create(acquireRequest, options: SerializerOptions);

                var swAcquire = Stopwatch.StartNew();
                using var acquireResponse = await flowableClient.PostAsync("acquire/jobs", requestContent, stoppingToken);
                swAcquire.Stop();

                if (!acquireResponse.IsSuccessStatusCode)
                {
                    var error = await acquireResponse.Content.ReadAsStringAsync(stoppingToken);
                    _logger.LogWarning(
                        "Acquire failed status={Status} elapsedMs={Elapsed} response={Response} worker={WorkerId}",
                        (int)acquireResponse.StatusCode,
                        swAcquire.ElapsedMilliseconds,
                        error,
                        workerId);
                    await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
                    continue;
                }

                var payload = await acquireResponse.Content.ReadAsStringAsync(stoppingToken);
                var jobs = JsonSerializer.Deserialize<List<FlowableJob>>(payload, SerializerOptions) ?? new();

                _logger.LogInformation(
                    "Acquire ok jobs={Count} elapsedMs={Elapsed} worker={WorkerId}",
                    jobs.Count,
                    swAcquire.ElapsedMilliseconds,
                    workerId);

                if (jobs.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
                    continue;
                }

                using var throttler = new SemaphoreSlim(mdop);
                var tasks = jobs.Select(job => ProcessJobAsync(flowableClient, job, throttler, stoppingToken)).ToList();
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Cancellation requested. Stopping worker loop.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in worker loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
        }

        _logger.LogInformation($"Worker {workerId} stopped");
    }

    private bool ShouldPause(TimeZoneInfo tz)
    {
        var window = _options.TimeWindow;
        if (window.PauseFromHour is null || window.PauseToHourExclusive is null)
        {
            return false;
        }

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var hour = nowLocal.Hour;
        return hour >= window.PauseFromHour && hour < window.PauseToHourExclusive;
    }

    private async Task ProcessJobAsync(HttpClient flowableClient, FlowableJob job, SemaphoreSlim throttler, CancellationToken ct)
    {
        await throttler.WaitAsync(ct);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["jobId"] = job.Id,
            ["processInstance"] = job.ProcessInstanceId,
            ["execution"] = job.ExecutionId
        });

        try
        {
            var context = new FlowableJobContext(job, job.VariablesAsDictionary);
            var result = await _handler.HandleAsync(context, ct);
            await CompleteAsync(flowableClient, job.Id, _options.WorkerId, result, ct);
            _logger.LogInformation($"Job completed worker={_options.WorkerId}");
        }
        catch (FlowableJobRetryException retry)
        {
            await HandleRetryAsync(flowableClient, job, retry, ct);
        }
        catch (FlowableJobFinalException final)
        {
            await HandleFinalFailureAsync(flowableClient, job, final.Action, final, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception while processing job");
            await HandleRetryAsync(flowableClient, job, new FlowableJobRetryException(ex.Message, ex), ct);
        }
        finally
        {
            throttler.Release();
        }
    }

    private async Task CompleteAsync(
        HttpClient client,
        string jobId,
        string workerId,
        FlowableJobHandlerResult result,
        CancellationToken ct)
    {
        var payload = new
        {
            workerId,
            variables = result.Variables,
            localVariables = result.LocalVariables
        };

        using var content = JsonContent.Create(payload, options: SerializerOptions);
        using var response = await client.PostAsync($"acquire/jobs/{jobId}/complete", content, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task HandleRetryAsync(HttpClient client, FlowableJob job, FlowableJobRetryException retry, CancellationToken ct)
    {
        var remaining = job.Retries - 1;
        if (remaining <= 0)
        {
            _logger.LogWarning("Job {JobId} reached max retries. Triggering final failure.", job.Id);
            await HandleFinalFailureAsync(client, job, FlowableFinalFailureAction.Incident(retry.Message), retry, ct);
            return;
        }

        var timeout = retry.RetryAfter ?? ComputeBackoff(job);
        var payload = new
        {
            workerId = _options.WorkerId,
            retries = remaining,
            retryTimeout = FormatIsoDuration(timeout),
            errorMessage = retry.Message
        };

        using var content = JsonContent.Create(payload, options: SerializerOptions);
        using var response = await client.PostAsync($"acquire/jobs/{job.Id}/fail", content, ct);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Job {JobId} failed. retriesLeft={Retries} backoff={Backoff}s",
                job.Id,
                remaining,
                (int)timeout.TotalSeconds);
            return;
        }

        var error = await response.Content.ReadAsStringAsync(ct);
        _logger.LogError(
            "Fail request failed status={Status} response={Response}",
            (int)response.StatusCode,
            error);
    }

    private async Task HandleFinalFailureAsync(
        HttpClient client,
        FlowableJob job,
        FlowableFinalFailureAction action,
        Exception exception,
        CancellationToken ct)
    {
        var context = new FlowableJobContext(job, job.VariablesAsDictionary);
        await _handler.HandleFinalFailureAsync(context, exception, action, ct);

        switch (action.ActionType)
        {
            case FlowableFinalFailureActionType.Complete:
                await CompleteAsync(client, job.Id, _options.WorkerId, new FlowableJobHandlerResult(action.Variables ?? Array.Empty<FlowableVariable>()), ct);
                break;
            case FlowableFinalFailureActionType.BpmnError:
                await ThrowBpmnErrorAsync(client, job.Id, action, ct);
                break;
            default:
                await FailForIncidentAsync(client, job.Id, exception.Message, ct);
                break;
        }
    }

    private async Task ThrowBpmnErrorAsync(HttpClient client, string jobId, FlowableFinalFailureAction action, CancellationToken ct)
    {
        var payload = new
        {
            workerId = _options.WorkerId,
            errorCode = action.ErrorCode,
            errorMessage = action.ErrorMessage,
            variables = action.Variables
        };

        using var content = JsonContent.Create(payload, options: SerializerOptions);
        using var response = await client.PostAsync($"acquire/jobs/{jobId}/bpmnError", content, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task FailForIncidentAsync(HttpClient client, string jobId, string? message, CancellationToken ct)
    {
        var payload = new
        {
            workerId = _options.WorkerId,
            retries = 0,
            retryTimeout = FormatIsoDuration(TimeSpan.FromSeconds(_options.Retry.InitialDelaySeconds)),
            errorMessage = message ?? "Maximum retries reached"
        };

        using var content = JsonContent.Create(payload, options: SerializerOptions);
        using var response = await client.PostAsync($"acquire/jobs/{jobId}/fail", content, ct);
        response.EnsureSuccessStatusCode();
    }

    private TimeSpan ComputeBackoff(FlowableJob job)
    {
        var retry = _options.Retry;
        var attempt = Math.Max(0, _options.InitialRetries - job.Retries);
        var baseDelay = retry.InitialDelaySeconds * Math.Pow(retry.BackoffMultiplier, attempt);
        var bounded = Math.Min(retry.MaxDelaySeconds, (int)Math.Round(baseDelay, MidpointRounding.AwayFromZero));
        var jitter = retry.JitterSeconds > 0
            ? Random.Shared.Next(0, retry.JitterSeconds + 1)
            : 0;
        var seconds = Math.Min(retry.MaxDelaySeconds, bounded + jitter);
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    private static string FormatIsoDuration(TimeSpan timeout)
        => $"PT{Math.Max(1, (int)timeout.TotalSeconds)}S";
}
