using System.Reflection;
using Chargeback.Api;
using Chargeback.Api.Common.Security;
using Chargeback.Infrastructure;
using Chargeback.Infrastructure.Persistence;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using MediatR;
using NetArchTest.Rules;

namespace Chargeback.ArchitectureTests;

public sealed class LayeringTests
{
    private static readonly Assembly SharedKernel = typeof(Result).Assembly;
    private static readonly Assembly Infrastructure = typeof(DependencyInjection).Assembly;

    [Fact]
    public void Shared_kernel_has_no_framework_or_layer_dependencies()
    {
        var result = Types.InAssembly(SharedKernel).ShouldNot().HaveDependencyOnAny(
                "Chargeback.Api", "Chargeback.Infrastructure", "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore",
                "MediatR", "Npgsql", "Dapper", "Serilog", "FluentValidation", "Amazon")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(Describe(result));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_api()
    {
        var result = Types.InAssembly(Infrastructure).ShouldNot().HaveDependencyOn("Chargeback.Api").GetResult();

        result.IsSuccessful.Should().BeTrue(Describe(result));
    }

    [Fact]
    public void Feature_slices_only_share_contracts()
    {
        var api = typeof(ApiServiceRegistration).Assembly;
        var features = api.GetTypes()
            .Select(t => t.Namespace)
            .OfType<string>()
            .Where(ns => ns.StartsWith("Chargeback.Api.Features.", StringComparison.Ordinal))
            .Select(ns => ns.Split('.')[3])
            .Distinct()
            .ToArray();

        features.Should().Contain(["Intake", "Triage", "Cases", "Review", "Documents", "Filing", "ClientPortal", "Admin", "Sdk", "SchemeLifecycle"]);

        foreach (var feature in features)
        {
            // Internal (non-Contracts) type names of every other feature.
            var forbidden = api.GetTypes()
                .Where(t => t.Namespace is { } ns
                    && ns.StartsWith("Chargeback.Api.Features.", StringComparison.Ordinal)
                    && ns.Split('.')[3] != feature
                    && !ns.EndsWith(".Contracts", StringComparison.Ordinal))
                .Select(t => t.FullName!)
                .Where(n => !n.Contains('<', StringComparison.Ordinal))
                .ToArray();

            if (forbidden.Length == 0)
            {
                continue;
            }

            var result = Types.InAssembly(api)
                .That().ResideInNamespace($"Chargeback.Api.Features.{feature}")
                .ShouldNot().HaveDependencyOnAny(forbidden)
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"feature '{feature}' may only use other features' Contracts. {Describe(result)}");
        }
    }

    [Fact]
    public void No_entity_framework_migrations_exist()
    {
        var migrationTypes = new[] { SharedKernel, Infrastructure, typeof(ApiServiceRegistration).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.BaseType?.FullName is "Microsoft.EntityFrameworkCore.Migrations.Migration"
                or "Microsoft.EntityFrameworkCore.Infrastructure.ModelSnapshot");

        migrationTypes.Should().BeEmpty("the baseline schema is owned by the approved SQL script (ADR-0004)");
        Infrastructure.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain("Microsoft.EntityFrameworkCore.Design");
    }

    [Fact]
    public void Transaction_boundary_types_live_in_infrastructure_persistence()
    {
        typeof(IUnitOfWork).Namespace.Should().Be("Chargeback.Infrastructure.Persistence");
        typeof(ExternalCallGuard).Namespace.Should().Be("Chargeback.Infrastructure.Persistence");
    }

