using Carter;
using Chargeback.Api.Common.Endpoints;
using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using MediatR;

namespace Chargeback.Api.Features.Identity;

/// <summary>Profile for the frontend authStore: user type, role, permission codes and bank scope.</summary>
public sealed record CurrentUserDto(
    Guid UserId,
    string Email,
    string FullName,
    UserType UserType,
    Guid RoleId,
    string RoleName,
    Guid? HomeBankId,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<Guid> BankScopes);

[AllowAnyPlatformUser]
[NotBankScoped("Returns the caller's own profile and scopes only.")]
public sealed record GetCurrentUserQuery : IQuery<CurrentUserDto>;

internal sealed class GetCurrentUserHandler(CurrentUserAccessor currentUser) : IRequestHandler<GetCurrentUserQuery, Result<CurrentUserDto>>
{
    public Task<Result<CurrentUserDto>> Handle(GetCurrentUserQuery request, CancellationToken cancellationToken)
    {
        var access = currentUser.Access;
        var dto = new CurrentUserDto(
            access.UserId,
            access.Email,
            access.FullName,
            access.UserType,
            access.RoleId,
            access.RoleName,
            access.HomeBankId,
            access.Permissions.Order(StringComparer.Ordinal).ToArray(),
            access.BankScopes.Order().ToArray());
        return Task.FromResult(Result.Success(dto));
    }
}

public sealed class IdentityModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(EndpointConventions.ApiPrefix).WithTags("Identity");

        group.MapGet("/me", (ISender sender, HttpContext http) => Dispatch.Send(sender, new GetCurrentUserQuery(), http))
            .WithContract<CurrentUserDto>("getCurrentUser", "Current user profile, permissions and bank scopes");
    }
}
