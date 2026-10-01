using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases.ChangeCase;
using Chargeback.Api.Features.Cases.Contracts;
using Chargeback.Api.Features.Cases.CreateCase;
using Chargeback.Api.Features.Triage.RetriageCase;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using Chargeback.UnitTests.Behaviors;

namespace Chargeback.UnitTests.Cases;

public sealed class CaseReferenceTests
{
    [Theory]
    [InlineData(2026, 1, "CB-2026-000001")]
    [InlineData(2026, 42, "CB-2026-000042")]
    [InlineData(2027, 999_999, "CB-2027-999999")]
    public void Reference_uses_the_approved_format(int year, long number, string expected)
    {
        CaseReference.Format(year, number).Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000)]
    public void Numbers_outside_six_digits_are_refused_rather_than_reformatted(long number)
    {
        var act = () => CaseReference.Format(2026, number);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public sealed class CaseVersionTests
{
    [Theory]
    [InlineData("\"42\"")]
    [InlineData("W/\"42\"")]
    [InlineData("42")]
    public void Matching_if_match_passes(string header)
    {
        CaseVersion.Check(header, 42).Should().BeNull();
    }

    [Fact]
    public void Missing_if_match_is_precondition_required()
    {
        CaseVersion.Check(null, 42)!.Type.Should().Be(ErrorType.PreconditionRequired);
    }

    [Theory]
    [InlineData("\"41\"")]
    [InlineData("\"abc\"")]
    [InlineData("*")]
    public void Stale_or_malformed_if_match_is_precondition_failed(string header)
    {
        CaseVersion.Check(header, 42)!.Type.Should().Be(ErrorType.PreconditionFailed);
    }

    [Fact]
    public void Etag_is_quoted_version()
    {
        CaseVersion.ToETag(7).Should().Be("\"7\"");
    }
}

public sealed class CaseStatusTransitionTests
{
    [Fact]
    public void Status_set_is_the_approved_list()
    {
        CaseStatuses.All.Should().Equal("NEW", "FLAGGED", "UNDER_REVIEW", "APPROVED", "REJECTED", "FILED", "CLOSED");
    }

    [Fact]
    public void Every_transition_uses_approved_statuses()
    {
        CaseStatusTransitions.All.Should().OnlyContain(t => CaseStatuses.IsValid(t.From) && CaseStatuses.IsValid(t.To) && t.From != t.To);
    }

    [Fact]
    public void Action_vocabulary_is_the_approved_list()
    {
        CaseActions.All.Should().Equal("START_REVIEW", "FLAG", "UNFLAG", "CLOSE", "APPROVE", "REJECT", "FILE");
        CaseActions.Transitions.Should().Equal("START_REVIEW", "FLAG", "UNFLAG", "CLOSE");
    }

    [Theory]
    [InlineData("NEW", "START_REVIEW", "UNDER_REVIEW", TransitionActor.Analyst)]
    [InlineData("FLAGGED", "START_REVIEW", "UNDER_REVIEW", TransitionActor.Analyst)]
    [InlineData("REJECTED", "CLOSE", "CLOSED", TransitionActor.Analyst)]
    [InlineData("FILED", "CLOSE", "CLOSED", TransitionActor.Admin)]
    [InlineData("UNDER_REVIEW", "APPROVE", "APPROVED", TransitionActor.ReviewDecision)]
    [InlineData("UNDER_REVIEW", "REJECT", "REJECTED", TransitionActor.ReviewDecision)]
    [InlineData("APPROVED", "FILE", "FILED", TransitionActor.FilingConfirmation)]
    public void Approved_transitions(string from, string action, string to, TransitionActor actor)
    {
        var transition = CaseStatusTransitions.Find(from, action)!;
        transition.To.Should().Be(to);
        transition.Actor.Should().Be(actor);
    }

    [Theory]
    [InlineData("FLAGGED", "FILE")]
    [InlineData("NEW", "APPROVE")]
    [InlineData("CLOSED", "START_REVIEW")]
    [InlineData("UNDER_REVIEW", "CLOSE")]
    [InlineData("NEW", "FLAG")]
    [InlineData("FLAGGED", "UNFLAG")]
    public void Unlisted_transitions_do_not_exist(string from, string action)
    {
        CaseStatusTransitions.Find(from, action).Should().BeNull();
    }

    [Fact]
    public void Consequential_transitions_are_never_routed_to_the_transitions_endpoint()
    {
        CaseStatusTransitions.All.Where(t => t.To == CaseStatuses.Filed).Should().OnlyContain(t => t.Actor == TransitionActor.FilingConfirmation);
        CaseStatusTransitions.All.Where(t => t.To is CaseStatuses.Approved or CaseStatuses.Rejected).Should().OnlyContain(t => t.Actor == TransitionActor.ReviewDecision);
        CaseStatusTransitions.All.Where(t => CaseActions.IsTransition(t.Action)).Should().OnlyContain(t => t.Actor == TransitionActor.Analyst || t.Actor == TransitionActor.Admin);
    }

    [Theory]
    [InlineData("NEW", "START_REVIEW")]
    [InlineData("FLAGGED", "START_REVIEW")]
    [InlineData("UNDER_REVIEW", "")]
    [InlineData("REJECTED", "CLOSE")]
    [InlineData("FILED", "")]
    [InlineData("CLOSED", "")]
    public void Analyst_valid_actions(string status, string expected)
    {
        var analyst = User(UserType.Processor, Permissions.ViewCases, Permissions.UpdateCaseStatus);

        CaseStatusTransitions.ValidActions(status, analyst).Should().Equal(expected.Length == 0 ? [] : [expected]);
    }

    [Fact]
    public void Admin_can_close_a_filed_case()
    {
        CaseStatusTransitions.ValidActions(CaseStatuses.Filed, User(UserType.Admin, Permissions.UpdateCaseStatus)).Should().Equal(CaseActions.Close);
    }

    [Fact]
    public void Review_and_filing_actions_follow_their_own_permissions()
    {
        var reviewer = User(UserType.Processor, Permissions.ReviewCase, Permissions.SubmitMastercom);

        CaseStatusTransitions.ValidActions(CaseStatuses.UnderReview, reviewer).Should().Equal(CaseActions.Approve, CaseActions.Reject);
        CaseStatusTransitions.ValidActions(CaseStatuses.Approved, reviewer).Should().Equal(CaseActions.File);
        CaseStatusTransitions.ValidActions(CaseStatuses.New, reviewer).Should().BeEmpty("START_REVIEW needs UPDATE_CASE_STATUS");
    }

    [Fact]
    public void Bank_users_never_get_actions()
    {
        var bankUser = User(UserType.Bank, Permissions.UpdateCaseStatus, Permissions.ReviewCase, Permissions.SubmitMastercom);

        CaseStatuses.All.SelectMany(s => CaseStatusTransitions.ValidActions(s, bankUser)).Should().BeEmpty();
    }

    private static FakeCurrentUser User(UserType type, params string[] permissions)
    {
        var user = new FakeCurrentUser { UserType = type };
        user.PermissionSet.UnionWith(permissions);
        return user;
    }
}

public sealed class CaseValidatorTests
{
    [Theory]
    [InlineData("START_REVIEW", null, 1u, true)]
    [InlineData("START_REVIEW", "picked up", 1u, true)]
    [InlineData("START_REVIEW", "picked up", null, false)]
    [InlineData("CLOSE", "client notified", 1u, true)]
    [InlineData("CLOSE", "", 1u, false)]
    [InlineData("CLOSE", null, 1u, false)]
    [InlineData("APPROVE", null, 1u, true)]   // known action: refused by the handler with 422, not by validation
    [InlineData("OPEN", null, 1u, false)]
    [InlineData("start_review", null, 1u, false)]
    [InlineData("START_REVIEW", "card 4111111111111111", 1u, false)]
    [InlineData("START_REVIEW", "store ref 1234567890123", 1u, true)]
    public void Transition_needs_a_known_action_a_version_and_a_rationale_where_required(string action, string? rationale, uint? version, bool valid)
    {
        new TransitionCaseValidator().Validate(new TransitionCaseCommand(Guid.NewGuid(), new TransitionCaseRequest(action, rationale, version)))
            .IsValid.Should().Be(valid);
    }

    [Fact]
    public void Transition_rationale_is_bounded()
    {
        new TransitionCaseValidator().Validate(new TransitionCaseCommand(Guid.NewGuid(), new TransitionCaseRequest("START_REVIEW", new string('r', 1001), 1)))
            .IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("facts corrected", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Retriage_needs_a_reason(string? reason, bool valid)
    {
        new RetriageCaseValidator().Validate(new RetriageCaseCommand(Guid.NewGuid(), new RetriageRequest(reason))).IsValid.Should().Be(valid);
    }

    [Fact]
    public void Retriage_reason_is_bounded()
    {
        new RetriageCaseValidator().Validate(new RetriageCaseCommand(Guid.NewGuid(), new RetriageRequest(new string('r', 1001)))).IsValid.Should().BeFalse();
    }
}
