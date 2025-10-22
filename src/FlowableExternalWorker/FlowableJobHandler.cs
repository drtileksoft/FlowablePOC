namespace AMCSSZ.NWF.Shared.ExternalFlowableWorker;

public interface IFlowableJobHandler
{
    Task<FlowableJobHandlerResult> HandleAsync(FlowableJobContext context, CancellationToken cancellationToken);

    Task HandleFinalFailureAsync(
        FlowableJobContext context,
        Exception exception,
        FlowableFinalFailureAction action,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed record FlowableJobContext(FlowableJob Job, IReadOnlyDictionary<string, object?> Variables);

public sealed record FlowableJobHandlerResult(
    IReadOnlyCollection<FlowableVariable> Variables,
    IReadOnlyCollection<FlowableVariable>? LocalVariables = null)
{
    public static FlowableJobHandlerResult Empty { get; } = new(Array.Empty<FlowableVariable>());
}

public sealed record FlowableFinalFailureAction(
    FlowableFinalFailureActionType ActionType,
    string? ErrorMessage = null,
    IReadOnlyCollection<FlowableVariable>? Variables = null,
    string? ErrorCode = null)
{
    public static FlowableFinalFailureAction Incident(string? errorMessage = null)
        => new(FlowableFinalFailureActionType.Incident, errorMessage);

    public static FlowableFinalFailureAction Complete(
        IReadOnlyCollection<FlowableVariable>? variables = null,
        string? errorMessage = null)
        => new(FlowableFinalFailureActionType.Complete, errorMessage, variables);

    public static FlowableFinalFailureAction BpmnError(
        string errorCode,
        string? errorMessage = null,
        IReadOnlyCollection<FlowableVariable>? variables = null)
        => new(FlowableFinalFailureActionType.BpmnError, errorMessage, variables, errorCode);
}

public enum FlowableFinalFailureActionType
{
    Incident,
    Complete,
    BpmnError
}

public class FlowableJobRetryException : Exception
{
    public FlowableJobRetryException(string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        RetryAfter = retryAfter;
    }

    public FlowableJobRetryException(string message, Exception innerException, TimeSpan? retryAfter = null)
        : base(message, innerException)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}

public class FlowableJobFinalException : Exception
{
    public FlowableJobFinalException(FlowableFinalFailureAction action, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Action = action;
    }

    public FlowableFinalFailureAction Action { get; }
}
