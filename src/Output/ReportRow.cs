using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm.Output;

/// <summary>
/// The typed content of one console-report line (docs/cli-output.md): a kind (<c>metadata</c>, <c>sample</c>,
/// <c>output</c>, <c>instrument</c>, <c>result</c>, or <c>part</c> for a header or continuation line) and its fields,
/// attached to the line's log event as the <see cref="PropertyName"/> property. The line's text stays what it always
/// was, for the log lines and their golden tests; the <c>text</c>, <c>json</c> and <c>fancy</c> reporters read the fields.
/// </summary>
internal sealed class ReportRow : ILogEventEnricher
{
    /// <summary>The log event property holding the row, a <see cref="StructureValue"/> tagged with its kind.</summary>
    public const string PropertyName = "Report";

    /// <summary>A line whose content another row carries (a table header, or the rest of the metadata): not shown on its own.</summary>
    public static ReportRow Part { get; } = new("part");

    private readonly LogEventProperty _property;

    /// <summary>A row of <paramref name="kind"/>; a field whose value is <c>null</c> is left out.</summary>
    public ReportRow(string kind, params (string Name, object? Value)[] fields)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(fields);
        _property = new LogEventProperty(
            PropertyName,
            new StructureValue([.. fields.Where(f => f.Value is not null).Select(f => new LogEventProperty(f.Name, Value(f.Value)))], kind));
    }

    /// <inheritdoc/>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        logEvent.AddPropertyIfAbsent(_property);
    }

    /// <summary>
    /// Logs <paramref name="line"/> at <c>Information</c> with <paramref name="row"/> attached. The line is a property, not
    /// the template: names may hold '{' and '}', which a template would read as holes.
    /// </summary>
    public static void Write(ILogger logger, string line, ReportRow row)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(row);
        logger.ForContext(row).Information("{ReportLine:l}", line);
    }

    /// <summary>The row <paramref name="logEvent"/> carries, or <c>null</c> when it is an ordinary line.</summary>
    public static StructureValue? Find(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        return logEvent.Properties.TryGetValue(PropertyName, out LogEventPropertyValue? value) ? value as StructureValue : null;
    }

    /// <summary>A field's value: numbers as a sequence, everything else as a scalar.</summary>
    private static LogEventPropertyValue Value(object? value) => value switch
    {
        IEnumerable<int> numbers => new SequenceValue([.. numbers.Select(n => new ScalarValue(n))]),
        _ => new ScalarValue(value),
    };
}

/// <summary>Reads a <see cref="ReportRow"/>'s fields.</summary>
internal static class ReportFields
{
    /// <summary>The scalar field <paramref name="name"/>, or <c>null</c> when it is missing.</summary>
    public static object? Get(this StructureValue row, string name)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Properties.FirstOrDefault(p => p.Name == name)?.Value is ScalarValue scalar ? scalar.Value : null;
    }

    /// <summary>The number sequence <paramref name="name"/>, or an empty list when it is missing.</summary>
    public static IReadOnlyList<int> Numbers(this StructureValue row, string name)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Properties.FirstOrDefault(p => p.Name == name)?.Value is SequenceValue sequence
            ? [.. sequence.Elements.OfType<ScalarValue>().Select(s => Convert.ToInt32(s.Value, System.Globalization.CultureInfo.InvariantCulture))]
            : [];
    }
}
