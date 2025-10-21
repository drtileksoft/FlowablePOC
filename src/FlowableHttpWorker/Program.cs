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

builder.Services.Configure<HostOptions>(options =>
{
    options.ServicesStartConcurrently = true;
    options.ServicesStopConcurrently = true;
});

builder.Services.AddFlowableHttpWorkers(builder.Configuration);

await builder.Build().RunAsync();
