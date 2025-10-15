using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// ---------- Services ----------
builder.Services.AddEndpointsApiExplorer();

// CORS – pro POC povolíme vše (mùžeš omezit pøes App Settings pozdìji)
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// JSON options
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// In-memory storage (DI)
builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();

var app = builder.Build();

// ---------- Middleware ----------
app.UseCors();

// ---------- Endpoints ----------
app.MapGet("/", () => Results.Redirect("/health"));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// POST /api/events
app.MapPost("/api/events", async (HttpContext http, IEventStore store, PostEventRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Id))
        return Results.BadRequest(new { error = "Missing 'id'." });

    string? payloadString = req.Data?.GetRawText();

    // ---------- helpers ----------
    static DateTimeOffset ToPrague(DateTimeOffset utcNow)
    {
        TimeZoneInfo? tz = null;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague"); }
        catch { try { tz = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time"); } catch { } }

        if (tz is null) return utcNow;
        var local = TimeZoneInfo.ConvertTime(utcNow.UtcDateTime, tz);
        return new DateTimeOffset(local);
    }

    static DateTimeOffset? TryParseDto(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, out var dto) ? dto : null;
    }

var nowUtc = DateTimeOffset.UtcNow;
    var prague = ToPrague(nowUtc);

    var record = new EventRecord
    {
        Id = req.Id!,
        Payload = payloadString,
        ClientTs = req.ClientTs,
        ServerTsUtc = nowUtc,
        ServerTsPrague = prague,
        ProcessInstanceId = http.Request.Headers.TryGetValue("X-Process-Instance-Id", out var pid) ? pid.ToString() : null,
        BusinessKey = http.Request.Headers.TryGetValue("X-Business-Key", out var bk) ? bk.ToString() : null,
        RemoteIp = http.Connection.RemoteIpAddress?.ToString()
    };

    await store.AddAsync(record, ct);
    return Results.Created($"/api/events/{Uri.EscapeDataString(req.Id)}", record);
})
.WithName("PostEvent")
.Produces<EventRecord>(StatusCodes.Status201Created)
.Produces(StatusCodes.Status400BadRequest);

