using System.Text.Json.Serialization;

namespace Flowable.ExternalWorker;

public sealed record FlowableVariable(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] object? Value,
    [property: JsonPropertyName("type")] string? Type = null);

public sealed record FlowableAcquireRequest(
    [property: JsonPropertyName("workerId")] string WorkerId,
    [property: JsonPropertyName("maxJobs")] int MaxJobs,
    [property: JsonPropertyName("lockDuration")] string LockDuration,
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("fetchVariables")] bool FetchVariables = true);

public sealed record FlowableJob(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("processInstanceId")] string ProcessInstanceId,
    [property: JsonPropertyName("processDefinitionId")] string ProcessDefinitionId,
    [property: JsonPropertyName("executionId")] string ExecutionId,
    [property: JsonPropertyName("scopeId")] string? ScopeId,
    [property: JsonPropertyName("subScopeId")] string? SubScopeId,
    [property: JsonPropertyName("scopeDefinitionId")] string? ScopeDefinitionId,
    [property: JsonPropertyName("scopeType")] string? ScopeType,
    [property: JsonPropertyName("elementId")] string ElementId,
    [property: JsonPropertyName("elementName")] string ElementName,
    [property: JsonPropertyName("retries")] int Retries,
    [property: JsonPropertyName("exceptionMessage")] string? ExceptionMessage,
    [property: JsonPropertyName("dueDate")] string? DueDate,
    [property: JsonPropertyName("createTime")] string CreateTime,
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("lockOwner")] string? LockOwner,
    [property: JsonPropertyName("lockExpirationTime")] string LockExpirationTime,
    [property: JsonPropertyName("variables")] IReadOnlyList<FlowableVariable>? Variables)
{
    public IReadOnlyDictionary<string, object?> VariablesAsDictionary =>
        Variables?.ToDictionary(v => v.Name, v => v.Value) ?? new Dictionary<string, object?>();

    public DateTimeOffset? LockExpirationParsed
        => DateTimeOffset.TryParse(LockExpirationTime, out var dto) ? dto : null;

    public bool IsLocked(DateTimeOffset nowUtc)
        => LockExpirationParsed is { } exp && exp > nowUtc;
}

public sealed record FlowableJobAcquisition(
    FlowableJob Job,
    IReadOnlyDictionary<string, object?> Variables);
