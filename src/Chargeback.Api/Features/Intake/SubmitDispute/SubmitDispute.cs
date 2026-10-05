using System.Text.Json.Serialization;
using Chargeback.Api.Common.Idempotency;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Intake.Contracts;
using Chargeback.Api.Features.Intake.Gates;
using Chargeback.Api.Features.Intake.Normalisation;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.SharedKernel.Events;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using Chargeback.SharedKernel.ValueObjects;
using FluentValidation;
using MediatR;

namespace Chargeback.Api.Features.Intake.SubmitDispute;

/// <summary>
/// Portal single-dispute intake. Deliberately <b>not</b> an <see cref="ITransactionalCommand"/>: gates run
/// first, outside any transaction (future gates may call external systems), then the dispute, its gate
/// results, outbox events and the idempotency record (ADR-0106, atomic mode) are written in one SaveChanges.
/// </summary>
[RequirePermission(Permissions.CreateDispute)]
[Idempotent("submitDispute", IdempotencyMode.Atomic, StatusCodes.Status202Accepted)]
public sealed record SubmitDisputeCommand(SubmitDisputeRequest Body, [property: JsonIgnore] string? IdempotencyKey = null)
    : ICommand<DisputeAcceptedResponse>, IBankScopedRequest, IIdempotentCommand
{
    public Guid BankId => Body.BankId;
}

/// <summary>
/// Request-shape checks only (no database access, no business rules). Which fields are required is
/// Gate 1 (Required Fields), not validation. Card data must be masked; card numbers are rejected in any field.
/// </summary>
public sealed class SubmitDisputeValidator : AbstractValidator<SubmitDisputeCommand>
{
    public const string ContainsCardNumber = "Must not contain a card number.";

    public SubmitDisputeValidator()
    {
        RuleFor(x => x.Body).NotNull();
        When(x => x.Body is not null, () =>
        {
            RuleFor(x => x.Body.BankId).NotEmpty().OverridePropertyName("bankId");

            RuleFor(x => x.Body.CardNumberMasked)
                .Must(v => CardMasked.Create(v).IsSuccess)
                .When(x => !string.IsNullOrWhiteSpace(x.Body.CardNumberMasked))
                .WithMessage("Card number must be masked to the last four digits, e.g. ************1234.")
                .OverridePropertyName("cardNumberMasked");

            RuleFor(x => x.Body.CurrencyCode)
                .Must(v => CurrencyCode.Create(v).IsSuccess)
                .When(x => !string.IsNullOrWhiteSpace(x.Body.CurrencyCode))
                .WithMessage("Currency code must be three letters (ISO 4217 format).")
                .OverridePropertyName("currencyCode");

            RuleFor(x => x.Body.TransactionAmount)
                .GreaterThanOrEqualTo(0m)
                .Must(v => v is null || decimal.Round(v.Value, Money.MaxScale) == v.Value).WithMessage("Amount must have at most 2 decimal places.")
                .LessThanOrEqualTo(9_999_999_999_999_999.99m)
                .OverridePropertyName("transactionAmount");

            FreeText(x => x.Body.CardholderReference, "cardholderReference", 100);
            FreeText(x => x.Body.AcquirerReferenceNumber, "acquirerReferenceNumber", 100);
            FreeText(x => x.Body.MerchantName, "merchantName", 200);
        });
    }

    private void FreeText(System.Linq.Expressions.Expression<Func<SubmitDisputeCommand, string?>> field, string name, int maxLength) =>
        RuleFor(field)
            .MaximumLength(maxLength)
            .Must(v => !PanRedactor.ContainsPan(v)).WithMessage(ContainsCardNumber)
            .OverridePropertyName(name);
}

/// <summary>Raised when a dispute is persisted, whatever its gate outcome.</summary>
public sealed record DisputeReceived : DomainEvent
{
    public override string EventType => "dispute.received";

    public required IntakeChannel Channel { get; init; }
}

