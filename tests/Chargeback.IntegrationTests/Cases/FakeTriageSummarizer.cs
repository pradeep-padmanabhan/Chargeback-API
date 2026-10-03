using System.Collections.Concurrent;
using System.Text.Json;
using Chargeback.Infrastructure.Ai;
using Chargeback.Infrastructure.Ai.Capabilities;
using Dapper;
using Npgsql;

namespace Chargeback.IntegrationTests.Cases;

/// <summary>
/// SYNTHETIC Triage Summary capability. Like <c>BedrockAiClient</c>, it writes one <c>ai_decision_logs</c> row per call
/// (parsed output only on success). Calls are recorded per case so parallel tests do not interfere.
/// </summary>
public sealed class FakeTriageSummarizer : ITriageSummarizer
{
    public const string ModelName = "synthetic-model";
    public const string PromptTemplateId = "triage-summary@synthetic-1";

    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<TriageSummaryInput>> _calls = new();
    private readonly ConcurrentDictionary<Guid, bool> _unavailable = new();

    public string ConnectionString { get; set; } = "";

    public IReadOnlyList<TriageSummaryInput> CallsFor(Guid caseId) =>
        _calls.TryGetValue(caseId, out var calls) ? [.. calls] : [];

    public void MakeUnavailableFor(Guid caseId) => _unavailable[caseId] = true;

    public static string TextFor(Guid caseId, int call) => $"SYNTHETIC summary {call} for {caseId}";

    public async Task<AiResult<TriageSummaryOutput>> SummarizeAsync(TriageSummaryInput input, CancellationToken cancellationToken)
    {
        var calls = _calls.GetOrAdd(input.CaseId, _ => new ConcurrentQueue<TriageSummaryInput>());
        calls.Enqueue(input);
        var available = !_unavailable.ContainsKey(input.CaseId);
        var output = available ? new TriageSummaryOutput(TextFor(input.CaseId, calls.Count)) : null;

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            """
            INSERT INTO chargeback_diagram.ai_decision_logs(case_id, agent_name, capability_name, model_name, prompt_template_id, parsed_output)
            VALUES (@CaseId, 'AnalysisExplanation', @Capability, @ModelName, @PromptTemplateId, CAST(@Parsed AS jsonb))
            """,
            new
            {
                input.CaseId,
                Capability = AiCapabilities.TriageSummary,
                ModelName,
                PromptTemplateId,
                Parsed = output is null ? null : JsonSerializer.Serialize(output),
            });

        return output is null
            ? AiResult<TriageSummaryOutput>.Failed(AiResultStatus.Unavailable, "SYNTHETIC outage")
            : AiResult<TriageSummaryOutput>.Success(output, ModelName);
    }
}
