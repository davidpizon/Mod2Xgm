using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog.Events;

namespace Mod2Xgm.Output;

/// <summary>
/// <c>--progress json</c>, for programs (docs/cli-output.md#json, ADR 0077): one JSON object per line (NDJSON) on stdout,
/// every object with <c>"v":1</c> and a <c>"type"</c>; nothing on stderr. Written with <see cref="Utf8JsonWriter"/>, so
/// nothing is serialized by reflection. A phase's progress objects come at most every <see cref="ProgressInterval"/>; the
/// last line of every run is a <c>result</c> or an <c>error</c>.
/// </summary>
internal sealed class JsonReporter(TextWriter output) : IConversionReporter
{
    /// <summary>The schema version every object carries; bumped when a field changes meaning.</summary>
    private const int SchemaVersion = 1;

    /// <summary>Least time between two progress objects of one phase.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Names and paths stay readable (é, ←, \); the writer still escapes quotes and control characters.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Lock _lock = new();
    private StructureValue? _result;
    private TimeSpan _lastProgress;

    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (ReportRow.Find(logEvent) is { } row)
        {
            WriteRow(row);
            return;
        }

        string type = logEvent.Level switch
        {
            >= LogEventLevel.Warning => "note",
            LogEventLevel.Information => "info",
            _ => "debug",
        };
        Write(type, json =>
        {
            json.WriteString("message", TextValues.Message(logEvent));
            if (type == "info")
            {
                return;
            }

            json.WriteString("template", logEvent.MessageTemplate.Text);
            json.WriteStartObject("properties");
            foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties)
            {
                json.WritePropertyName(property.Key);
                WriteValue(json, property.Value);
            }

            json.WriteEndObject();
        });
    }

    /// <inheritdoc/>
    public void RunStarted(RunInfo run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Write("run", json =>
        {
            json.WriteString("version", RunInfo.Version);
            json.WriteString("input", run.Input);
            json.WriteString("format", run.Format);
            json.WriteString("quality", run.Quality);
            if (run.Jobs > 1)
            {
                json.WriteNumber("jobs", run.Jobs);
            }

            json.WriteStartArray("steps");
            foreach (PhaseInfo phase in run.Phases)
            {
                json.WriteStartObject();
                json.WriteNumber("n", phase.Number);
                json.WriteString("id", phase.Id);
                json.WriteString("name", phase.Name);
                json.WriteString("unit", phase.Unit);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        });
    }

    /// <inheritdoc/>
    public void Phase(PhaseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Event == PhaseEvent.Start)
        {
            _lastProgress = TimeSpan.Zero;
        }
        else if (update.Event == PhaseEvent.Progress)
        {
            if (update.Elapsed - _lastProgress < ProgressInterval)
            {
                return;
            }

            _lastProgress = update.Elapsed;
        }

        Write("phase", json =>
        {
            json.WriteNumber("n", update.Phase.Number);
            json.WriteNumber("of", update.Of);
            json.WriteString("id", update.Phase.Id);
            json.WriteString("event", update.Event switch
            {
                PhaseEvent.Start => "start",
                PhaseEvent.Progress => "progress",
                _ => "end",
            });
            json.WriteNumber("done", update.Done);
            json.WriteNumber("total", update.Total);
            json.WriteString("unit", update.Phase.Unit);
            json.WriteNumber("elapsed_s", Seconds(update.Elapsed));
            WriteSeconds(json, "eta_s", update.Remaining);
            WriteSeconds(json, "total_eta_s", update.TotalRemaining);
            json.WriteBoolean("eta_partial", update.TotalPartial);
        });
    }

    /// <inheritdoc/>
    public void Completed(RunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        WriteTimings(outcome.Timings, outcome.Elapsed);
        Write("result", json =>
        {
            json.WriteNumber("exit", (int)outcome.Code);
            WriteField(json, "output", _result?.Get("output"));
            WriteField(json, "wav", _result?.Get("wav"));
            WriteField(json, "notes", _result?.Get("notes"));
            json.WriteNumber("seconds", Seconds(outcome.Elapsed));
        });
    }

    /// <inheritdoc/>
    public void Failed(RunFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        WriteTimings(failure.Timings, failure.Elapsed);
        Write("error", json =>
        {
            json.WriteNumber("code", (int)failure.Code);
            json.WriteString("error", failure.Kind);
            json.WriteString("message", failure.Usage is { } usage ? string.Join(" ", usage.Problems) : failure.Message);
            if (failure.Usage is not null)
            {
                json.WriteStartArray("problems");
                foreach (string problem in failure.Usage.Problems)
                {
                    json.WriteStringValue(problem);
                }

                json.WriteEndArray();
                json.WriteStartArray("missing");
                foreach (string missing in failure.Usage.Missing)
                {
                    json.WriteStringValue(missing);
                }

                json.WriteEndArray();
                json.WriteString("usage", UsageException.UsageLine);
            }

            if (failure.Exception is not null)
            {
                json.WriteString("stack", failure.Exception.ToString());
            }
        });
    }

    /// <inheritdoc/>
    public void Dispose() => output.Flush();

    /// <summary>A report row as an object of its kind: <c>metadata</c>, <c>sample</c>, <c>output</c>, <c>instrument</c>.</summary>
    private void WriteRow(StructureValue row)
    {
        switch (row.TypeTag)
        {
            case "result":
                _result = row;
                return;
            case "part" or null:
                return;
            default:
                Write(row.TypeTag, json =>
                {
                    foreach (LogEventProperty field in row.Properties)
                    {
                        json.WritePropertyName(field.Name);
                        WriteValue(json, field.Value);
                    }
                });
                return;
        }
    }

    private void WriteTimings(IReadOnlyList<PhaseTiming> timings, TimeSpan elapsed)
    {
        if (timings.Count == 0)
        {
            return;
        }

        Write("timing", json =>
        {
            json.WriteStartArray("phases");
            foreach (PhaseTiming timing in timings)
            {
                json.WriteStartObject();
                json.WriteString("id", timing.Phase.Id);
                json.WriteNumber("seconds", Seconds(timing.Elapsed));
                json.WriteNumber("done", timing.Done);
                json.WriteString("unit", timing.Phase.Unit);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteNumber("total_s", Seconds(elapsed));
        });
    }

    /// <summary>Writes one object of <paramref name="type"/> on its own line.</summary>
    private void Write(string type, Action<Utf8JsonWriter> fields)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteNumber("v", SchemaVersion);
            json.WriteString("type", type);
            fields(json);
            json.WriteEndObject();
        }

        string line = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        lock (_lock)
        {
            output.WriteLine(line);
        }
    }

    /// <summary>A log value as JSON: scalars by their type, sequences as arrays, structures as objects.</summary>
    private static void WriteValue(Utf8JsonWriter json, LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue scalar:
                WriteScalar(json, scalar.Value);
                break;
            case SequenceValue sequence:
                json.WriteStartArray();
                foreach (LogEventPropertyValue element in sequence.Elements)
                {
                    WriteValue(json, element);
                }

                json.WriteEndArray();
                break;
            case StructureValue structure:
                json.WriteStartObject();
                foreach (LogEventProperty property in structure.Properties)
                {
                    json.WritePropertyName(property.Name);
                    WriteValue(json, property.Value);
                }

                json.WriteEndObject();
                break;
            default:
                json.WriteStringValue(value.ToString(null, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static void WriteScalar(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;
            case bool flag:
                json.WriteBooleanValue(flag);
                break;
            case int or long or short or byte or sbyte or ushort or uint:
                json.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong number:
                json.WriteNumberValue(number);
                break;
            case double number when double.IsFinite(number):
                json.WriteNumberValue(number);
                break;
            case float number when float.IsFinite(number):
                json.WriteNumberValue(number);
                break;
            case decimal number:
                json.WriteNumberValue(number);
                break;
            case IFormattable formattable:
                json.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                json.WriteStringValue(value.ToString());
                break;
        }
    }

    private static void WriteField(Utf8JsonWriter json, string name, object? value)
    {
        json.WritePropertyName(name);
        WriteScalar(json, value);
    }

    private static void WriteSeconds(Utf8JsonWriter json, string name, TimeSpan? time)
    {
        if (time is { } t)
        {
            json.WriteNumber(name, Seconds(t));
        }
        else
        {
            json.WriteNull(name);
        }
    }

    /// <summary>Seconds with millisecond precision.</summary>
    private static double Seconds(TimeSpan time) => Math.Round(time.TotalSeconds, 3);
}
