using System.Globalization;
using System.Text;
using Serilog.Events;

namespace Mod2Xgm.Output;

/// <summary>
/// <c>--progress text</c>, for AI agents (docs/cli-output.md#text, ADR 0077): one fact per line, a keyword and
/// <c>key=value</c> pairs, no color or animation. Every line goes to stdout except <c>error</c> lines, which go to stderr.
/// A long phase prints a progress line at most every <see cref="ProgressInterval"/>.
/// </summary>
internal sealed class TextReporter(TextWriter output, TextWriter error) : IConversionReporter
{
    /// <summary>Least time between two progress lines of one phase.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private readonly Dictionary<int, string> _sampleNames = [];
    private StructureValue? _result;
    private TimeSpan _lastProgress;

    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        string? line = logEvent.Level switch
        {
            >= LogEventLevel.Warning => "note: " + OneLine(logEvent),
            LogEventLevel.Information => InformationLine(logEvent),
            _ => "debug " + OneLine(logEvent),
        };
        if (line is not null)
        {
            Write(output, line);
        }
    }

    /// <inheritdoc/>
    public void RunStarted(RunInfo run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Write(output, Join(
            "run",
            Pair("mod2xgm", RunInfo.Version),
            Pair("input", Path.GetFileName(run.Input)),
            Pair("format", run.Format),
            Pair("quality", run.Quality),
            Pair("steps", run.Phases.Count),
            run.Jobs > 1 ? Pair("jobs", run.Jobs) : null));
    }

    /// <inheritdoc/>
    public void Phase(PhaseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        string step = Invariant($"step {update.Phase.Number}/{update.Of} {update.Phase.Id}");
        string eta = "eta " + TextValues.Eta(update.TotalRemaining, update.TotalPartial);
        switch (update.Event)
        {
            case PhaseEvent.Start:
                _lastProgress = TimeSpan.Zero;
                Write(output, $"{step} start | {eta}");
                break;
            case PhaseEvent.Progress when update.Elapsed - _lastProgress >= ProgressInterval:
                _lastProgress = update.Elapsed;
                string done = update.Fraction is { } fraction
                    ? TextValues.Percent(fraction)
                    : Invariant($"{update.Done} {update.Phase.Unit}").TrimEnd();
                Write(output, $"{step} {done} | {TextValues.Seconds(update.Elapsed)} | {eta}");
                break;
            case PhaseEvent.End:
                Write(output, $"{step} done | {TextValues.Seconds(update.Elapsed)}");
                break;
            default:
                break;
        }
    }

    /// <inheritdoc/>
    public void Completed(RunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        WriteTimings(outcome.Timings, outcome.Elapsed);
        Write(output, Join(
            "result",
            outcome.Code == ExitCode.Success ? "ok" : "fail",
            Pair("exit", (int)outcome.Code),
            _result?.Get("output") is { } written ? Pair("out", written) : null,
            _result?.Get("wav") is { } wav ? Pair("wav", wav) : null,
            _result?.Get("notes") is { } notes ? Pair("notes", notes) : null,
            Pair("time", TextValues.Seconds(outcome.Elapsed))));
    }

    /// <inheritdoc/>
    public void Failed(RunFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        WriteTimings(failure.Timings, failure.Elapsed);
        Write(error, Join(
            "error",
            Pair("exit", (int)failure.Code),
            Pair("kind", failure.Kind),
            Pair("message", failure.Usage is { } usage ? string.Join("; ", usage.Problems) : failure.Message),
            failure.Usage is { Missing.Count: > 0 } missing ? Pair("missing", string.Join(",", missing.Missing)) : null,
            failure.Usage is not null ? Pair("usage", UsageException.UsageLine) : null));
        if (failure.Exception is not null)
        {
            foreach (string line in failure.Exception.ToString().Split('\n'))
            {
                Write(error, "stack " + line.TrimEnd('\r'));
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        output.Flush();
        error.Flush();
    }

    /// <summary>The line for an <c>Information</c> event: a report row's own line, or <c>info</c> and the message.</summary>
    private string? InformationLine(LogEvent logEvent)
    {
        if (ReportRow.Find(logEvent) is not { } row)
        {
            return "info " + OneLine(logEvent);
        }

        switch (row.TypeTag)
        {
            case "metadata":
                return Join(
                    "meta",
                    Pair("file", row.Get("file")),
                    Pair("bytes", row.Get("bytes")),
                    "title=" + Text(row.Get("title")),
                    Pair("format", row.Get("tracker")),
                    Pair("channels", row.Get("channels")),
                    Pair("slots", row.Get("slots")),
                    Pair("orders", row.Get("orders")),
                    Pair("restart", row.Get("restart")),
                    Pair("patterns", row.Get("patterns")),
                    Pair("speed", row.Get("speed")),
                    Pair("bpm", row.Get("bpm")));
            case "sample":
                int number = Convert.ToInt32(row.Get("n"), CultureInfo.InvariantCulture);
                string name = (string?)row.Get("name") ?? string.Empty;
                _sampleNames[number] = name;
                string loop = row.Get("loop_start") is { } start ? Invariant($"{start}+{row.Get("loop_length")}") : "-";
                return Join(
                    "sample",
                    Invariant($"{number:D2}"),
                    TextValues.Quote(name),
                    Pair("len", row.Get("length")),
                    Pair("loop", loop),
                    Pair("vol", row.Get("volume")),
                    Pair("fine", row.Get("finetune")),
                    Pair("notes", row.Get("notes")),
                    (string?)row.Get("status"));
            case "output":
                return Join(
                    "output",
                    (string?)row.Get("format"),
                    Pair("fm", row.Get("fm")),
                    Pair("psg", row.Get("psg")),
                    Pair("dac", row.Get("dac")),
                    Pair("pcm_bytes", row.Get("pcm_bytes")));
            case "instrument":
                return InstrumentLine(row);
            case "result":
                _result = row;
                return null;
            default:
                return null;
        }
    }

    /// <summary><c>output FM 01 &lt;- 01 "lead", 02 "pad" (Sine patch)</c>, or a DAC sample with its rate and size.</summary>
    private string InstrumentLine(StructureValue row)
    {
        string from = string.Join(", ", row.Numbers("from").Select(n => Invariant($"{n:D2} ") + TextValues.Quote(_sampleNames.GetValueOrDefault(n, string.Empty))));
        StringBuilder line = new(Invariant($"output {row.Get("kind")} {Convert.ToInt32(row.Get("n"), CultureInfo.InvariantCulture):D2}"));
        if (from.Length > 0)
        {
            line.Append(" <- ").Append(from);
        }

        if (row.Get("detail") is string detail)
        {
            line.Append(" (").Append(detail).Append(')');
        }

        foreach ((string field, string key) in new[] { ("rate_hz", "rate"), ("bytes", "bytes"), ("offset", "offset"), ("volume_db", "volume_db") })
        {
            if (row.Get(field) is { } value)
            {
                line.Append(' ').Append(Pair(key, value));
            }
        }

        return line.ToString();
    }

    /// <summary>The <c>time</c> line: every finished phase's time and the total.</summary>
    private void WriteTimings(IReadOnlyList<PhaseTiming> timings, TimeSpan elapsed)
    {
        if (timings.Count > 0)
        {
            Write(output, Join("time", [.. timings.Select(t => Pair(t.Phase.Id, TextValues.Seconds(t.Elapsed))), Pair("total", TextValues.Seconds(elapsed))]));
        }
    }

    private void Write(TextWriter writer, string line)
    {
        lock (_lock)
        {
            writer.WriteLine(line);
        }
    }

    /// <summary>The event's message on one line: line breaks become spaces.</summary>
    private static string OneLine(LogEvent logEvent) => TextValues.Message(logEvent).ReplaceLineEndings(" ");

    private static string Pair(string key, object? value) => key + "=" + TextValues.Format(value);

    /// <summary>A name or title is always quoted, so an empty one still shows.</summary>
    private static string Text(object? value) => TextValues.Quote(value as string ?? string.Empty);

    private static string Join(string keyword, params string?[] parts) =>
        string.Join(' ', parts.Where(p => p is not null).Prepend(keyword));

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
