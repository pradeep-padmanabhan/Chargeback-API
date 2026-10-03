using System.Text.Json.Serialization;
using Carter;
using Chargeback.Api.Common.Behaviors;
using Chargeback.Api.Common.Http;
using Chargeback.Api.Common.Idempotency;
using Chargeback.Api.Common.OpenApi;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.Cases;
using Chargeback.Api.Features.Intake;
using Chargeback.Api.Features.Triage;
using Chargeback.Api.Workers;
using Chargeback.Infrastructure;
using Chargeback.Infrastructure.Correlation;
using Chargeback.Infrastructure.Maintenance;
using Chargeback.SharedKernel.Security;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Serilog;

namespace Chargeback.Api;

public static class ApiServiceRegistration
{
    /// <summary>
    /// MediatR pipeline, outermost first. APPROVED ORDER (diagram + ADR-0106, 2026-09-29):
    /// Logging → Validation → Authorization → Idempotency → Transaction → handler.
    /// Changing it requires an ADR; <c>PipelineOrderTests</c> enforce it.
    /// </summary>
    public static IServiceCollection AddChargebackApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(ApiServiceRegistration).Assembly);
            cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            cfg.AddOpenBehavior(typeof(AuthorizationBehavior<,>));
            cfg.AddOpenBehavior(typeof(IdempotencyBehavior<,>));
            cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
        });
        services.AddOptions<IdempotencyOptions>().BindConfiguration(IdempotencyOptions.SectionName);
        services.AddScoped<IdempotencyContext>();
        services.AddValidatorsFromAssembly(typeof(ApiServiceRegistration).Assembly, includeInternalTypes: true);

        // Slice-owned services.
        services.AddIntakeSlice();
        services.AddTriageSlice();
        services.AddCasesSlice();
        return services;
    }

    public static IServiceCollection AddChargebackApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            // Approved contract: one traceId (W3C when tracing is active). Overwrites the framework default so all errors agree.
            context.ProblemDetails.Extensions[ResultHttpMapper.TraceIdField] = ResultHttpMapper.TraceIdFor(context.HttpContext);
        });
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddHttpContextAccessor();
        services.AddScoped<ICorrelationIdProvider, HttpCorrelationIdProvider>();
        services.AddScoped<IPrincipalAccessor, HttpContextPrincipalAccessor>();
        services.AddScoped<CurrentUserAccessor>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUserAccessor>());
        services.AddScoped<ICurrentUserLoader>(sp => sp.GetRequiredService<CurrentUserAccessor>());

        services.AddChargebackAuthentication(configuration);
        services.AddChargebackApplication();
        services.AddWorkflowConsumers();
        services.AddCarter();
        services.AddChargebackOpenApi();

        return services;
    }

    public static WebApplication UseChargebackPipeline(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseSerilogRequestLogging();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapOpenApi().AllowAnonymous();
        if (app.Environment.IsDevelopment())
        {
            app.MapScalarApiReference().AllowAnonymous();
        }

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(DependencyInjection.ReadinessTag),
        }).AllowAnonymous();

        app.MapCarter();
        return app;
    }
}
