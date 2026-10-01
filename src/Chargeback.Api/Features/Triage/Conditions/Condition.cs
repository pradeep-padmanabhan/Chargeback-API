using System.Globalization;
using System.Text.Json;
using Chargeback.SharedKernel.Results;

namespace Chargeback.Api.Features.Triage.Conditions;

/// <summary>Three-valued logic: a condition over a missing fact is <see cref="Unknown"/>, never false.</summary>
public enum TriState
{
    False,
    True,
    Unknown,
}

public enum ConditionOperator
{
    Eq,
    Neq,
    In,
    NotIn,
    Gt,
    Gte,
    Lt,
    Lte,
    Exists,
    NotExists,
}

/// <summary>Parsed, type-checked rule condition (format: ADR-0120).</summary>
public abstract record Condition
{
    public abstract TriState Evaluate(CaseFacts facts);

    /// <summary>Catalogued facts this condition reads (for audit: which facts were missing).</summary>
    public abstract IEnumerable<string> Facts();
}

public sealed record AllCondition(IReadOnlyList<Condition> Conditions) : Condition
{
    public override TriState Evaluate(CaseFacts facts)
    {
        var unknown = false;
        foreach (var condition in Conditions)
        {
            switch (condition.Evaluate(facts))
            {
                case TriState.False:
                    return TriState.False;
                case TriState.Unknown:
                    unknown = true;
                    break;
            }
        }

        return unknown ? TriState.Unknown : TriState.True;
    }

    public override IEnumerable<string> Facts() => Conditions.SelectMany(c => c.Facts());
}

public sealed record AnyCondition(IReadOnlyList<Condition> Conditions) : Condition
{
    public override TriState Evaluate(CaseFacts facts)
    {
        var unknown = false;
        foreach (var condition in Conditions)
        {
            switch (condition.Evaluate(facts))
            {
                case TriState.True:
                    return TriState.True;
                case TriState.Unknown:
                    unknown = true;
                    break;
            }
        }

        return unknown ? TriState.Unknown : TriState.False;
    }

    public override IEnumerable<string> Facts() => Conditions.SelectMany(c => c.Facts());
}

public sealed record NotCondition(Condition Inner) : Condition
{
    public override TriState Evaluate(CaseFacts facts) => Inner.Evaluate(facts) switch
    {
        TriState.True => TriState.False,
        TriState.False => TriState.True,
        _ => TriState.Unknown,
    };

    public override IEnumerable<string> Facts() => Inner.Facts();
}

public sealed record FactCondition(FactDefinition Fact, ConditionOperator Operator, IReadOnlyList<FactValue> Values) : Condition
{
    public override TriState Evaluate(CaseFacts facts)
    {
        var present = facts.TryGet(Fact.Name, out var actual);
        switch (Operator)
        {
            case ConditionOperator.Exists:
                return present ? TriState.True : TriState.False;
            case ConditionOperator.NotExists:
                return present ? TriState.False : TriState.True;
        }

        if (!present)
        {
            return TriState.Unknown;
        }

        var result = Operator switch
        {
            ConditionOperator.Eq => actual.EqualsValue(Values[0]),
            ConditionOperator.Neq => !actual.EqualsValue(Values[0]),
            ConditionOperator.In => Values.Any(actual.EqualsValue),
            ConditionOperator.NotIn => !Values.Any(actual.EqualsValue),
            ConditionOperator.Gt => actual.CompareTo(Values[0]) > 0,
            ConditionOperator.Gte => actual.CompareTo(Values[0]) >= 0,
            ConditionOperator.Lt => actual.CompareTo(Values[0]) < 0,
            ConditionOperator.Lte => actual.CompareTo(Values[0]) <= 0,
            _ => throw new InvalidOperationException($"Unsupported operator {Operator}."),
        };
        return result ? TriState.True : TriState.False;
    }

    public override IEnumerable<string> Facts() => [Fact.Name];
}

/// <summary>
/// Parses the proposed condition JSON (ADR-0120):
/// <c>{"all":[...]}</c> | <c>{"any":[...]}</c> | <c>{"not":{...}}</c> |
/// <c>{"fact":"dispute.currencyCode","op":"eq","value":"GBP"}</c>.
/// Anything outside the format — including the column default <c>{}</c> — is invalid; it never means "match all".
/// </summary>
public static class ConditionParser
{
    public const int MaxDepth = 10;
    public const int MaxNodes = 200;

