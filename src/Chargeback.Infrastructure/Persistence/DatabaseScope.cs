using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Chargeback.Infrastructure.Persistence;

/// <summary>What the row-level security policies (migration 0010, ADR-0006) let this request see.</summary>
public enum DatabaseScopeMode
{
    /// <summary>Nothing (fail closed): the scope has not been established yet, or the request is not bank-scoped.</summary>
    None,

    /// <summary>Rows of the listed banks only.</summary>
    Banks,

    /// <summary>All rows: workflow steps and authorization's own scope resolution.</summary>
    System,
}

/// <summary>
/// The request's database scope. Applied as session settings (<c>app.scope</c>, <c>app.bank_ids</c>) on every connection
/// the request opens, and re-applied to an open connection whenever the scope changes. Settings are written on every
/// open, so a pooled connection never carries another request's scope.
/// </summary>
public interface IDatabaseScope
{
    DatabaseScopeMode Mode { get; }

    IReadOnlyCollection<Guid> BankIds { get; }

    Task UseBanksAsync(IEnumerable<Guid> bankIds, CancellationToken cancellationToken);

    Task UseSystemAsync(CancellationToken cancellationToken);

    /// <summary>Temporarily system scope (e.g. resolving which bank owns a resource); the previous scope is restored on dispose.</summary>
    Task<IAsyncDisposable> ElevateToSystemAsync(CancellationToken cancellationToken);
}

internal sealed class DatabaseScope(IServiceProvider services) : IDatabaseScope
{
    public DatabaseScopeMode Mode { get; private set; } = DatabaseScopeMode.None;

    public IReadOnlyCollection<Guid> BankIds { get; private set; } = [];

    public Task UseBanksAsync(IEnumerable<Guid> bankIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bankIds);
        return SetAsync(DatabaseScopeMode.Banks, [.. bankIds.Distinct()], cancellationToken);
    }

    public Task UseSystemAsync(CancellationToken cancellationToken) => SetAsync(DatabaseScopeMode.System, [], cancellationToken);

    public async Task<IAsyncDisposable> ElevateToSystemAsync(CancellationToken cancellationToken)
    {
        var previous = (Mode, BankIds);
        await UseSystemAsync(cancellationToken);
        return new Restore(this, previous.Mode, previous.BankIds);
    }

    internal Task ApplyAsync(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken) =>
        DatabaseScopeSql.ApplyAsync(connection, transaction, Mode, BankIds, cancellationToken);

    private async Task SetAsync(DatabaseScopeMode mode, IReadOnlyCollection<Guid> bankIds, CancellationToken cancellationToken)
    {
        Mode = mode;
        BankIds = bankIds;

        // An EF connection kept open (e.g. inside a transaction) must see the new scope immediately.
        var db = services.GetService<ChargebackDbContext>();
        if (db?.Database.GetDbConnection() is { State: System.Data.ConnectionState.Open } connection)
        {
            await ApplyAsync(connection, db.Database.CurrentTransaction?.GetDbTransaction(), cancellationToken);
        }
    }

    private sealed class Restore(DatabaseScope scope, DatabaseScopeMode mode, IReadOnlyCollection<Guid> bankIds) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await scope.SetAsync(mode, bankIds, CancellationToken.None);
    }
}

/// <summary>Writes the scope as session settings read by the policies' helper functions (migration 0010).</summary>
public static class DatabaseScopeSql
{
    private const string Sql = "SELECT set_config('app.scope', @scope, false), set_config('app.bank_ids', @bankIds, false)";

    public static string ScopeValue(DatabaseScopeMode mode) => mode switch
    {
        DatabaseScopeMode.Banks => "banks",
        DatabaseScopeMode.System => "system",
        _ => "",
    };

    /// <summary>A PostgreSQL uuid[] literal, e.g. <c>{a,b}</c>; empty for no banks.</summary>
    public static string BankIdsValue(DatabaseScopeMode mode, IEnumerable<Guid> bankIds) =>
        mode == DatabaseScopeMode.Banks ? "{" + string.Join(',', bankIds.Select(id => id.ToString("D"))) + "}" : "";

    public static async Task ApplyAsync(
        DbConnection connection, DbTransaction? transaction, DatabaseScopeMode mode, IEnumerable<Guid> bankIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.Transaction = transaction;
        AddParameter(command, "scope", ScopeValue(mode));
        AddParameter(command, "bankIds", BankIdsValue(mode, bankIds));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Applies the request's database scope whenever EF opens a connection.</summary>
internal sealed class DatabaseScopeConnectionInterceptor(IDatabaseScope scope) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default) =>
        await ((DatabaseScope)scope).ApplyAsync(connection, null, cancellationToken);

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        ((DatabaseScope)scope).ApplyAsync(connection, null, CancellationToken.None).GetAwaiter().GetResult();
}
