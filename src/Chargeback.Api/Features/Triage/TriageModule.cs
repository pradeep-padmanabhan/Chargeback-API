using System.Text.Json;
using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Triage.Contracts;
using Chargeback.Api.Features.Triage.IssuerTriage;
using Chargeback.Api.Features.Triage.SchemeRules;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using Chargeback.SharedKernel.ValueObjects;
using MediatR;

namespace Chargeback.Api.Features.Triage;

[RequirePermission(Permissions.ViewTriage)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record GetCaseTriageQuery(Guid CaseId) : IQuery<IReadOnlyList<TriageResultDto>>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

internal sealed class GetCaseTriageHandler(ITriageResultReader triage) : IRequestHandler<GetCaseTriageQuery, Result<IReadOnlyList<TriageResultDto>>>
{
    public async Task<Result<IReadOnlyList<TriageResultDto>>> Handle(GetCaseTriageQuery request, CancellationToken cancellationToken) =>
        Result.Success(await triage.ReadForCaseAsync(request.CaseId, cancellationToken));
}

internal sealed class TriageResultReader(IDapperQueryService db) : ITriageResultReader
{
    private const string Sql = """
        SELECT id, triage_layer, hard_eligibility_pass, routing_policy_outcome, risk_score, risk_flags::text AS risk_flags,
               human_review_triggered, human_review_reason, outcome, created_at
        FROM chargeback_diagram.triage_results
        WHERE case_id = @CaseId
        ORDER BY created_at DESC, id DESC
        """;

    public async Task<IReadOnlyList<TriageResultDto>> ReadForCaseAsync(Guid caseId, CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync<Row>(Sql, new { CaseId = caseId }, cancellationToken);
        return rows.Select(r => r.ToDto()).ToArray();
    }

    private sealed class Row
    {
        public Guid Id { get; set; }

        public string? TriageLayer { get; set; }

        public bool? HardEligibilityPass { get; set; }

        public string? RoutingPolicyOutcome { get; set; }

        public decimal? RiskScore { get; set; }

        public string RiskFlags { get; set; } = "[]";

        public bool HumanReviewTriggered { get; set; }

        public string? HumanReviewReason { get; set; }

        public string? Outcome { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public TriageResultDto ToDto()
        {
            var outcome = Outcome is null ? (TriageOutcome?)null : Enum.Parse<TriageOutcome>(Outcome);
            return new TriageResultDto(
                Id,
                TriageLayer,
                HardEligibilityPass,
                RoutingPolicyOutcome,
                RiskScore,
                ParseFlags(RiskFlags),
                HumanReviewTriggered,
                HumanReviewReason,
                outcome,
                CreatedAt,
                outcome is null ? TriageEvaluationStatus.Incomplete : TriageEvaluationStatus.Complete);
        }

        private static string[] ParseFlags(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}

public static class TriageServiceRegistration
{
    public static IServiceCollection AddTriageSlice(this IServiceCollection services)
    {
        services.AddOptions<SchemeRulesOptions>().BindConfiguration(SchemeRulesOptions.SectionName);
        services.AddScoped<ISchemeRuleRepository, SchemeRuleRepository>();
        services.AddScoped<ISchemeRulesEngine, SchemeRulesEngine>();
        services.AddScoped<IIssuerTriageEngine, IssuerTriageEngine>();
        services.AddScoped<EvaluateCaseTriage.CaseTriageRunner>();
        services.AddSingleton<ISchemeCalendar, SchemeCalendar>();
        services.AddScoped<ITriageResultReader, TriageResultReader>();

        // No approved bank configuration storage exists (ADR-0119): every bank is "not configured".
        services.AddSingleton<IBankTriageConfigurationProvider, PendingApprovalBankTriageConfigurationProvider>();
        return services;
    }
}

public sealed class TriageModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        // Triage itself is run by the workflow (EvaluateCaseTriageCommand, system-only); this endpoint only reads results.
        app.MapGroup(EndpointConventions.ApiPrefix).WithTags("Triage")
            .MapGet("/cases/{caseId:guid}/triage", (Guid caseId, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new GetCaseTriageQuery(caseId), http))
            .WithContract<IReadOnlyList<TriageResultDto>>("getCaseTriage", "Triage evaluations for a case, newest first")
            .WithDescription(
                "Outcomes are deterministic recommendations and never execute refunds or filings. " +
                "evaluationStatus = Incomplete means approved rules, bank configuration or facts were missing; " +
                "outcome is then null and the case is routed for manual attention.");

        app.MapGroup(EndpointConventions.ApiPrefix).WithTags("Triage")
            .MapPost("/cases/{caseId:guid}/retriage", (Guid caseId, RetriageCase.RetriageRequest body, ISender sender, HttpContext http) =>
                Dispatch.Send(sender, new RetriageCase.RetriageCaseCommand(caseId, body), http))
            .WithContract<TriageResultDto>("retriageCase", "Manually re-run triage on a FLAGGED or UNDER_REVIEW case (reason required)")
            .WithDescription(
                "ADR-0124: analyst-only (RETRIAGE_CASE), explicit action with a recorded reason; never automatic. " +
                "Records case.retriage.requested and the new triage result atomically. Does not change the case status. " +
                "409 RETRIAGE_NOT_ALLOWED for other statuses; 503 TRIAGE_UNAVAILABLE records nothing.");
    }
}
