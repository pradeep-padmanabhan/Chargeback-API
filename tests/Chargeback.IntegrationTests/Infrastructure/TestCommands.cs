using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Security;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.SharedKernel.Events;
using Chargeback.SharedKernel.Results;
using MediatR;

namespace Chargeback.IntegrationTests.Infrastructure;

public enum TestOutcome
{
    Succeed,
    ReturnFailure,
    Throw,
}

/// <summary>Test-only transactional command used to exercise TransactionBehavior + outbox end to end.</summary>
[AllowAnyPlatformUser]
[NotBankScoped("test-only command")]
public sealed record CreateTestBankCommand(string BankCode, TestOutcome Outcome) : ICommand<Guid>, ITransactionalCommand;

public sealed record TestBankCreated : DomainEvent
{
    public override string EventType => "test.bank.created";

    public required string BankCode { get; init; }
}

public sealed class CreateTestBankHandler(ChargebackDbContext db, IDapperQueryService queries) : IRequestHandler<CreateTestBankCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateTestBankCommand request, CancellationToken cancellationToken)
    {
        var bank = new Bank { BankCode = request.BankCode, BankName = "Tx Bank" };
        bank.AddDomainEvent(new TestBankCreated { BankId = bank.Id, BankCode = request.BankCode });
        db.Banks.Add(bank);
        await db.SaveChangesAsync(cancellationToken);

        // Dapper joins the open EF transaction and sees the uncommitted row.
        var visible = await queries.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM chargeback_diagram.banks WHERE id = @Id", new { bank.Id }, cancellationToken);
        if (visible != 1)
        {
            return Error.Failure("NOT_VISIBLE", "Dapper did not join the EF transaction.");
        }

        return request.Outcome switch
        {
            TestOutcome.ReturnFailure => Error.Conflict("TEST_FAILURE", "Deliberate failure."),
            TestOutcome.Throw => throw new InvalidOperationException("Deliberate exception."),
            _ => bank.Id,
        };
    }
}
