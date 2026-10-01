using System.Data;
using System.Data.Common;
using Chargeback.SharedKernel.Paging;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Chargeback.Infrastructure.Persistence.Queries;

/// <summary>
/// Read-side SQL access. Slices own their SQL; always reference tables schema-qualified
/// (<c>chargeback_diagram.cases</c>) and always filter by the caller's bank scope.
/// Inside a transactional command the query joins the active EF transaction so it sees
/// uncommitted writes; otherwise it uses a pooled connection.
/// </summary>
public interface IDapperQueryService
{
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters, CancellationToken cancellationToken);

    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters, CancellationToken cancellationToken);

    Task<T> ExecuteScalarAsync<T>(string sql, object? parameters, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="countSql"/> and <paramref name="pageSql"/>; the page SQL must end with
    /// <c>LIMIT @Limit OFFSET @Offset</c> and <paramref name="parameters"/> must not define those names.
    /// </summary>
    Task<PagedResult<T>> QueryPageAsync<T>(
        string countSql, string pageSql, DynamicParameters parameters, PageRequest page, CancellationToken cancellationToken);
}

internal sealed class DapperQueryService(NpgsqlDataSource dataSource, ChargebackDbContext db) : IDapperQueryService
{
    private const int CommandTimeoutSeconds = 30;

    public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters, CancellationToken cancellationToken) =>
        WithConnection(async (connection, transaction) =>
            (IReadOnlyList<T>)(await connection.QueryAsync<T>(Command(sql, parameters, transaction, cancellationToken))).AsList());

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters, CancellationToken cancellationToken) =>
        WithConnection((connection, transaction) =>
            connection.QuerySingleOrDefaultAsync<T>(Command(sql, parameters, transaction, cancellationToken)));

    public Task<T> ExecuteScalarAsync<T>(string sql, object? parameters, CancellationToken cancellationToken) =>
        WithConnection(async (connection, transaction) =>
            (await connection.ExecuteScalarAsync<T>(Command(sql, parameters, transaction, cancellationToken)))!);

    public Task<PagedResult<T>> QueryPageAsync<T>(
        string countSql, string pageSql, DynamicParameters parameters, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(page);

        return WithConnection(async (connection, transaction) =>
        {
            var total = await connection.ExecuteScalarAsync<long>(Command(countSql, parameters, transaction, cancellationToken));
            parameters.Add("Limit", page.PageSize);
            parameters.Add("Offset", page.Offset);
            var items = (await connection.QueryAsync<T>(Command(pageSql, parameters, transaction, cancellationToken))).AsList();
            return new PagedResult<T>(items, page.Page, page.PageSize, total);
        });
    }

    private static CommandDefinition Command(string sql, object? parameters, DbTransaction? transaction, CancellationToken ct) =>
        new(sql, parameters, transaction, CommandTimeoutSeconds, CommandType.Text, CommandFlags.Buffered, ct);

    private async Task<TResult> WithConnection<TResult>(Func<DbConnection, DbTransaction?, Task<TResult>> work)
    {
        if (db.Database.CurrentTransaction is { } efTransaction)
        {
            return await work(db.Database.GetDbConnection(), efTransaction.GetDbTransaction());
        }

        await using var connection = await dataSource.OpenConnectionAsync();
        return await work(connection, null);
    }
}
