using System.Diagnostics;

namespace Chargeback.Infrastructure.Correlation;

/// <summary>Supplies the correlation id stamped on logs, problem responses and outbox events.</summary>
public interface ICorrelationIdProvider
{
    string CorrelationId { get; }
}

/// <summary>Fallback for non-HTTP work (background workers): uses the ambient trace id.</summary>
internal sealed class AmbientCorrelationIdProvider : ICorrelationIdProvider
{
    private readonly string _fallback = Guid.NewGuid().ToString("N");

    public string CorrelationId => Activity.Current?.TraceId.ToString() ?? _fallback;
}
