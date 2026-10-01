using Chargeback.Infrastructure.Persistence.Queries;

namespace Chargeback.Infrastructure.Security;

/// <summary>Kinds of resource whose owning bank can be resolved for scope checks.</summary>
public enum ScopedResourceKind
{
    Dispute,
    Case,
    Document,
    Filing,

    /// <summary>A bank user (users.bank_id NOT NULL). Processor/admin users are not bank-owned and never resolve.</summary>
    BankUser,
}

public interface IResourceBankResolver
{
    /// <returns>The owning bank id, or <c>null</c> when the resource does not exist or has no owning bank.</returns>
    Task<Guid?> ResolveBankIdAsync(ScopedResourceKind kind, Guid resourceId, CancellationToken cancellationToken);
}

internal sealed class ResourceBankResolver(IDapperQueryService db) : IResourceBankResolver
{
    private static readonly Dictionary<ScopedResourceKind, string> Queries = new()
    {
        [ScopedResourceKind.Dispute] = """
            SELECT d.bank_id FROM chargeback_diagram.disputes d WHERE d.id = @Id
            """,
        [ScopedResourceKind.Case] = """
            SELECT d.bank_id FROM chargeback_diagram.cases c
            JOIN chargeback_diagram.disputes d ON d.id = c.dispute_id
            WHERE c.id = @Id
            """,
        [ScopedResourceKind.Document] = """
            SELECT d.bank_id FROM chargeback_diagram.documents doc
            JOIN chargeback_diagram.cases c ON c.id = doc.case_id
            JOIN chargeback_diagram.disputes d ON d.id = c.dispute_id
            WHERE doc.id = @Id
            """,
        [ScopedResourceKind.Filing] = """
            SELECT d.bank_id FROM chargeback_diagram.mastercom_filings f
            JOIN chargeback_diagram.cases c ON c.id = f.case_id
            JOIN chargeback_diagram.disputes d ON d.id = c.dispute_id
            WHERE f.id = @Id
            """,
        [ScopedResourceKind.BankUser] = """
            SELECT u.bank_id FROM chargeback_diagram.users u WHERE u.id = @Id AND u.user_type = 'BANK'
            """,
    };

    public Task<Guid?> ResolveBankIdAsync(ScopedResourceKind kind, Guid resourceId, CancellationToken cancellationToken) =>
        db.QuerySingleOrDefaultAsync<Guid?>(Queries[kind], new { Id = resourceId }, cancellationToken);
}
