using Chargeback.Infrastructure.Persistence;
using Dapper;
using Npgsql;

namespace Chargeback.Infrastructure.Ai;

/// <summary>Column values for one <c>ai_decision_logs</c> row. Payloads must already be masked.</summary>
public sealed record AiDecisionLogEntry(
    Guid Id,
    Guid? CaseId,
    Guid? BankId,
    string AgentName,
    string CapabilityName,
    string? ModelName,
    string PromptTemplateId,
    string InputHash,
    string? RawResponseJson,
    string? ParsedOutputJson,
    int LatencyMs,
    int? TokenCountInput,
    int? TokenCountOutput,
    DateTimeOffset CreatedAt);

public interface IAiDecisionLogWriter
{
    Task WriteAsync(AiDecisionLogEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// Writes on its own connection, independent of any business transaction, so the audit record
/// survives even if the calling command later rolls back.
/// </summary>
internal sealed class AiDecisionLogWriter(NpgsqlDataSource dataSource) : IAiDecisionLogWriter
{
    private const string InsertSql = """
        INSERT INTO chargeback_diagram.ai_decision_logs
            (id, case_id, bank_id, agent_name, capability_name, model_name, prompt_template_id, input_hash,
             raw_response, parsed_output, latency_ms, token_count_input, token_count_output, created_at)
        VALUES
            (@Id, @CaseId, @BankId, @AgentName, @CapabilityName, @ModelName, @PromptTemplateId, @InputHash,
             CAST(@RawResponseJson AS jsonb), CAST(@ParsedOutputJson AS jsonb), @LatencyMs,
             @TokenCountInput, @TokenCountOutput, @CreatedAt)
        """;

    public async Task WriteAsync(AiDecisionLogEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Audit writer on its own connection: system scope under row-level security (migration 0011).
        await DatabaseScopeSql.ApplyAsync(connection, null, DatabaseScopeMode.System, [], cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(InsertSql, entry, cancellationToken: cancellationToken));
    }
}
