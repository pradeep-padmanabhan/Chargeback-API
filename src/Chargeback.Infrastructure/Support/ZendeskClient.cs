using Microsoft.Extensions.Logging;

namespace Chargeback.Infrastructure.Support;

/// <summary>Outbound Zendesk support tickets (common guide §1). External call: never inside a database transaction.</summary>
public interface IZendeskClient
{
    /// <returns>The ticket id.</returns>
    Task<string> CreateTicketAsync(Guid caseId, Guid bankId, string subject, string body, CancellationToken cancellationToken);
}

/// <summary>
/// KNOWN_LIMITATION_ZENDESK_: no Zendesk account, credentials or field mapping are approved, so no ticket is created.
/// Returns <c>STUB-&lt;uuid&gt;</c>. The payload is recorded on the case timeline by the caller (<c>case.support.requested</c>)
/// for future integration; this log line carries ids and sizes only, never the free text.
/// </summary>
public sealed partial class KnownLimitationZendeskClient(ILogger<KnownLimitationZendeskClient> logger) : IZendeskClient
{
    public const string Marker = "KNOWN_LIMITATION_ZENDESK_";
    public const string TicketPrefix = "STUB-";

    public Task<string> CreateTicketAsync(Guid caseId, Guid bankId, string subject, string body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(body);
        var ticketId = TicketPrefix + Guid.NewGuid().ToString("D");
        LogStubTicket(logger, Marker, ticketId, caseId, bankId, subject.Length, body.Length);
        return Task.FromResult(ticketId);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "{Marker} support ticket {TicketId} not sent to Zendesk: case {CaseId}, bank {BankId}, subject {SubjectLength} chars, body {BodyLength} chars")]
    private static partial void LogStubTicket(ILogger logger, string marker, string ticketId, Guid caseId, Guid bankId, int subjectLength, int bodyLength);
}
