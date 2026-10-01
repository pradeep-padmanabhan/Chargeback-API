using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Results;
using Chargeback.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Chargeback.UnitTests.Http;

public sealed class ResultHttpMapperTests
{
    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.Failure, 400)]
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.NotImplemented, 501)]
    public void Maps_error_types_to_status_codes(ErrorType type, int expected)
    {
        ResultHttpMapper.StatusCodeFor(type).Should().Be(expected);
    }

    [Fact]
    public void Problem_carries_code_and_trace_id()
    {
        var http = new DefaultHttpContext();
        CorrelationId.Set(http, "corr-123");

        var result = ResultHttpMapper.ToProblem(Errors.ResourceNotFound, http);

        var problem = result.Should().BeOfType<ProblemHttpResult>().Subject.ProblemDetails;
        problem.Status.Should().Be(404);
        problem.Extensions["code"].Should().Be("RESOURCE_NOT_FOUND");
        problem.Extensions["traceId"].Should().Be("corr-123");
        problem.Extensions.Should().NotContainKey("correlationId");
    }

    [Fact]
    public void Validation_error_becomes_validation_problem()
    {
        var error = Error.Validation(new Dictionary<string, string[]> { ["name"] = ["required"] });

        var result = ResultHttpMapper.ToProblem(error, new DefaultHttpContext());

        result.Should().BeOfType<ValidationProblem>().Subject.ProblemDetails.Errors.Should().ContainKey("name");
    }

    [Theory]
    [InlineData("abc-123_X.9", "abc-123_X.9")]
    [InlineData(null, null)]
    [InlineData("has space", null)]
    [InlineData("<script>", null)]
    public void Correlation_id_accepts_only_safe_values(string? candidate, string? expected)
    {
        var normalized = CorrelationId.Normalize(candidate);

        if (expected is null)
        {
            normalized.Should().MatchRegex("^[0-9a-f]{32}$");
        }
        else
        {
            normalized.Should().Be(expected);
        }
    }
}
