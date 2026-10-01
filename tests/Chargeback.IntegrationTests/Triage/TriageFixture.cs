using System.Collections.Concurrent;
using Chargeback.Api.Features.Intake.Gates;
using Chargeback.Api.Features.Triage.IssuerTriage;
using Chargeback.IntegrationTests.Infrastructure;
using Chargeback.TestSupport;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Chargeback.IntegrationTests.Triage;

/// <summary>
/// Own PostgreSQL container for triage tests: scheme rules are global data, so they are isolated from the
/// other integration suites. Two hosts share it:
/// <list type="bullet">
/// <item><see cref="Default"/> — production configuration (no gate evaluators, no scheme settings, no bank config);</item>
/// <item><see cref="Synthetic"/> — SYNTHETIC gate evaluators (all pass), SYNTHETIC scheme settings and a SYNTHETIC
/// per-bank triage configuration. Synthetic values exercise the mechanics only; they are not approved rules.</item>
/// </list>
/// </summary>
public class TriageFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase("chargeback_workflow").Build();

    public string ConnectionString => _container.GetConnectionString();

    public ChargebackApiFactory Default { get; private set; } = null!;

    public ChargebackApiFactory Synthetic { get; private set; } = null!;

    public SyntheticBankConfigurationProvider BankConfigurations { get; } = new();

    public TestData Data { get; private set; } = null!;

    public static IReadOnlyDictionary<string, string> SyntheticSchemeSettings { get; } = new Dictionary<string, string>
    {
        ["SchemeRules:CalendarTimeZone"] = "UTC",
        ["SchemeRules:EffectiveDateBasis"] = "TransactionDate",
        ["SchemeRules:ClockStartBasis"] = "TransactionDate",
        ["SchemeRules:DeadlineDayCounting"] = "CalendarDays",
    };

    /// <summary>Settings applied to both hosts (e.g. the outbox transport in the case-management suite).</summary>
    protected virtual IReadOnlyDictionary<string, string> ExtraSettings { get; } = new Dictionary<string, string>();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await BaselineDatabase.ApplyAllAsync(ConnectionString);
        Data = new TestData(ConnectionString);
        await Data.SeedProposedPermissionsAsync();

        Default = new ChargebackApiFactory(ConnectionString) { Settings = new Dictionary<string, string>(ExtraSettings) };

        var settings = new Dictionary<string, string>(SyntheticSchemeSettings);
        foreach (var (key, value) in ExtraSettings)
        {
            settings[key] = value;
        }

        for (var gate = 1; gate <= 10; gate++)
        {
            settings[$"Intake:Gates:ActiveGates:{gate - 1}"] = gate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Synthetic = new ChargebackApiFactory(ConnectionString)
        {
            Settings = settings,
            ConfigureServices = services =>
            {
                for (var gate = 1; gate <= 10; gate++)
                {
                    var number = gate;
                    services.AddSingleton<IGateEvaluator>(new SyntheticPassingGate(number));
                }

                services.AddSingleton<IBankTriageConfigurationProvider>(BankConfigurations);
            },
        };
    }

    public async Task DisposeAsync()
    {
        await Default.DisposeAsync();
        await Synthetic.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>Inserts a SYNTHETIC reason code + rule spec. Descriptions say so; never production data.</summary>
    public async Task<(Guid ReasonCodeId, Guid RuleSpecId)> CreateSyntheticRuleAsync(
        string code, string conditionsJson, string requiredDocsJson, int? timeLimitDays, string approvalStatus = "APPROVED",
        DateOnly? effectiveFrom = null, DateOnly? effectiveTo = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var reasonCodeId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO chargeback_diagram.scheme_reason_codes(code, description, category, effective_from)
            VALUES (@code, 'SYNTHETIC TEST DATA - not a scheme reason code', 'SYNTHETIC', DATE '2020-01-01') RETURNING id
            """,
            new { code });
        var ruleSpecId = await connection.ExecuteScalarAsync<Guid>(
            """
            INSERT INTO chargeback_diagram.scheme_rule_specs(reason_code_id, scenario, conditions_json, required_docs, time_limit_days,
                                                            effective_from, effective_to, approval_status)
            VALUES (@reasonCodeId, 'SYNTHETIC TEST SCENARIO', CAST(@conditionsJson AS jsonb), CAST(@requiredDocsJson AS jsonb), @timeLimitDays,
                    @effectiveFrom, @effectiveTo, @approvalStatus) RETURNING id
            """,
            new
            {
                reasonCodeId,
                conditionsJson,
                requiredDocsJson,
                timeLimitDays,
                effectiveFrom = (effectiveFrom ?? new DateOnly(2020, 1, 1)).ToDateTime(TimeOnly.MinValue),
                effectiveTo = effectiveTo?.ToDateTime(TimeOnly.MinValue),
                approvalStatus,
            });
        return (reasonCodeId, ruleSpecId);
    }

    /// <summary>
    /// SYNTHETIC case fixture: case creation (and the case-reference format) belongs to Phase 7, so tests insert
    /// the case row directly for a dispute that intake already created.
    /// </summary>
    public async Task<Guid> CreateSyntheticCaseAsync(Guid disputeId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<Guid>(
            "INSERT INTO chargeback_diagram.cases(dispute_id, case_reference) VALUES (@disputeId, @reference) RETURNING id",
            new { disputeId, reference = "SYN-CASE-" + Guid.NewGuid().ToString("N")[..12] });
    }
}

[CollectionDefinition(Name)]
public sealed class TriageCollection : ICollectionFixture<TriageFixture>
{
    public const string Name = "triage";
}

/// <summary>SYNTHETIC evaluator: always passes. Exists only so tests can reach the NEW path; not a gate criterion.</summary>
public sealed class SyntheticPassingGate(int gateNumber) : IGateEvaluator
{
    public int GateNumber => gateNumber;

    public Task<GateOutcome> EvaluateAsync(GateContext context, CancellationToken cancellationToken) => Task.FromResult(GateOutcome.Pass());
}

/// <summary>SYNTHETIC per-bank configuration store for tests; unknown banks are "not configured" like production.</summary>
public sealed class SyntheticBankConfigurationProvider : IBankTriageConfigurationProvider
{
    private readonly ConcurrentDictionary<Guid, BankTriageConfigurationResult> _byBank = new();

    public void Set(Guid bankId, BankTriageConfiguration configuration) => _byBank[bankId] = BankTriageConfigurationResult.Approved(configuration);

    public void SetUnavailable(Guid bankId) => _byBank[bankId] = BankTriageConfigurationResult.Unavailable("SYNTHETIC store failure");

    public Task<BankTriageConfigurationResult> GetApprovedAsync(Guid bankId, DateTimeOffset asOf, CancellationToken cancellationToken) =>
        Task.FromResult(_byBank.TryGetValue(bankId, out var result)
            ? result
            : BankTriageConfigurationResult.NotConfigured("SYNTHETIC: no configuration for this bank"));
}