    internal static string Describe(TestResult result) =>
        result.IsSuccessful ? "" : "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}

public sealed class RequestConventionTests
{
    private static readonly Type[] RequestTypes = typeof(ApiServiceRegistration).Assembly.GetTypes()
        .Where(t => t is { IsAbstract: false, IsInterface: false }
            && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)))
        .ToArray();

    public static TheoryData<Type> Requests()
    {
        var data = new TheoryData<Type>();
        foreach (var type in RequestTypes)
        {
            data.Add(type);
        }

        return data;
    }

    [Fact]
    public void Requests_exist()
    {
        RequestTypes.Should().HaveCountGreaterThan(40);
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void Every_request_returns_a_result(Type requestType)
    {
        var response = requestType.GetInterfaces()
            .Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
            .GetGenericArguments()[0];

        (response == typeof(Result) || (response.IsGenericType && response.GetGenericTypeDefinition() == typeof(Result<>)))
            .Should().BeTrue($"{requestType.Name} must return Result or Result<T>, not {response.Name}");
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void Every_request_declares_valid_authorization(Type requestType)
    {
        var metadata = RequestAuthorizationMetadata.For(requestType);

        metadata.IsValid.Should().BeTrue($"{requestType.Name}: {string.Join("; ", metadata.Problems)}");
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void Every_request_permission_is_catalogued(Type requestType)
    {
        var metadata = RequestAuthorizationMetadata.For(requestType);
        if (metadata.Permission is { } permission)
        {
            Permissions.Seeded.Concat(Permissions.Proposed).Should().Contain(permission);
        }
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void Every_request_has_exactly_one_handler(Type requestType)
    {
        var response = requestType.GetInterfaces()
            .Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
            .GetGenericArguments()[0];
        var handlerInterface = typeof(IRequestHandler<,>).MakeGenericType(requestType, response);

        var handlers = typeof(ApiServiceRegistration).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && handlerInterface.IsAssignableFrom(t));

        handlers.Should().ContainSingle($"{requestType.Name} needs exactly one handler");
    }

    [Fact]
    public void Handlers_are_internal_and_sealed()
    {
        var result = Types.InAssembly(typeof(ApiServiceRegistration).Assembly)
            .That().ImplementInterface(typeof(IRequestHandler<,>))
            .And().AreNotAbstract()
            .Should().NotBePublic().And().BeSealed()
            .GetResult();

        result.IsSuccessful.Should().BeTrue(LayeringTests.Describe(result));
    }

    [Fact]
    public void Only_workers_may_enter_system_execution()
    {
        // ADR-0123: HTTP-facing code (endpoints, handlers, behaviors) must never elevate to system execution.
        var offenders = Types.InAssembly(typeof(ApiServiceRegistration).Assembly)
            .That().HaveDependencyOn(typeof(SystemExecution).FullName!)
            .And().DoNotResideInNamespace("Chargeback.Api.Workers")
            .GetTypes()
            .Select(t => t.FullName!)
            .Where(n => !n.StartsWith(typeof(SystemExecution).FullName!, StringComparison.Ordinal)
                && !n.StartsWith("Chargeback.Api.Common.Behaviors.AuthorizationBehavior", StringComparison.Ordinal));

        offenders.Should().BeEmpty("only Chargeback.Api.Workers may call SystemExecution.Begin; AuthorizationBehavior only reads IsActive");
    }

    [Fact]
    public void System_operations_are_not_mapped_to_http_endpoints()
    {
        var systemRequests = RequestTypes.Where(t => RequestAuthorizationMetadata.For(t).IsSystemOperation).ToArray();
        systemRequests.Should().NotBeEmpty();

        var endpointModules = typeof(ApiServiceRegistration).Assembly.GetTypes().Where(t => typeof(Carter.ICarterModule).IsAssignableFrom(t));
        foreach (var request in systemRequests)
        {
            var result = Types.InAssembly(typeof(ApiServiceRegistration).Assembly)
                .That().ImplementInterface(typeof(Carter.ICarterModule))
                .ShouldNot().HaveDependencyOn(request.FullName!)
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{request.Name} is a system operation and must not be sent from an endpoint. {LayeringTests.Describe(result)}");
        }

        endpointModules.Should().NotBeEmpty();
    }

    [Fact]
    public void Proposed_permissions_are_not_seeded_names()
    {
        Permissions.Proposed.Intersect(Permissions.Seeded).Should().BeEmpty();
    }
}