// GET /api/events/{id}?from=&to=&limit=
app.MapGet("/api/events/{id}", async (string id, string? from, string? to, int? limit, IEventStore store, CancellationToken ct) =>
{
    // ---------- helpers ----------
    static DateTimeOffset ToPrague(DateTimeOffset utcNow)
    {
        TimeZoneInfo? tz = null;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague"); }
        catch { try { tz = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time"); } catch { } }

        if (tz is null) return utcNow;
        var local = TimeZoneInfo.ConvertTime(utcNow.UtcDateTime, tz);
        return new DateTimeOffset(local);
    }

    static DateTimeOffset? TryParseDto(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s, out var dto) ? dto : null;
    }


    var fromDto = TryParseDto(from);
    var toDto = TryParseDto(to);
    var list = await store.GetByIdAsync(id, fromDto, toDto, limit, ct);
    return Results.Ok(list);
})
.WithName("GetEventsById")
.Produces<List<EventRecord>>(StatusCodes.Status200OK);

app.MapGet("/api/stats/hourly/{id}", async (
    string id,
    string? date,
    string? tz,
    string? format,
    IEventStore store,
    CancellationToken ct) =>
{
    static TimeZoneInfo ResolveTimeZone(string tzId)
    {
        // Primárnì IANA, fallback na Windows ID pro App Service (Windows plány)
        try { return TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch
        {
            if (string.Equals(tzId, "Europe/Prague", StringComparison.OrdinalIgnoreCase))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time"); } catch { }
            }
            // fallback na UTC
            return TimeZoneInfo.Utc;
        }
    }

    string timezone = string.IsNullOrWhiteSpace(tz) ? "Europe/Prague" : tz.Trim();
    var tzInfo = ResolveTimeZone(timezone);

    static DateOnly ParseDateOrToday(string? date, TimeZoneInfo tz)
    {
        if (!string.IsNullOrWhiteSpace(date) && DateOnly.TryParse(date, out var d))
            return d;

        var nowLocal = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
        return DateOnly.FromDateTime(nowLocal);
    }

    // den v cílovém timezone
    var day = ParseDateOrToday(date, tzInfo);

    // UTC interval tohoto dne
    var localStart = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Unspecified);
    var localEnd = localStart.AddDays(1);
    var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, tzInfo);
    var utcEnd = TimeZoneInfo.ConvertTimeToUtc(localEnd, tzInfo);

    // stáhneme jen záznamy v intervalu
    var events = await store.GetByIdAsync(id, utcStart, utcEnd, null, ct);

    // napoèítáme po hodinách (v cílovém timezone)
    var buckets = new int[24];
    foreach (var ev in events)
    {
        var local = TimeZoneInfo.ConvertTime(ev.ServerTsUtc.UtcDateTime, tzInfo);
        int hour = local.Hour; // 0..23
        buckets[hour]++;
    }

    int total = 0;
    var rows = new List<object>(24);
    for (int h = 0; h < 24; h++)
    {
        total += buckets[h];
        rows.Add(new { hour = h, count = buckets[h] });
    }

    var payload = new
    {
        id,
        date = $"{day:yyyy-MM-dd}",
        timezone,
        total,
        buckets = rows
    };

    // format=html => jednoduchá HTML tabulka
    if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        return Results.Ok(payload);

    static string BuildHourlyHtmlTable(string id, string date, string tz, int total, int[] buckets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Hourly Stats</title>");
        sb.AppendLine("<style>body{font-family:system-ui,Segoe UI,Roboto,Arial,sans-serif;margin:24px}"
            + "table{border-collapse:collapse;min-width:360px}th,td{border:1px solid #ddd;padding:6px 10px;text-align:right}"
            + "th{background:#f5f5f5;text-align:center}caption{font-weight:600;margin-bottom:8px}"
            + "tr:nth-child(even){background:#fafafa}}</style></head><body>");
        sb.AppendLine($"<h2>Poèty zpráv po hodinách</h2>");
        sb.AppendLine($"<div><strong>ID:</strong> {System.Net.WebUtility.HtmlEncode(id)}<br/>");
        sb.AppendLine($"<strong>Datum:</strong> {date} &nbsp; <strong>Time zone:</strong> {System.Net.WebUtility.HtmlEncode(tz)}<br/>");
        sb.AppendLine($"<strong>Celkem:</strong> {total}</div><br/>");
        sb.AppendLine("<table><thead><tr><th>Hodina</th><th>Poèet</th></tr></thead><tbody>");
        for (int h = 0; h < 24; h++)
        {
            sb.Append("<tr><td style=\"text-align:center\">");
            sb.Append(h.ToString("D2"));
            sb.Append(":00</td><td>");
            sb.Append(buckets[h]);
            sb.AppendLine("</td></tr>");
        }
        sb.AppendLine("</tbody></table>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    var html = BuildHourlyHtmlTable(payload.id, payload.date, payload.timezone, payload.total, buckets);
    return Results.Content(html, "text/html", Encoding.UTF8);
})
.WithName("GetHourlyStats")
.Produces(StatusCodes.Status200OK);

// GET /api/ids
app.MapGet("/api/ids", async (IEventStore store, CancellationToken ct) =>
{
    var ids = await store.ListIdsAsync(ct);
    return Results.Ok(ids);
})
.WithName("ListIds");

app.Run();


// ---------- Models ----------
public sealed class PostEventRequest
{
    public string Id { get; set; } = default!;
    public JsonElement? Data { get; set; }
    public string? ClientTs { get; set; }
}

public sealed class EventRecord
{
    public string Id { get; set; } = default!;
    public string? Payload { get; set; }
    public string? ClientTs { get; set; }
    public DateTimeOffset ServerTsUtc { get; set; }
    public DateTimeOffset ServerTsPrague { get; set; }
    public string? ProcessInstanceId { get; set; }
    public string? BusinessKey { get; set; }
    public string? RemoteIp { get; set; }
}

// ---------- Storage (In-memory) ----------
public interface IEventStore
{
    Task AddAsync(EventRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<EventRecord>> GetByIdAsync(
        string id,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int? limit = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListIdsAsync(CancellationToken ct = default);
}

public sealed class InMemoryEventStore : IEventStore
{
    private readonly ConcurrentDictionary<string, List<EventRecord>> _store = new(StringComparer.Ordinal);

    public Task AddAsync(EventRecord record, CancellationToken ct = default)
    {
        var list = _store.GetOrAdd(record.Id, static _ => new List<EventRecord>());
        lock (list)
        {
            list.Add(record);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EventRecord>> GetByIdAsync(string id, DateTimeOffset? from = null, DateTimeOffset? to = null, int? limit = null, CancellationToken ct = default)
    {
        if (!_store.TryGetValue(id, out var list))
            return Task.FromResult<IReadOnlyList<EventRecord>>(Array.Empty<EventRecord>());

        IEnumerable<EventRecord> q = list;

        if (from is not null) q = q.Where(x => x.ServerTsUtc >= from);
        if (to is not null) q = q.Where(x => x.ServerTsUtc <= to);

        q = q.OrderBy(x => x.ServerTsUtc);

        if (limit is > 0) q = q.Take(limit.Value);

        return Task.FromResult<IReadOnlyList<EventRecord>>(q.ToList());
    }

    public Task<IReadOnlyList<string>> ListIdsAsync(CancellationToken ct = default)
    {
        var ids = _store.Keys.OrderBy(k => k).ToList();
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }
}


