using Chargeback.Api.Common.Http;
using Chargeback.SharedKernel.Paging;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace Chargeback.Api.Common.OpenApi;

public static class OpenApiConfiguration
{
    public const string DocumentName = "v1";

    public static IServiceCollection AddChargebackOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Chargeback Management Platform API",
                    Version = "v1",
                    Description =
                        "Diagram-aligned MVP contract (Phase 4). Endpoints marked [STUB] return 501 after " +
                        "authentication, authorization and bank-scope checks pass. Errors are RFC 9457 problem " +
                        "details with a stable `code` and `traceId`. AI-derived fields are advisory.",
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Amazon Cognito access token (single user pool).",
                };
                document.SecurityRequirements.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
                    }] = [],
                });
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = CorrelationId.HeaderName,
                    In = ParameterLocation.Header,
                    Required = false,
                    Description = "Optional caller correlation id (1-64 chars [A-Za-z0-9._-]); echoed in the response.",
                    Schema = new OpenApiSchema { Type = "string", MaxLength = 64 },
                });

                // Approved pagination contract: 1-based page; pageSize 1-100, default 25.
                foreach (var parameter in operation.Parameters.Where(p => p.In == ParameterLocation.Query))
                {
                    switch (parameter.Name)
                    {
                        case "page":
                            parameter.Description = "1-based page number (default 1).";
                            parameter.Schema = new OpenApiSchema { Type = "integer", Format = "int32", Minimum = 1, Default = new OpenApiInteger(1) };
                            break;
                        case "pageSize":
                            parameter.Description = $"Items per page, 1-{PageRequest.MaxPageSize} (default {PageRequest.DefaultPageSize}); out-of-range values are clamped.";
                            parameter.Schema = new OpenApiSchema
                            {
                                Type = "integer",
                                Format = "int32",
                                Minimum = 1,
                                Maximum = PageRequest.MaxPageSize,
                                Default = new OpenApiInteger(PageRequest.DefaultPageSize),
                            };
                            break;
                    }
                }

                if (context.Description.ActionDescriptor.EndpointMetadata.OfType<RequiresIdempotencyKeyMetadata>().Any())
                {
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = IdempotencyKey.HeaderName,
                        In = ParameterLocation.Header,
                        Required = true,
                        Description =
                            "Client-generated key (8-128 chars [A-Za-z0-9._:-]), scoped to the caller and operation, kept 90 days (ADR-0106). " +
                            "A retry with the same key and request replays the stored response with header Idempotent-Replayed: true; " +
                            "the same key with a different request is 422 IDEMPOTENCY_KEY_REUSED; while the first is still running, 409 IDEMPOTENCY_REQUEST_IN_PROGRESS.",
                        Schema = new OpenApiSchema { Type = "string", MinLength = 8, MaxLength = 128 },
                    });

                    foreach (var (status, response) in operation.Responses.Where(r => r.Key.StartsWith('2')))
                    {
                        response.Headers ??= new Dictionary<string, OpenApiHeader>();
                        response.Headers[IdempotencyKey.ReplayedHeaderName] = new OpenApiHeader
                        {
                            Description = "Present with value 'true' when this response was replayed from the idempotency store.",
                            Schema = new OpenApiSchema { Type = "string", Enum = [new OpenApiString("true")] },
                        };
                    }

                    operation.Responses.TryAdd("409", ProblemResponse("IDEMPOTENCY_REQUEST_IN_PROGRESS (or an operation-specific conflict)"));
                    operation.Responses.TryAdd("422", ProblemResponse("IDEMPOTENCY_KEY_REUSED"));
                }

                return Task.CompletedTask;
            });

            options.AddSchemaTransformer((schema, context, _) =>
            {
                var type = context.JsonTypeInfo.Type;

                // Approved error contract: RFC 9457 problem details plus a stable machine-readable code and the correlation id.
                if (typeof(Microsoft.AspNetCore.Mvc.ProblemDetails).IsAssignableFrom(type))
                {
                    schema.Properties["code"] = new OpenApiSchema
                    {
                        Type = "string",
                        Pattern = "^[A-Z][A-Z0-9_]*$",
                        Description = "Stable machine-readable error code (UPPER_SNAKE_CASE), e.g. RESOURCE_NOT_FOUND, VALIDATION_FAILED.",
                    };
                    schema.Properties["traceId"] = new OpenApiSchema
                    {
                        Type = "string",
                        MaxLength = 64,
                        Description = "Trace id of the request: the correlation id also returned in X-Correlation-Id.",
                    };
                }

                // Approved reference contract (ADR-0125): CB-{YYYY}-{6-digit number}.
                if (schema.Properties.TryGetValue("caseReference", out var reference))
                {
                    reference.Pattern = @"^CB-\d{4}-\d{6}$";
                    reference.Description = "Case reference CB-{YYYY}-{NNNNNN}: UTC year of creation and a global, non-resetting database sequence (ADR-0125).";
                }

                // Approved pagination contract.
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PagedResult<>))
                {
                    schema.Description = "One page of results: items, 1-based page, pageSize, totalCount and totalPages.";
                }

                return Task.CompletedTask;
            });
        });

    private static OpenApiResponse ProblemResponse(string codes) => new()
    {
        Description = $"Problem details; code {codes}.",
        Content = new Dictionary<string, OpenApiMediaType>
        {
            ["application/problem+json"] = new OpenApiMediaType
            {
                Schema = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "ProblemDetails" } },
            },
        },
    };
}