    public static Result<Condition> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Invalid("conditions are empty");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var nodes = 0;
            return ParseNode(document.RootElement, "$", 0, ref nodes);
        }
        catch (JsonException)
        {
            return Invalid("conditions are not valid JSON");
        }
    }

    private static Result<Condition> ParseNode(JsonElement node, string path, int depth, ref int nodes)
    {
        if (++nodes > MaxNodes || depth > MaxDepth)
        {
            return Invalid($"{path}: condition too large or too deeply nested");
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            return Invalid($"{path}: a condition must be a JSON object");
        }

        var names = node.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length == 0)
        {
            return Invalid($"{path}: empty condition '{{}}' is not allowed (it would match every case)");
        }

        if (names is ["all"] or ["any"])
        {
            var items = node.GetProperty(names[0]);
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                return Invalid($"{path}.{names[0]}: must be a non-empty array");
            }

            var children = new List<Condition>();
            var index = 0;
            foreach (var item in items.EnumerateArray())
            {
                var child = ParseNode(item, $"{path}.{names[0]}[{index++}]", depth + 1, ref nodes);
                if (child.IsFailure)
                {
                    return child;
                }

                children.Add(child.Value);
            }

            return names[0] == "all" ? new AllCondition(children) : new AnyCondition(children);
        }

        if (names is ["not"])
        {
            var inner = ParseNode(node.GetProperty("not"), $"{path}.not", depth + 1, ref nodes);
            return inner.IsFailure ? inner : new NotCondition(inner.Value);
        }

        return ParseLeaf(node, path, names);
    }

    private static Result<Condition> ParseLeaf(JsonElement node, string path, string[] names)
    {
        if (names.Except(["fact", "op", "value"]).Any())
        {
            return Invalid($"{path}: unexpected member(s) {string.Join(", ", names.Except(["fact", "op", "value"]))}");
        }

        if (!node.TryGetProperty("fact", out var factElement) || factElement.ValueKind != JsonValueKind.String
            || !FactCatalog.All.TryGetValue(factElement.GetString()!, out var fact))
        {
            return Invalid($"{path}.fact: must name a catalogued fact");
        }

        if (!node.TryGetProperty("op", out var opElement) || opElement.ValueKind != JsonValueKind.String
            || !Enum.TryParse<ConditionOperator>(opElement.GetString(), ignoreCase: true, out var op)
            || !char.IsLower(opElement.GetString()![0]))
        {
            return Invalid($"{path}.op: unsupported operator");
        }

        var hasValue = node.TryGetProperty("value", out var valueElement);
        switch (op)
        {
            case ConditionOperator.Exists or ConditionOperator.NotExists:
                return hasValue ? Invalid($"{path}.value: '{opElement.GetString()}' takes no value") : new FactCondition(fact, op, []);

            case ConditionOperator.In or ConditionOperator.NotIn:
                if (!hasValue || valueElement.ValueKind != JsonValueKind.Array || valueElement.GetArrayLength() == 0)
                {
                    return Invalid($"{path}.value: '{opElement.GetString()}' needs a non-empty array");
                }

                var values = new List<FactValue>();
                foreach (var item in valueElement.EnumerateArray())
                {
                    var parsed = ParseValue(item, fact, path);
                    if (parsed.IsFailure)
                    {
                        return Result.Failure<Condition>(parsed.Error);
                    }

                    values.Add(parsed.Value);
                }

                return new FactCondition(fact, op, values);

            case ConditionOperator.Gt or ConditionOperator.Gte or ConditionOperator.Lt or ConditionOperator.Lte
                when fact.Type is not (FactType.Number or FactType.Date):
                return Invalid($"{path}.op: '{opElement.GetString()}' applies only to number or date facts");

            default:
                if (!hasValue)
                {
                    return Invalid($"{path}.value: required");
                }

                var single = ParseValue(valueElement, fact, path);
                return single.IsFailure ? Result.Failure<Condition>(single.Error) : new FactCondition(fact, op, [single.Value]);
        }
    }

    private static Result<FactValue> ParseValue(JsonElement value, FactDefinition fact, string path)
    {
        switch (fact.Type)
        {
            case FactType.String when value.ValueKind == JsonValueKind.String:
                return FactValue.Of(value.GetString()!);
            case FactType.Number when value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number):
                return FactValue.Of(number);
            case FactType.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return FactValue.Of(value.GetBoolean());
            case FactType.Date when value.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                return FactValue.Of(date);
            default:
                return Result.Failure<FactValue>(InvalidError($"{path}.value: expected a {fact.Type} value for '{fact.Name}'"));
        }
    }

    private static Result<Condition> Invalid(string detail) => Result.Failure<Condition>(InvalidError(detail));

    private static Error InvalidError(string detail) => Error.Failure("INVALID_CONDITION", detail);
}