/// <summary>Gate trail summary for downstream consumers (triage starts only for status NEW).</summary>
public sealed record DisputeGatesEvaluated : DomainEvent
{
    public override string EventType => "dispute.gates.evaluated";

    public required Guid DisputeId { get; init; }

    public required string RegistryVersion { get; init; }

    public required string Status { get; init; }

    public required IReadOnlyList<int> PassedGates { get; init; }

    public required IReadOnlyList<int> FailedGates { get; init; }

    public required IReadOnlyList<int> UndecidedGates { get; init; }

    public required IReadOnlyList<int> PendingDefinitionGates { get; init; }

    public required IReadOnlyList<int> TechnicalErrorGates { get; init; }
}

/// <summary>Raised when any gate did not pass: the dispute goes to the analyst queue.</summary>
public sealed record DisputeFlagged : DomainEvent
{
    public override string EventType => "dispute.flagged";

    public required Guid DisputeId { get; init; }
}

internal sealed class SubmitDisputeHandler(ChargebackDbContext db, IGateEngine gates, IdempotencyContext idempotency)
    : IRequestHandler<SubmitDisputeCommand, Result<DisputeAcceptedResponse>>
{
    public async Task<Result<DisputeAcceptedResponse>> Handle(SubmitDisputeCommand request, CancellationToken cancellationToken)
    {
        // Portal users are the only authenticated intake callers today; the direct-API channel needs an
        // approved machine-to-machine identity first (open question), so the channel is not client-supplied.
        var package = NormalisedIntakePackage.FromRequest(request.Body, IntakeChannel.Portal);
        var evaluation = await gates.EvaluateAsync(package, cancellationToken);

        var dispute = new Dispute
        {
            BankId = package.BankId,
            CardholderReference = package.CardholderReference,
            CardNumberMasked = package.CardNumber?.Value,
            TransactionDate = package.TransactionDate,
            TransactionAmount = package.TransactionAmount,
            CurrencyCode = package.Currency?.Value,
            AcquirerReferenceNumber = package.AcquirerReferenceNumber,
            MerchantName = package.MerchantName,
            IntakeChannel = package.Channel.ToString().ToUpperInvariant(),
            Status = evaluation.DisputeStatus,
        };

        db.Disputes.Add(dispute);
        db.GateResults.AddRange(evaluation.Results.Select(r => new GateResultRecord
        {
            DisputeId = dispute.Id,
            GateNumber = r.GateNumber,
            GateName = r.GateName,
            Passed = r.Passed,
            FlagReason = r.FlagReason,
            CheckedAt = r.CheckedAt,
        }));

        dispute.AddDomainEvent(new DisputeReceived { BankId = dispute.BankId, Channel = package.Channel });
        dispute.AddDomainEvent(new DisputeGatesEvaluated
        {
            BankId = dispute.BankId,
            DisputeId = dispute.Id,
            RegistryVersion = evaluation.RegistryVersion,
            Status = evaluation.DisputeStatus,
            PassedGates = evaluation.Results.Where(r => r.Passed == true).Select(r => r.GateNumber).ToArray(),
            FailedGates = evaluation.Results.Where(r => r.Passed == false).Select(r => r.GateNumber).ToArray(),
            UndecidedGates = evaluation.Results.Where(r => r.Passed is null).Select(r => r.GateNumber).ToArray(),
            PendingDefinitionGates = evaluation.GatesWith(GateExecutionStatus.PendingDefinition),
            TechnicalErrorGates = evaluation.GatesWith(GateExecutionStatus.TechnicalError),
        });
        if (!evaluation.AllPassed)
        {
            dispute.AddDomainEvent(new DisputeFlagged { BankId = dispute.BankId, DisputeId = dispute.Id });
        }

        var response = new DisputeAcceptedResponse(dispute.Id, dispute.Status);
        idempotency.RecordCompleted(db, response, dispute.Id);

        // One SaveChanges = one database transaction for dispute + gate results + outbox rows + idempotency record.
        await db.SaveChangesAsync(cancellationToken);

        return response;
    }
}
