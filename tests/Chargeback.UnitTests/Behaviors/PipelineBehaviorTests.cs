using Chargeback.Api;
using Chargeback.Api.Common.Behaviors;
using Chargeback.Api.Common.Security;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chargeback.UnitTests.Behaviors;

public sealed class ValidationBehaviorTests
{
    private sealed class Validator : AbstractValidator<ValidatedTestCommand>
    {
        public Validator()
        {
            RuleFor(x => x.Name).NotEmpty();
            RuleFor(x => x.Count).InclusiveBetween(1, 10);
        }
    }

    [Fact]
    public async Task Invalid_request_short_circuits_with_camel_case_errors()
    {
        var called = false;
        var behavior = new ValidationBehavior<ValidatedTestCommand, Result<int>>([new Validator()]);

        var result = await behavior.Handle(new ValidatedTestCommand(null, 0), _ =>
        {
            called = true;
            return Task.FromResult(Result.Success(1));
        }, CancellationToken.None);

        called.Should().BeFalse();
        result.Error.Type.Should().Be(ErrorType.Validation);
        result.Error.ValidationErrors.Should().ContainKeys("name", "count");
    }

    [Fact]
    public async Task Valid_request_reaches_handler()
    {
        var behavior = new ValidationBehavior<ValidatedTestCommand, Result<int>>([new Validator()]);

        var result = await behavior.Handle(new ValidatedTestCommand("x", 5), _ => Task.FromResult(Result.Success(7)), CancellationToken.None);

        result.Value.Should().Be(7);
    }
}

public sealed class TransactionBehaviorTests
{
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUnitOfWorkTransaction _tx = Substitute.For<IUnitOfWorkTransaction>();

    public TransactionBehaviorTests() => _uow.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_tx);

    [Fact]
    public async Task Success_saves_and_commits_with_external_calls_blocked_inside()
    {
        var insideTransaction = false;
        var behavior = new TransactionBehavior<TransactionalTestCommand, Result>(_uow);

        var result = await behavior.Handle(new TransactionalTestCommand(), _ =>
        {
            insideTransaction = ExternalCallGuard.IsInDatabaseTransaction;
            return Task.FromResult(Result.Success());
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        insideTransaction.Should().BeTrue();
        ExternalCallGuard.IsInDatabaseTransaction.Should().BeFalse();
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _tx.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await _tx.DidNotReceive().RollbackAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failure_result_rolls_back_without_saving()
    {
        var behavior = new TransactionBehavior<TransactionalTestCommand, Result>(_uow);

        await behavior.Handle(new TransactionalTestCommand(), _ => Task.FromResult(Result.Failure(Error.Conflict("X", "x"))), CancellationToken.None);

        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _tx.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        await _tx.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Exception_rolls_back_and_rethrows()
    {
        var behavior = new TransactionBehavior<TransactionalTestCommand, Result>(_uow);

        var act = () => behavior.Handle(new TransactionalTestCommand(), _ => throw new InvalidOperationException("boom"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _tx.Received(1).RollbackAsync(Arg.Any<CancellationToken>());
        ExternalCallGuard.IsInDatabaseTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task Non_transactional_request_opens_no_transaction()
    {
        var behavior = new TransactionBehavior<PlainTestCommand, Result>(_uow);

        await behavior.Handle(new PlainTestCommand(), _ => Task.FromResult(Result.Success()), CancellationToken.None);

        await _uow.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void External_call_guard_throws_only_inside_transaction_scope()
    {
        ExternalCallGuard.ThrowIfInDatabaseTransaction("S3");

        using (ExternalCallGuard.EnterDatabaseTransaction())
        {
            var act = () => ExternalCallGuard.ThrowIfInDatabaseTransaction("S3");
            act.Should().Throw<InvalidOperationException>().WithMessage("*S3*outbox*");
        }
    }
}

public sealed class PipelineOrderTests
{
    [Fact]
    public void Behaviors_run_in_the_approved_order()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton(Substitute.For<IResourceBankResolver>());
        services.AddSingleton(Substitute.For<Chargeback.Infrastructure.Persistence.IDatabaseScope>());
        services.AddSingleton(Substitute.For<Chargeback.Infrastructure.Persistence.Queries.IDapperQueryService>());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var user = new FakeCurrentUser();
        services.AddSingleton<ICurrentUser>(user);
        services.AddSingleton<ICurrentUserLoader>(user);
        services.AddChargebackApplication();

        using var provider = services.BuildServiceProvider();
        var behaviors = provider.GetServices<IPipelineBehavior<PlainTestCommand, Result>>().Select(b => b.GetType().GetGenericTypeDefinition());

        behaviors.Should().Equal(
            typeof(LoggingBehavior<,>),
            typeof(ValidationBehavior<,>),
            typeof(AuthorizationBehavior<,>),
            typeof(RlsSetupBehavior<,>),
            typeof(IdempotencyBehavior<,>),
            typeof(TransactionBehavior<,>));
    }

    [Fact]
    public async Task Logging_behavior_passes_through_result()
    {
        var behavior = new LoggingBehavior<PlainTestCommand, Result>(NullLogger<LoggingBehavior<PlainTestCommand, Result>>.Instance);

        var result = await behavior.Handle(new PlainTestCommand(), _ => Task.FromResult(Result.Failure(Error.Conflict("C", "c"))), CancellationToken.None);

        result.Error.Code.Should().Be("C");
    }
}
