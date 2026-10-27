using System.Globalization;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Mod2Xgm;

/// <summary>
/// Collects <b>conversion notes</b>: every lossy decision (dropped effect, shortened sample, substituted sound…).
/// Each note is logged at <see cref="LogEventLevel.Warning"/> as soon as it is added, and kept for the summary.
/// Use Serilog message templates with named scalar properties, e.g.
/// <c>notes.Add("Sample {Sample} truncated to {Length} bytes", name, length)</c>.
/// </summary>
internal sealed class ConversionNotes(ILogger logger)
{
    // "l" renders text values without quotes, like the console's {Message:lj}.
    private static readonly MessageTemplateTextFormatter LiteralMessage = new("{Message:l}", CultureInfo.InvariantCulture);

    private readonly List<string> _messages = [];

    /// <summary>Rendered note texts, in the order they were added.</summary>
    public IReadOnlyList<string> Messages => _messages;

    public int Count => _messages.Count;

    public void Add(string messageTemplate, params object?[] propertyValues)
    {
        logger.Warning(messageTemplate, propertyValues);
        _messages.Add(Render(messageTemplate, propertyValues));
    }

    private string Render(string messageTemplate, object?[] propertyValues)
    {
        if (!logger.BindMessageTemplate(messageTemplate, propertyValues, out MessageTemplate? template, out IEnumerable<LogEventProperty>? properties)
            || template is null
            || properties is null)
        {
            return messageTemplate;
        }

        LogEvent logEvent = new(DateTimeOffset.UtcNow, LogEventLevel.Warning, null, template, properties);
        using StringWriter writer = new(CultureInfo.InvariantCulture);
        LiteralMessage.Format(logEvent, writer);
        return writer.ToString();
    }
}
