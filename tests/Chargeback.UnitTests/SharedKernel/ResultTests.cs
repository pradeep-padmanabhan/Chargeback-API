using Chargeback.Api.Common.Results;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.Results;

namespace Chargeback.UnitTests.SharedKernel;

public sealed class ResultTests
{
    private static readonly Error SampleError = Error.Conflict("SAMPLE", "sample");

    [Fact]
    public void Success_exposes_value_and_no_error()
    {
        var result = Result.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Invoking(r => r.Error).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Failure_exposes_error_and_no_value()
    {
        Result<int> result = SampleError;

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SampleError);
        result.Invoking(r => r.Value).Should().Throw<InvalidOperationException>().WithMessage("*SAMPLE*");
    }

    [Fact]
    public void ResultFailure_creates_failed_non_generic_result()
    {
        var result = ResultFailure<Result>.Create(SampleError);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SAMPLE");
    }

    [Fact]
    public void ResultFailure_creates_failed_generic_result()
    {
        var result = ResultFailure<Result<string>>.Create(SampleError);

        result.Should().BeOfType<Result<string>>();
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void ResultFailure_rejects_non_result_response_types()
    {
        var act = () => ResultFailure<string>.Create(SampleError);

        act.Should().Throw<InvalidOperationException>().WithMessage("*must return Result*");
    }

    [Theory]
    [InlineData(null, null, 1, 25)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(3, 500, 3, 100)]
    [InlineData(-2, 10, 1, 10)]
    public void PageRequest_clamps_to_allowed_range(int? page, int? size, int expectedPage, int expectedSize)
    {
        var request = new PageRequest(page, size);

        request.Page.Should().Be(expectedPage);
        request.PageSize.Should().Be(expectedSize);
        request.Offset.Should().Be((expectedPage - 1) * expectedSize);
    }

    [Fact]
    public void PagedResult_computes_total_pages()
    {
        new PagedResult<int>([], 1, 25, 51).TotalPages.Should().Be(3);
    }
}
