namespace Chargeback.Infrastructure.Persistence;

/// <summary>
/// Enforces "never hold a database transaction open across an external call".
/// The transaction behavior enters a scope for the duration of a transactional command; every
/// external adapter (Bedrock, S3, Textract, Mastercom, Zendesk, SNS/SQS, SES) calls
/// <see cref="ThrowIfInDatabaseTransaction"/> first. External work belongs after commit, via the outbox.
/// </summary>
public static class ExternalCallGuard
{
    private static readonly AsyncLocal<int> Depth = new();

    public static bool IsInDatabaseTransaction => Depth.Value > 0;

    public static IDisposable EnterDatabaseTransaction()
    {
        Depth.Value++;
        return new Scope();
    }

    public static void ThrowIfInDatabaseTransaction(string dependency)
    {
        if (IsInDatabaseTransaction)
        {
            throw new InvalidOperationException(
                $"External call to '{dependency}' attempted inside a database transaction. " +
                "Raise a domain event and perform the call after commit (outbox).");
        }
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Depth.Value--;
            }
        }
    }
}
