using Chargeback.Api.Common.Idempotency;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Intake.SubmitDispute;
using Chargeback.Infrastructure.Maintenance;
using Chargeback.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace Chargeback.UnitTests.Idempotency;

public sealed class IdempotencyHashTests
{
    private static readonly Guid Bank = Guid.NewGuid();

    private static SubmitDisputeCommand Command(string merchant = "Shop", decimal? amount = 10m, string? key = "key-12345678") =>
        new(new SubmitDisputeRequest(Bank, "CH-1", null, null, amount, "GBP", null, merchant), key);

    [Fact]
    public void Same_request_gives_the_same_hash_regardless_of_key()
    {
        IdempotencyHash.Compute("submitDispute", Command(key: "key-aaaaaaaa"))
            .Should().Be(IdempotencyHash.Compute("submitDispute", Command(key: "key-bbbbbbbb")), "the key itself is not part of the request");
    }

    [Fact]
    public void Any_field_change_changes_the_hash()
    {
        var baseline = IdempotencyHash.Compute("submitDispute", Command());

        IdempotencyHash.Compute("submitDispute", Command(merchant: "Other")).Should().NotBe(baseline);
        IdempotencyHash.Compute("submitDispute", Command(amount: 10.01m)).Should().NotBe(baseline);
        IdempotencyHash.Compute("confirmFiling", Command()).Should().NotBe(baseline, "the operation is part of the hash");
    }

    [Theory]
    [InlineData("5")]
    [InlineData("5.0")]
    [InlineData("5.00")]
    public void Decimal_scale_does_not_change_the_hash(string amount)
    {
        IdempotencyHash.Compute("submitDispute", Command(amount: decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)))
            .Should().Be(IdempotencyHash.Compute("submitDispute", Command(amount: 5m)));
    }

    [Fact]
    public void Hash_is_sha256_hex()
    {
        IdempotencyHash.Compute("submitDispute", Command()).Should().MatchRegex("^[0-9a-f]{64}$");
    }
}

public sealed class IdempotencyContextTests
{
    private readonly IdempotencyContext _context = new(TimeProvider.System, Options.Create(new IdempotencyOptions()));

    [Fact]
    public void Replay_rebuilds_the_success_result()
    {
        var result = ResultReplay<Result<DisputeAcceptedResponse>>.FromJson("""{"disputeId":"0192d000-0000-7000-8000-000000000001","status":"FLAGGED"}""");

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("FLAGGED");
    }

    [Fact]
    public void Replay_requires_a_value_result()
    {
        var act = () => ResultReplay<Result>.FromJson("{}");

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("""{"disputeId":"0192d000-1234-5678-9012-345678901234","status":"NEW"}""", false)] // all-digit UUID groups: not card data
    [InlineData("""{"disputeId":"0192d000-0000-7000-8000-000000000001","status":"FLAGGED"}""", false)]
    [InlineData("""{"card":"4111111111111111"}""", true)]
    [InlineData("""{"note":"ref 4111 1111 1111 1111"}""", true)]
    [InlineData("""{"nested":{"items":["x","5500005555555559"]}}""", true)]
    [InlineData("""{"amount":4111111111111111}""", true)]
    [InlineData("""{"loyalty":"9876543210987654"}""", true)] // conservative: Luhn not required for storage
    public void Stored_response_guard_checks_values_and_skips_guids(string json, bool expected)
    {
        IdempotencyContext.ContainsCardLikeValue(json).Should().Be(expected);
    }
    [Fact]
    public void Inactive_context_records_nothing()
    {
        _context.RecordCompleted(null!, new object(), null);
    }
}

public sealed class IdempotencyDeclarationTests
{
    [Fact]
    public void Idempotent_requests_carry_the_key_and_the_key_is_excluded_from_hashing()
    {
        var declared = typeof(SubmitDisputeCommand).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(IdempotentAttribute), false).Length > 0)
            .ToArray();

        declared.Should().Contain(typeof(SubmitDisputeCommand));
        foreach (var type in declared)
        {
            typeof(IIdempotentCommand).IsAssignableFrom(type).Should().BeTrue($"{type.Name} must implement IIdempotentCommand");
            type.GetProperty(nameof(IIdempotentCommand.IdempotencyKey))!
                .GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false).Should().NotBeEmpty($"{type.Name}.IdempotencyKey must be [JsonIgnore]");
        }
    }

    [Fact]
    public void Submit_dispute_is_atomic_mode_with_202()
    {
        var attribute = (IdempotentAttribute)typeof(SubmitDisputeCommand).GetCustomAttributes(typeof(IdempotentAttribute), false).Single();

        attribute.Operation.Should().Be("submitDispute");
        attribute.Mode.Should().Be(IdempotencyMode.Atomic);
        attribute.SuccessStatusCode.Should().Be(202);
    }
}

public sealed class PurgeScheduleTests
{
    [Theory]
    [InlineData("2026-09-29T01:00:00Z", "02:00", 1.0)]
    [InlineData("2026-09-29T02:00:00Z", "02:00", 24.0)]
    [InlineData("2026-09-29T23:30:00Z", "02:00", 2.5)]
    public void Next_run_is_the_next_configured_utc_time(string now, string at, double expectedHours)
    {
        IdempotencyKeyPurgeJob.DelayUntilNextRun(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), at)
            .Should().Be(TimeSpan.FromHours(expectedHours));
    }

    [Fact]
    public void Approved_defaults_are_90_days_5000_rows_48_hours()
    {
        var options = new IdempotencyOptions();

        options.TimeToLive.Should().Be(TimeSpan.FromDays(90));
        options.ProcessedEventRetention.Should().Be(TimeSpan.FromDays(90));
        options.PurgeBatchSize.Should().Be(5_000);
        options.HealthLagThreshold.Should().Be(TimeSpan.FromHours(48));
    }
}
