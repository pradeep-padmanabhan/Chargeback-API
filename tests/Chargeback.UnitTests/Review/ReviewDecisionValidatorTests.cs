using Chargeback.Api.Features.Review.Contracts;
using Chargeback.Api.Features.Review.Decision;

namespace Chargeback.UnitTests.Review;

public sealed class ReviewDecisionValidatorTests
{
    private static readonly Guid Code = Guid.NewGuid();

    public static TheoryData<ReviewDecision?, string?, Guid?, uint?, bool> Cases() => new()
    {
        { ReviewDecision.Approve, "evidence supports it", Code, 1u, true },
        { ReviewDecision.Reject, "no basis", null, 1u, true },
        { null, "x", null, 1u, false },                                   // decision is required (never defaults to Approve)
        { (ReviewDecision)7, "x", null, 1u, false },
        { ReviewDecision.Approve, "x", null, 1u, false },                 // approve confirms the derived code
        { ReviewDecision.Reject, "x", Code, 1u, false },                  // reasonCodeId applies only to approve
        { ReviewDecision.Reject, "", null, 1u, false },
        { ReviewDecision.Reject, null, null, 1u, false },
        { ReviewDecision.Reject, "x", null, null, false },
        { ReviewDecision.Reject, "card 4111111111111111", null, 1u, false },
        { ReviewDecision.Reject, new string('r', 4001), null, 1u, false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Decision_shape(ReviewDecision? decision, string? rationale, Guid? reasonCodeId, uint? version, bool valid)
    {
        new SubmitReviewDecisionValidator()
            .Validate(new SubmitReviewDecisionCommand(Guid.NewGuid(), new ReviewDecisionRequest(decision, rationale, reasonCodeId, version)))
            .IsValid.Should().Be(valid);
    }
}
