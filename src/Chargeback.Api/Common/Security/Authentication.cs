using System.ComponentModel.DataAnnotations;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Chargeback.Api.Common.Security;

public sealed class CognitoOptions
{
    public const string SectionName = "Authentication:Cognito";

    [Required]
    public string Region { get; set; } = "";

    [Required]
    public string UserPoolId { get; set; } = "";

    /// <summary>App client ids of the Analyst and Client portals. Access tokens from other clients are rejected.</summary>
    [MinLength(1)]
    public string[] AllowedClientIds { get; set; } = [];

    public string Authority => $"https://cognito-idp.{Region}.amazonaws.com/{UserPoolId}";
}

public static class ChargebackAuthentication
{
    public const string SdkHostTokenScheme = "SdkHostToken";
    public const string SdkHostTokenPolicy = "SdkHostToken";

    /// <summary>
    /// Single Cognito user pool for processor and bank users; only access tokens are accepted.
    /// User type/role/permissions are not taken from token claims (ADR-0003).
    /// </summary>
    public static IServiceCollection AddChargebackAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var cognito = configuration.GetSection(CognitoOptions.SectionName).Get<CognitoOptions>() ?? new CognitoOptions();
        Validator.ValidateObject(cognito, new ValidationContext(cognito), validateAllProperties: true);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = cognito.Authority;
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = cognito.Authority,
                    // Cognito access tokens carry client_id instead of aud; checked below.
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    NameClaimType = CurrentUserAccessor.SubjectClaim,
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var tokenUse = context.Principal?.FindFirst("token_use")?.Value;
                        var clientId = context.Principal?.FindFirst("client_id")?.Value;
                        if (tokenUse != "access" || clientId is null || !cognito.AllowedClientIds.Contains(clientId, StringComparer.Ordinal))
                        {
                            context.Fail("Token is not an access token for an allowed client.");
                        }

                        return Task.CompletedTask;
                    },
                };
            })
            .AddScheme<AuthenticationSchemeOptions, SdkHostTokenPendingHandler>(SdkHostTokenScheme, null);

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build())
            .AddPolicy(SdkHostTokenPolicy, policy => policy
                .AddAuthenticationSchemes(SdkHostTokenScheme)
                .RequireAuthenticatedUser());

        return services;
    }
}

/// <summary>
/// SDK host-app token exchange is PROPOSED, not approved (ADR-0005). Until confirmed this scheme
/// authenticates nobody, so every SDK endpoint returns 401.
/// </summary>
internal sealed class SdkHostTokenPendingHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}
