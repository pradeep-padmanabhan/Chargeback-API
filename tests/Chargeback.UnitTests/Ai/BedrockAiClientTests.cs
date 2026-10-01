using Chargeback.Infrastructure.Ai;
using Chargeback.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Chargeback.UnitTests.Ai;

public sealed class BedrockAiClientTests
{
    private readonly IBedrockTransport _transport = Substitute.For<IBedrockTransport>();
    private readonly IAiDecisionLogWriter _audit = Substitute.For<IAiDecisionLogWriter>();
    private readonly AiOptions _options = new()
    {
        Enabled = true,
        EnabledCapabilities = [AiCapabilities.TriageSummary],
        ModelId = "test-model",
        Timeout = TimeSpan.FromMilliseconds(200),
        MaxAttempts = 2,
    };

    private static AiInvocation Invocation(string input = "Summarize case", Guid? bankId = null) =>
        new(AiAgents.AnalysisExplanation, AiCapabilities.TriageSummary, "triage-summary", "1", input, Guid.NewGuid(), bankId ?? Guid.NewGuid());

    private BedrockAiClient CreateClient() =>
        new(_transport, _audit, Options.Create(_options), new FakeTimeProvider(), NullLogger<BedrockAiClient>.Instance);

    private sealed record Summary(string SummaryText);

    [Fact]
    public async Task Masks_pan_before_model_input_and_uses_temperature_zero()
    {
        BedrockTransportRequest? sent = null;
        _transport.InvokeAsync(Arg.Do<BedrockTransportRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new BedrockTransportResponse("""{"summaryText":"ok"}""", "test-model", 10, 5));

        var result = await CreateClient().InvokeAsync<Summary>(Invocation("card 4111111111111111"), null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        sent!.MaskedInput.Should().Be("card ************1111");
        sent.Temperature.Should().Be(0m);
    }

    [Fact]
    public async Task Audits_every_invocation_with_masked_response()
    {
        AiDecisionLogEntry? logged = null;
        await _audit.WriteAsync(Arg.Do<AiDecisionLogEntry>(e => logged = e), Arg.Any<CancellationToken>());
        _transport.InvokeAsync(default!, default).ReturnsForAnyArgs(
            new BedrockTransportResponse("""{"summaryText":"echo 4111111111111111"}""", "test-model", 10, 5));

        var result = await CreateClient().InvokeAsync<Summary>(Invocation(), null, CancellationToken.None);

        result.Output!.SummaryText.Should().Be("echo ************1111");
        logged!.PromptTemplateId.Should().Be("triage-summary@1");
        logged.RawResponseJson.Should().NotContain("4111111111111111");
        logged.ParsedOutputJson.Should().NotContain("4111111111111111");
        logged.TokenCountInput.Should().Be(10);
    }

    [Fact]
    public async Task Disabled_capability_never_calls_the_model()
    {
        _options.EnabledCapabilities = [];

        var result = await CreateClient().InvokeAsync<Summary>(Invocation(), null, CancellationToken.None);

        result.Status.Should().Be(AiResultStatus.Disabled);
        await _transport.DidNotReceiveWithAnyArgs().InvokeAsync(default!, default);
    }

    [Fact]
    public async Task Disabled_bank_never_calls_the_model()
    {
        var bank = Guid.NewGuid();
        _options.DisabledBankIds = [bank];

        var result = await CreateClient().InvokeAsync<Summary>(Invocation(bankId: bank), null, CancellationToken.None);

        result.Status.Should().Be(AiResultStatus.Disabled);
    }

    [Fact]
    public async Task Timeout_retries_then_fails_soft()
    {
        _transport.InvokeAsync(default!, default).ReturnsForAnyArgs(async call =>
        {
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return new BedrockTransportResponse("{}", "m", null, null);
        });

        var result = await CreateClient().InvokeAsync<Summary>(Invocation(), null, CancellationToken.None);

        result.Status.Should().Be(AiResultStatus.Unavailable);
        result.FailureReason.Should().Be("TIMEOUT");
        await _transport.ReceivedWithAnyArgs(2).InvokeAsync(default!, default);
    }

    [Fact]
    public async Task Not_configured_transport_fails_soft_without_retry()
    {
        _transport.InvokeAsync(default!, default).ReturnsForAnyArgs<BedrockTransportResponse>(_ => throw new AiUnavailableException("no"));

        var result = await CreateClient().InvokeAsync<Summary>(Invocation(), null, CancellationToken.None);

        result.Status.Should().Be(AiResultStatus.Unavailable);
        await _transport.ReceivedWithAnyArgs(1).InvokeAsync(default!, default);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"summaryText":"x","reasonCode":"4853"}""")]   // unexpected member: model tried to decide
    [InlineData("""{}""")]                                          // required member missing
    public async Task Output_outside_schema_is_discarded(string content)
    {
        _transport.InvokeAsync(default!, default).ReturnsForAnyArgs(new BedrockTransportResponse(content, "m", null, null));

        var result = await CreateClient().InvokeAsync<Summary>(Invocation(), null, CancellationToken.None);

        result.Status.Should().Be(AiResultStatus.InvalidOutput);
        result.Output.Should().BeNull();
    }

    [Fact]
    public async Task Capability_validator_can_reject_output()
    {
        _transport.InvokeAsync(default!, default).ReturnsForAnyArgs(new BedrockTransportResponse("""{"summaryText":""}""", "m", null, null));

        var result = await CreateClient().InvokeAsync<Summary>(Invocation(), s => s.SummaryText.Length > 0, CancellationToken.None);

        result.Status.Should().Be(AiResultStatus.InvalidOutput);
    }

    [Fact]
    public async Task Audit_failure_discards_output()
    {
        _transport.InvokeAsync(default!, default).ReturnsForAnyArgs(new BedrockTransportResponse("""{"summaryText":"ok"}""", "m", null, null));
        _audit.WriteAsync(default!, default).ReturnsForAnyArgs<Task>(_ => throw new InvalidOperationException("db down"));

        var result = await CreateClient().InvokeAsync<Summary>(Invocation(), null, CancellationToken.None);

        result.Status.Should().Be(AiResultStatus.Unavailable);
        result.FailureReason.Should().Be("AUDIT_WRITE_FAILED");
    }

    [Fact]
    public async Task Refuses_to_run_inside_a_database_transaction()
    {
        using var scope = ExternalCallGuard.EnterDatabaseTransaction();

        var act = () => CreateClient().InvokeAsync<Summary>(Invocation(), null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Bedrock*");
    }
}
