using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chargeback.Api.Common.Results;
using Chargeback.Infrastructure.Maintenance;
using Chargeback.Infrastructure.Persistence;
using Chargeback.Infrastructure.Persistence.Entities;
using Chargeback.SharedKernel.Results;
using Chargeback.SharedKernel.Security;
using Microsoft.Extensions.Options;

namespace Chargeback.Api.Common.Idempotency;

/// <summary>ADR-0106 execution mode.</summary>
public enum IdempotencyMode
{
    /// <summary>The COMPLETED key row is written in the same transaction as the business effect.</summary>
    Atomic,

    /// <summary>Reserve (IN_PROGRESS), perform the external call outside any transaction, then complete. Phase 10.</summary>
    Reservation,
}

/// <summary>Declares a request idempotent under ADR-0106. The request must implement <see cref="IIdempotentCommand"/>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class IdempotentAttribute(string operation, IdempotencyMode mode, int successStatusCode) : Attribute
{
    /// <summary>OpenAPI operationId; part of the key scope and the request hash.</summary>
    public string Operation { get; } = operation;

    public IdempotencyMode Mode { get; } = mode;

    /// <summary>HTTP status of the original success, stored for audit and replay.</summary>
    public int SuccessStatusCode { get; } = successStatusCode;
}

/// <summary>
/// Carries the client's <c>Idempotency-Key</c>. Implementations mark the property <c>[JsonIgnore]</c> so the key
/// is not part of the request hash.
/// </summary>
public interface IIdempotentCommand
{
    string? IdempotencyKey { get; }
}

public static class IdempotencyErrors
{
    public static readonly Error KeyReused = Error.Unprocessable(
        "IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was already used with a different request.");

    public static readonly Error InProgress = Error.Conflict(
        "IDEMPOTENCY_REQUEST_IN_PROGRESS", "A request with this Idempotency-Key is still being processed. Retry later.");

    public static readonly Error ReservationNotImplemented = Error.NotImplemented(
        "NOT_IMPLEMENTED", "Reservation-mode idempotency (external side effects) arrives with Phase 10 (ADR-0106).");
}

public static class IdempotencyHash
{
    /// <summary>Serialization for stored and replayed responses.</summary>
    internal static readonly JsonSerializerOptions CanonicalOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serialization for hashing: as above, plus scale-normalized decimals (5, 5.0 and 5.00 are one amount).</summary>
    private static readonly JsonSerializerOptions HashOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(), new NormalizedDecimalConverter() },
    };

    /// <summary>
    /// SHA-256 (hex) of the operation plus the canonical JSON of the bound request (route values and body; the key
    /// itself is excluded). Hashing the bound object makes property order and whitespace in the raw body irrelevant.
    /// </summary>
    public static string Compute(string operation, object request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = JsonSerializer.SerializeToUtf8Bytes(request, request.GetType(), HashOptions);
        var prefix = System.Text.Encoding.UTF8.GetBytes(operation + "\n");
        return Convert.ToHexStringLower(SHA256.HashData([.. prefix, .. body]));
    }

    private sealed class NormalizedDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDecimal();

        // Dividing by 1.000…0 strips trailing zeros from the scale without changing the value.
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value / 1.000000000000000000000000000000000m);
    }
}

/// <summary>Per-request idempotency state shared by the behavior, the handler and the endpoint filter.</summary>
public sealed class IdempotencyContext(TimeProvider timeProvider, IOptions<IdempotencyOptions> options)
{
    public const int MaxStoredResponseBytes = 16 * 1024;

    private IdempotentAttribute? _attribute;
    private Guid _principalId;
    private string? _key;
    private string? _hash;

    public bool IsActive => _attribute is not null;

    /// <summary>The response was replayed from the store (drives the <c>Idempotent-Replayed</c> header).</summary>
    public bool Replayed { get; private set; }

    public bool Recorded { get; private set; }

    internal void Begin(IdempotentAttribute attribute, Guid principalId, string key, string hash)
    {
        _attribute = attribute;
        _principalId = principalId;
        _key = key;
        _hash = hash;
    }

    internal void MarkReplayed() => Replayed = true;

    /// <summary>
    /// Conservative card-data guard over the stored response's VALUES: any non-GUID string, or any number, containing a
    /// PAN-shaped digit run (Luhn not required). GUID values are skipped because their dash-separated all-digit groups
    /// can look like a grouped card number (found by the concurrency test: ~1 in a few hundred dispute ids).
    /// </summary>
    internal static bool ContainsCardLikeValue(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Scan(document.RootElement);

        static bool Scan(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Any(p => Scan(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Any(Scan),
            JsonValueKind.String => element.GetString() is { } s && !Guid.TryParse(s, out _) && PanRedactor.ContainsPanCandidate(s),
            JsonValueKind.Number => PanRedactor.ContainsPanCandidate(element.GetRawText()),
            _ => false,
        };
    }

    /// <summary>
    /// Atomic mode: called by the handler just before its SaveChanges so the COMPLETED key row commits in the same
    /// transaction as the business effect. No-op when the request is not idempotent.
    /// </summary>
    public void RecordCompleted(ChargebackDbContext db, object response, Guid? resourceId)
    {
        if (_attribute is null)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(response);

        var json = JsonSerializer.Serialize(response, response.GetType(), IdempotencyHash.CanonicalOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxStoredResponseBytes)
        {
            throw new InvalidOperationException($"{_attribute.Operation}: response exceeds {MaxStoredResponseBytes} bytes and cannot be stored (ADR-0106 §4).");
        }

        if (ContainsCardLikeValue(json))
        {
            throw new InvalidOperationException($"{_attribute.Operation}: response contains a PAN-shaped value and must never be stored (ADR-0106 §4).");
        }

        var now = timeProvider.GetUtcNow();
        db.IdempotencyKeys.Add(new IdempotencyKeyRecord
        {
            PrincipalId = _principalId,
            Operation = _attribute.Operation,
            IdempotencyKey = _key!,
            RequestHash = _hash!,
            State = "COMPLETED",
            ResponseStatus = _attribute.SuccessStatusCode,
            ResponseBody = json,
            ResourceId = resourceId,
            CreatedAt = now,
            CompletedAt = now,
            ExpiresAt = now + options.Value.TimeToLive,
        });
        Recorded = true;
    }
}

/// <summary>Builds a successful <typeparamref name="TResponse"/> (<c>Result&lt;T&gt;</c>) from a stored JSON value, for replays.</summary>
public static class ResultReplay<TResponse>
{
    private static readonly (Type ValueType, MethodInfo Success)? Shape = BuildShape();

    public static TResponse FromJson(string json)
    {
        var shape = Shape ?? throw new InvalidOperationException($"{typeof(TResponse)} is not Result<T>; idempotent operations must return a value.");
        var value = JsonSerializer.Deserialize(json, shape.ValueType, IdempotencyHash.CanonicalOptions);
        return (TResponse)shape.Success.Invoke(null, [value])!;
    }

    private static (Type, MethodInfo)? BuildShape()
    {
        var type = typeof(TResponse);
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Result<>))
        {
            return null;
        }

        var success = type.GetMethod(nameof(Result<object>.Success), BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly, [type.GetGenericArguments()[0]])!;
        return (type.GetGenericArguments()[0], success);
    }
}
