using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using Flowable.ExternalWorker;
using FlowableHttpWorker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

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

bool allowInsecure = builder.Configuration.GetValue("AllowInsecureSsl", false);
HttpClientHandler? InsecureHandler()
    => allowInsecure ? new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true } : null;

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

builder.Services.Configure<HostOptions>(options =>
{
    options.ServicesStartConcurrently = true;
    options.ServicesStopConcurrently = true;
});

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
    {
        var flowableOptions = new FlowableWorkerOptions
        {
            Topic = options.Topic ?? builder.Configuration["Flowable:Topic"] ?? "srd.call",
            WorkerId = options.WorkerId ?? builder.Configuration["Flowable:WorkerId"] ?? "srd-worker-1",
            LockDuration = options.LockDuration ?? builder.Configuration["Flowable:LockDuration"] ?? "PT30S",
            MaxJobsPerTick = options.MaxJobsPerTick ?? int.Parse(builder.Configuration["Flowable:MaxJobsPerTick"] ?? "5"),
            PollPeriodSeconds = options.PollPeriodSeconds ?? int.Parse(builder.Configuration["Flowable:PollPeriodSeconds"] ?? "3"),
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism ?? int.Parse(builder.Configuration["Flowable:MaxDegreeOfParallelism"] ?? "2"),
            InitialRetries = options.InitialRetries ?? 3,
            TimeZoneId = builder.Configuration["Windows:Timezone"] ?? "Europe/Prague",
            PauseFromHour = builder.Configuration.GetValue<int?>("Windows:PauseFromHour"),
            PauseToHourExclusive = builder.Configuration.GetValue<int?>("Windows:PauseToHourExclusive"),
            Retry = new RetryOptions
            {
                InitialDelaySeconds = int.Parse(builder.Configuration["Retry:InitialDelaySeconds"] ?? "60"),
                MaxDelaySeconds = int.Parse(builder.Configuration["Retry:MaxDelaySeconds"] ?? "900"),
                JitterSeconds = int.Parse(builder.Configuration["Retry:JitterSeconds"] ?? "5"),
                BackoffMultiplier = double.TryParse(builder.Configuration["Retry:Multiplier"], out var multiplier) ? multiplier : 2.0
            }
        };

        var handler = ActivatorUtilities.CreateInstance<HttpExternalTaskHandler>(sp, options);
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        var logger = sp.GetRequiredService<ILogger<FlowableExternalWorkerService<HttpExternalTaskHandler>>>();
        return new FlowableExternalWorkerService<HttpExternalTaskHandler>(httpFactory, logger, flowableOptions, handler);
    });
}

await builder.Build().RunAsync();

public sealed record WorkerOptions
{
    public string? Topic { get; init; }
    public string? WorkerId { get; init; }
    public string? LockDuration { get; init; }
    public int? MaxJobsPerTick { get; init; }
    public int? PollPeriodSeconds { get; init; }
    public int? MaxDegreeOfParallelism { get; init; }
    public string? TargetUrl { get; init; }
    public int? InitialRetries { get; init; }
}
