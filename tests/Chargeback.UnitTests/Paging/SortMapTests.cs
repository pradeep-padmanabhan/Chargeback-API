using Chargeback.Api.Common.Paging;
using Chargeback.Api.Features.Admin;
using Chargeback.Api.Features.Cases.GetCases;
using Chargeback.Api.Features.ClientPortal;
using Chargeback.Api.Features.Review.Workspace;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.Results;

namespace Chargeback.UnitTests.Paging;

public sealed class SortMapTests
{
    private static readonly SortMap Map = new("c.id", ("createdAt", "c.created_at"), ("caseReference", "c.case_reference"));

    [Fact]
    public void Default_sort_is_created_at_descending_with_a_stable_tie_breaker()
    {
        Map.OrderBy(new PageRequest()).Should().Be("ORDER BY c.created_at DESC NULLS LAST, c.id DESC");
    }

    [Theory]
    [InlineData("caseReference", "asc", "ORDER BY c.case_reference ASC NULLS LAST, c.id ASC")]
    [InlineData("caseReference", "ASC", "ORDER BY c.case_reference ASC NULLS LAST, c.id ASC")]
    [InlineData("caseReference", null, "ORDER BY c.case_reference DESC NULLS LAST, c.id DESC")]
    [InlineData(null, "asc", "ORDER BY c.created_at ASC NULLS LAST, c.id ASC")]
    [InlineData("  createdAt ", " desc ", "ORDER BY c.created_at DESC NULLS LAST, c.id DESC")]
    public void Supported_fields_map_to_trusted_columns(string? sortBy, string? direction, string expected)
    {
        Map.OrderBy(new PageRequest(sortBy: sortBy, sortDirection: direction)).Should().Be(expected);
    }

    [Theory]
    [InlineData("case_reference")]   // snake_case is not the contract
    [InlineData("CaseReference")]    // case-sensitive camelCase
    [InlineData("c.id; DROP TABLE cases")]
    public void Unsupported_fields_are_invalid_sort_field(string sortBy)
    {
        var error = Map.Validate(new PageRequest(sortBy: sortBy))!;

        error.Code.Should().Be("INVALID_SORT_FIELD");
        error.Type.Should().Be(ErrorType.Failure); // 400
        error.Message.Should().Contain("createdAt, caseReference");
    }

    [Fact]
    public void Unsupported_direction_is_a_validation_error()
    {
        var error = Map.Validate(new PageRequest(sortDirection: "up"))!;

        error.Type.Should().Be(ErrorType.Validation);
        error.ValidationErrors.Should().ContainKey("sortDirection");
    }

    [Fact]
    public void Ordering_an_unvalidated_sort_is_refused()
    {
        var act = () => Map.OrderBy(new PageRequest(sortBy: "bogus"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Every_map_must_support_the_default_field()
    {
        var act = () => new SortMap("c.id", ("caseReference", "c.case_reference"));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0, 20, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, 101, "pageSize")]
    public void Out_of_range_paging_is_a_validation_error_not_clamped(int page, int pageSize, string field)
    {
        var error = PagingValidation.Validate(new PageRequest(page, pageSize), Map)!;

        error.Code.Should().Be("VALIDATION_FAILED");
        error.ValidationErrors.Should().ContainKey(field);
    }

    [Fact]
    public void All_paging_problems_are_reported_together()
    {
        var error = PagingValidation.Validate(new PageRequest(0, 500, sortDirection: "up"), Map)!;

        error.ValidationErrors!.Keys.Should().BeEquivalentTo(["page", "pageSize", "sortDirection"]);
    }

    [Fact]
    public void Unsupported_sort_field_takes_its_own_code()
    {
        PagingValidation.Validate(new PageRequest(0, 20, sortBy: "bogus"), Map)!.Code.Should().Be("INVALID_SORT_FIELD");
    }

    [Fact]
    public void Valid_paging_passes()
    {
        PagingValidation.Validate(new PageRequest(2, 100, "caseReference", "asc"), Map).Should().BeNull();
    }

    [Fact]
    public void Every_request_carrying_a_page_request_is_validated_by_the_pipeline()
    {
        var carriers = typeof(IPagedRequest).Assembly.GetTypes()
            .Where(t => t.GetProperties().Any(p => p.PropertyType == typeof(PageRequest)))
            .ToArray();

        carriers.Should().NotBeEmpty().And.OnlyContain(t => typeof(IPagedRequest).IsAssignableFrom(t), "an unvalidated PageRequest could reach SQL out of range");
    }

    [Fact]
    public void Admin_banks_default_to_bank_name_ascending_but_honour_an_explicit_sort()
    {
        ListBanksQuery.Sorts.OrderBy(new PageRequest()).Should().Be("ORDER BY b.bank_name ASC NULLS LAST, b.id ASC");
        ListBanksQuery.Sorts.OrderBy(new PageRequest(sortBy: "createdAt")).Should().Be("ORDER BY b.created_at DESC NULLS LAST, b.id DESC");
        ListBanksQuery.Sorts.OrderBy(new PageRequest(sortDirection: "desc")).Should().Be("ORDER BY b.bank_name DESC NULLS LAST, b.id DESC");
    }

    [Fact]
    public void Default_sort_must_be_a_supported_field() =>
        ((Action)(() => new SortMap("c.id", ("createdAt", "c.created_at")).WithDefaultSort("bogus", SortMap.Ascending))).Should().Throw<ArgumentException>();

    [Fact]
    public void Every_list_endpoint_declares_created_at()
    {
        SortMap[] maps = [ListBanksQuery.Sorts, ListBankUsersQuery.Sorts, ListCasesQuery.Sorts, ListPortalCasesQuery.Sorts, GetReviewQueueQuery.Sorts];

        maps.Should().OnlyContain(m => m.Fields.Contains(SortMap.DefaultField));
    }
}
