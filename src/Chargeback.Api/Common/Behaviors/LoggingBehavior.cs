using System.Diagnostics;
using Chargeback.SharedKernel.Results;
using MediatR;

namespace Chargeback.Api.Common.Behaviors;

/// <summary>
/// Pipeline step 1. Logs request type, outcome code and duration. Never logs request or response
/// payloads (they may contain personal data).
/// </summary>
public sealed partial class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var requestName = typeof(TRequest).Name;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await next(cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (response is Result { IsFailure: true } failed)
            {
                LogFailed(logger, requestName, failed.Error.Code, elapsed);
            }
            else
            {
                LogHandled(logger, requestName, elapsed);
            }

            return response;
        }
        catch (Exception ex)
        {
            LogException(logger, requestName, Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Handled {RequestName} in {ElapsedMs:0.0} ms")]
    private static partial void LogHandled(ILogger logger, string requestName, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{RequestName} failed with {ErrorCode} in {ElapsedMs:0.0} ms")]
    private static partial void LogFailed(ILogger logger, string requestName, string errorCode, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "{RequestName} threw after {ElapsedMs:0.0} ms")]
    private static partial void LogException(ILogger logger, string requestName, double elapsedMs, Exception exception);
}
