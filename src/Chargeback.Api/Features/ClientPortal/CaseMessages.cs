using Chargeback.Api.Common.Messaging;
using Chargeback.Api.Common.Results;
using Chargeback.Api.Common.Security;
using Chargeback.Api.Features.ClientPortal.Contracts;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.Infrastructure.Persistence.Queries;
using Chargeback.Infrastructure.Security;
using Chargeback.Infrastructure.Support;
using Chargeback.SharedKernel.Events;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Chargeback.Api.Features.ClientPortal;

// The two-way case thread (portal_messages, append-only). Bank users use /portal/cases/{id}/messages; analysts use
// /cases/{id}/messages with VIEW_CASES. Both read the same thread; the sender type comes from the caller, never the body.
// No push: the portal polls.

public static class CaseMessageRules
{
    public const int MaxBodyLength = 2000;

    /// <summary>Stored value: BANK for bank users; PROCESSOR for analysts and admin users.</summary>
    public static string StoredSenderType(UserType userType) => userType == UserType.Bank ? "BANK" : "PROCESSOR";

    public static string ApiSenderType(string stored) => stored == "BANK" ? MessageSenderTypes.BankUser : MessageSenderTypes.Analyst;
}

/// <summary>Timeline entry for a new message; the text itself stays in <c>portal_messages</c>.</summary>
public sealed record CaseMessagePosted : DomainEvent
{
    public override string EventType => "case.message.posted";

    public required Guid MessageId { get; init; }

    public required string SenderType { get; init; }

    public required Guid SenderId { get; init; }
}

/// <summary>A support request, recorded with its payload for the future Zendesk integration (KNOWN_LIMITATION_ZENDESK_).</summary>
public sealed record CaseSupportRequested : DomainEvent
{
    public override string EventType => "case.support.requested";

    public required string TicketId { get; init; }

    public required string Subject { get; init; }

    public required string Body { get; init; }

    public required Guid RequestedBy { get; init; }
}

[AllowAnyPlatformUser]
[RestrictToUserTypes(UserType.Bank)]
public sealed record ListPortalMessagesQuery(Guid CaseId) : IQuery<IReadOnlyList<PortalMessageDto>>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

[AllowAnyPlatformUser]
[RestrictToUserTypes(UserType.Bank)]
public sealed record PostPortalMessageCommand(Guid CaseId, PostMessageRequest Body) : ICommand<PortalMessageDto>, IResourceScopedRequest, ITransactionalCommand, IPostMessage
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

