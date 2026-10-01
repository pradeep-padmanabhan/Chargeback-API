using Chargeback.SharedKernel.Security;
using Serilog.Core;
using Serilog.Events;

namespace Chargeback.Api.Common.Logging;

/// <summary>
/// Masks PAN-like values (every 13-19 digit candidate, Luhn or not) in every log event property, including nested structures. Runs last in the
/// enrichment chain. Message templates are constants (CA2254 is an error), so card data can only
/// reach logs through properties, which this covers. Exception messages are not rewritten: never put
/// card data into exception messages.
/// </summary>
public sealed class PanRedactionEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        foreach (var (name, value) in logEvent.Properties.ToArray())
        {
            var redacted = Redact(value);
            if (!ReferenceEquals(redacted, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
            }
        }
    }

    internal static LogEventPropertyValue Redact(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string text } when PanRedactor.ContainsPanCandidate(text):
                return new ScalarValue(PanRedactor.Redact(text));

            case SequenceValue sequence:
                var elements = sequence.Elements.Select(Redact).ToArray();
                return elements.SequenceEqual(sequence.Elements) ? value : new SequenceValue(elements);

            case StructureValue structure:
                var properties = structure.Properties.Select(p => new LogEventProperty(p.Name, Redact(p.Value))).ToArray();
                return properties.Select(p => p.Value).SequenceEqual(structure.Properties.Select(p => p.Value))
                    ? value
                    : new StructureValue(properties, structure.TypeTag);

            case DictionaryValue dictionary:
                var entries = dictionary.Elements
                    .Select(e => new KeyValuePair<ScalarValue, LogEventPropertyValue>(e.Key, Redact(e.Value)))
                    .ToArray();
                return entries.Select(e => e.Value).SequenceEqual(dictionary.Elements.Select(e => e.Value))
                    ? value
                    : new DictionaryValue(entries);

            default:
                return value;
        }
    }
}
