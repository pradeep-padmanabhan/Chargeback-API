using Chargeback.Api;
using Chargeback.Api.Common.Logging;
using Chargeback.Infrastructure;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.With<PanRedactionEnricher>()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

// Api first: its registrations (e.g. HTTP correlation id) take precedence over infrastructure defaults.
builder.Services.AddChargebackApi(builder.Configuration);
builder.Services.AddChargebackInfrastructure(builder.Configuration);

var app = builder.Build();
app.UseChargebackPipeline();
app.Run();

/// <summary>Entry point; public for WebApplicationFactory.</summary>
public partial class Program;