[RequirePermission(Permissions.ViewCases)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record ListCaseMessagesQuery(Guid CaseId) : IQuery<IReadOnlyList<CaseMessageDto>>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

[RequirePermission(Permissions.ViewCases)]
[RestrictToUserTypes(UserType.Processor, UserType.Admin)]
public sealed record PostCaseMessageCommand(Guid CaseId, PostMessageRequest Body) : ICommand<CaseMessageDto>, IResourceScopedRequest, ITransactionalCommand, IPostMessage
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

/// <summary>Raises a support request; not transactional because the (stubbed) Zendesk call is external.</summary>
[AllowAnyPlatformUser]
[RestrictToUserTypes(UserType.Bank)]
public sealed record CreateSupportTicketCommand(Guid CaseId, SupportTicketRequest Body) : ICommand<SupportTicketAcceptedDto>, IResourceScopedRequest
{
    public ScopedResource Resource => new(ScopedResourceKind.Case, CaseId);
}

public interface IPostMessage
{
    PostMessageRequest Body { get; }
}

public sealed class PostPortalMessageValidator : AbstractValidator<PostPortalMessageCommand>
{
    public PostPortalMessageValidator() => Include(new MessageBodyValidator());
}

public sealed class PostCaseMessageValidator : AbstractValidator<PostCaseMessageCommand>
{
    public PostCaseMessageValidator() => Include(new MessageBodyValidator());
}

public sealed class MessageBodyValidator : AbstractValidator<IPostMessage>
{
    public MessageBodyValidator()
    {
        RuleFor(x => x.Body).NotNull();
        RuleFor(x => x.Body.Body)
            .Must(b => !string.IsNullOrWhiteSpace(b)).WithMessage("body is required.")
            .Must(b => b is null || b.Trim().Length <= CaseMessageRules.MaxBodyLength).WithMessage($"body must be at most {CaseMessageRules.MaxBodyLength} characters.")
            .Must(b => !PanRedactor.ContainsPan(b)).WithMessage("Must not contain a card number.")
            .When(x => x.Body is not null)
            .OverridePropertyName("body");
    }
}

public sealed class CreateSupportTicketValidator : AbstractValidator<CreateSupportTicketCommand>
{
    public CreateSupportTicketValidator()
    {
        RuleFor(x => x.Body).NotNull();
        When(x => x.Body is not null, () =>
        {
            RuleFor(x => x.Body.Subject).NotEmpty().MaximumLength(200)
                .Must(s => !PanRedactor.ContainsPan(s)).WithMessage("Must not contain a card number.")
                .OverridePropertyName("subject");
            RuleFor(x => x.Body.Body).NotEmpty().MaximumLength(CaseMessageRules.MaxBodyLength)
                .Must(b => !PanRedactor.ContainsPan(b)).WithMessage("Must not contain a card number.")
                .OverridePropertyName("body");
        });
    }
}

internal sealed class CaseThread(ChargebackDbContext db, IDapperQueryService queries, ICurrentUser currentUser, TimeProvider timeProvider)
{
    private const string Sql = """
        SELECT id AS message_id, case_id, sender_type, sender_id, message_text AS body, created_at
        FROM chargeback_diagram.portal_messages
        WHERE case_id = @CaseId
        ORDER BY created_at, id
        """;

    public async Task<IReadOnlyList<CaseMessageDto>> ReadAsync(Guid caseId, CancellationToken cancellationToken) =>
        (await queries.QueryAsync<Row>(Sql, new { CaseId = caseId }, cancellationToken))
            .Select(r => new CaseMessageDto(r.MessageId, r.CaseId, CaseMessageRules.ApiSenderType(r.SenderType), r.SenderId, r.Body, r.CreatedAt))
            .ToArray();

    /// <returns>null when the case does not exist.</returns>
    public async Task<CaseMessageDto?> PostAsync(Guid caseId, string body, CancellationToken cancellationToken)
    {
        var bankId = await BankOfCaseAsync(db, caseId, cancellationToken);
        if (bankId is null)
        {
            return null;
        }

        var message = new PortalMessage
        {
            CaseId = caseId,
            SenderType = CaseMessageRules.StoredSenderType(currentUser.UserType),
            SenderId = currentUser.UserId,
            MessageText = body.Trim(),
            CreatedAt = timeProvider.GetUtcNow(),
        };
        message.AddDomainEvent(new CaseMessagePosted
        {
            CaseId = caseId,
            BankId = bankId,
            MessageId = message.Id,
            SenderType = CaseMessageRules.ApiSenderType(message.SenderType),
            SenderId = currentUser.UserId,
        });
        db.PortalMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        return new CaseMessageDto(message.Id, caseId, CaseMessageRules.ApiSenderType(message.SenderType), message.SenderId, message.MessageText, message.CreatedAt);
    }

    public static Task<Guid?> BankOfCaseAsync(ChargebackDbContext db, Guid caseId, CancellationToken cancellationToken) =>
        (from c in db.Cases join d in db.Disputes on c.DisputeId equals d.Id where c.Id == caseId select (Guid?)d.BankId)
            .SingleOrDefaultAsync(cancellationToken);

    private sealed class Row
    {
        public Guid MessageId { get; set; }

        public Guid CaseId { get; set; }

        public string SenderType { get; set; } = "";

        public Guid? SenderId { get; set; }

        public string Body { get; set; } = "";

        public DateTimeOffset CreatedAt { get; set; }
    }
}

internal sealed class ListPortalMessagesHandler(CaseThread thread) : IRequestHandler<ListPortalMessagesQuery, Result<IReadOnlyList<PortalMessageDto>>>
{
    public async Task<Result<IReadOnlyList<PortalMessageDto>>> Handle(ListPortalMessagesQuery request, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PortalMessageDto>>((await thread.ReadAsync(request.CaseId, cancellationToken))
            .Select(m => new PortalMessageDto(m.MessageId, m.SenderType, m.Body, m.CreatedAt))
            .ToArray());
}

internal sealed class PostPortalMessageHandler(CaseThread thread) : IRequestHandler<PostPortalMessageCommand, Result<PortalMessageDto>>
{
    public async Task<Result<PortalMessageDto>> Handle(PostPortalMessageCommand request, CancellationToken cancellationToken) =>
        await thread.PostAsync(request.CaseId, request.Body.Body!, cancellationToken) is { } m
            ? new PortalMessageDto(m.MessageId, m.SenderType, m.Body, m.CreatedAt)
            : Errors.ResourceNotFound;
}

internal sealed class ListCaseMessagesHandler(CaseThread thread) : IRequestHandler<ListCaseMessagesQuery, Result<IReadOnlyList<CaseMessageDto>>>
{
    public async Task<Result<IReadOnlyList<CaseMessageDto>>> Handle(ListCaseMessagesQuery request, CancellationToken cancellationToken) =>
        Result.Success(await thread.ReadAsync(request.CaseId, cancellationToken));
}

internal sealed class PostCaseMessageHandler(CaseThread thread) : IRequestHandler<PostCaseMessageCommand, Result<CaseMessageDto>>
{
    public async Task<Result<CaseMessageDto>> Handle(PostCaseMessageCommand request, CancellationToken cancellationToken) =>
        await thread.PostAsync(request.CaseId, request.Body.Body!, cancellationToken) is { } m ? m : Errors.ResourceNotFound;
}

internal sealed class CreateSupportTicketHandler(ChargebackDbContext db, IZendeskClient zendesk, ICurrentUser currentUser)
    : IRequestHandler<CreateSupportTicketCommand, Result<SupportTicketAcceptedDto>>
{
    public async Task<Result<SupportTicketAcceptedDto>> Handle(CreateSupportTicketCommand request, CancellationToken cancellationToken)
    {
        if (await CaseThread.BankOfCaseAsync(db, request.CaseId, cancellationToken) is not { } bankId)
        {
            return Errors.ResourceNotFound;
        }

        var subject = request.Body.Subject!.Trim();
        var body = request.Body.Body!.Trim();

        // External call first, outside any database transaction; then the payload is recorded on the case timeline.
        var ticketId = await zendesk.CreateTicketAsync(request.CaseId, bankId, subject, body, cancellationToken);
        var @case = await db.Cases.SingleAsync(c => c.Id == request.CaseId, cancellationToken);
        @case.AddDomainEvent(new CaseSupportRequested
        {
            CaseId = request.CaseId, BankId = bankId, TicketId = ticketId, Subject = subject, Body = body, RequestedBy = currentUser.UserId,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new SupportTicketAcceptedDto(ticketId);
    }
}
