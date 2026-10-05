namespace Chargeback.Api.Common.Security;

/// <summary>
/// Marks a request as an internal workflow step (e.g. triage after intake) that no user or HTTP caller may
/// invoke. It satisfies the permission rule only while <see cref="SystemExecution"/> is active (ADR-0123).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class SystemOperationAttribute(string justification) : Attribute
{
    public string Justification { get; } = justification;
}

/// <summary>
/// Ambient, in-process marker that the current async flow is a trusted workflow step, not a user request.
/// Never set from HTTP handling: an architecture test restricts callers of <see cref="Begin"/> to the
/// <c>Chargeback.Api.Workers</c> namespace (and tests). It grants nothing to user requests.
/// </summary>
public static class SystemExecution
{
    private static readonly AsyncLocal<string?> Operation = new();

    public static bool IsActive => Operation.Value is not null;

    public static string? CurrentOperation => Operation.Value;

    public static IDisposable Begin(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var previous = Operation.Value;
        Operation.Value = operation;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Operation.Value = previous;
            }
        }
    }
}
