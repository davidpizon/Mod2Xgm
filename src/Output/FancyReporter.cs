using System.Globalization;
using Serilog.Events;
using Spectre.Console;

namespace Mod2Xgm.Output;

/// <summary>
/// <c>--progress fancy</c>, the default, for people (docs/cli-output.md#fancy, ADR 0077): drawn with Spectre.Console.
/// A header rule, the report sections as a grid and tables, a live area with the step's bar and the run's total bar,
/// notes in yellow, detail dimmed, a timing table and the result, all on stdout; the error on stderr, after the live
/// area has closed. On a console that can't redraw (output piped, <c>TERM=dumb</c>) there is no live area: each
/// finished step gets one static line instead (the T17.1 spike: Spectre draws nothing for a live display there).
/// Names are always written as <see cref="Text"/> or through <see cref="Markup.Escape"/>, never as markup: they may
/// hold <c>[</c> and <c>]</c>.
/// </summary>
internal sealed class FancyReporter : IConversionReporter
{
    private readonly IAnsiConsole _console;
    private readonly IAnsiConsole _error;
    private readonly bool _live;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, string> _sampleNames = [];
    private readonly List<StructureValue> _samples = [];
    private readonly List<StructureValue> _instruments = [];
    private StructureValue? _output;
    private StructureValue? _result;
    private LiveArea? _area;

    /// <summary>
    /// A reporter drawing on <paramref name="console"/> (stdout) and <paramref name="error"/> (stderr); <paramref name="live"/>
    /// when the console can redraw in place.
    /// </summary>
    public FancyReporter(IAnsiConsole console, IAnsiConsole error, bool live)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(error);
        _console = console;
        _error = error;
        _live = live;
    }

    /// <summary>A reporter on <paramref name="output"/> and <paramref name="error"/>, set up from <paramref name="terminal"/>.</summary>
    public static FancyReporter Create(TextWriter output, TextWriter error, TerminalProbe terminal)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(terminal);
        IAnsiConsole console = Console(output, terminal is { OutputRedirected: false, DumbTerminal: false }, terminal);
        IAnsiConsole errorConsole = Console(error, terminal is { ErrorRedirected: false, DumbTerminal: false }, terminal);
        return new FancyReporter(console, errorConsole, terminal.Interactive && console.Profile.Capabilities.Interactive);
    }

    /// <inheritdoc/>
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (_lock)
        {
            if (logEvent.Level == LogEventLevel.Information && ReportRow.Find(logEvent) is { } row)
            {
                Collect(row);
                return;
            }

            FlushTables();
            string message = TextValues.Message(logEvent);
            _console.Write(logEvent.Level switch
            {
                >= LogEventLevel.Warning => new Markup("[yellow]note[/] " + Markup.Escape(message) + "\n"),
                LogEventLevel.Information => new Text(message + "\n"),
                _ => new Text(message + "\n", new Style(decoration: Decoration.Dim)),
            });
        }
    }

    /// <inheritdoc/>
    public void RunStarted(RunInfo run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_lock)
        {
            string title = run.Format == RunInfo.HarnessFormat
                ? Invariant($"mod2xgm harness {RunInfo.Version} · {run.Phases.Count} songs, {run.Jobs} at a time")
                : Invariant($"mod2xgm {RunInfo.Version} · {Path.GetFileName(run.Input)} → {run.Format.ToUpperInvariant()} · quality {run.Quality} · {run.Phases.Count} steps");
            _console.Write(new Rule(Markup.Escape(title)) { Justification = Justify.Left });
        }
    }

    /// <inheritdoc/>
    public void Phase(PhaseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_lock)
        {
            FlushTables();
            if (!_live)
            {
                if (update.Event == PhaseEvent.End)
                {
                    _console.MarkupLine(Invariant($"[green]✓[/] [[{update.Phase.Number}/{update.Of}]] {Markup.Escape(update.Phase.Name)} [dim]{Seconds(update.Elapsed)}[/]"));
                }

                return;
            }

            _area ??= new LiveArea(_console);
            if (_area.Show(update))
            {
                CloseArea();
            }
        }
    }

    /// <inheritdoc/>
    public void Completed(RunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        lock (_lock)
        {
            CloseArea();
            FlushTables();
            WriteTimings(outcome.Timings, outcome.Elapsed);
            string target = _result?.Get("output") as string ?? _result?.Get("wav") as string ?? "no file written";
            string notes = _result?.Get("notes") is { } n ? Invariant($", {n} conversion notes") : string.Empty;
            string verdict = outcome.Code == ExitCode.Success ? "[green]Done[/]" : Invariant($"[red]Finished with exit code {(int)outcome.Code}[/]");
            _console.MarkupLine(Invariant($"{verdict} → {Markup.Escape(target)}{Markup.Escape(notes)} [dim]in {Seconds(outcome.Elapsed)}[/]"));
        }
    }

    /// <inheritdoc/>
    public void Failed(RunFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_lock)
        {
            CloseArea();
            FlushTables();
            WriteTimings(failure.Timings, failure.Elapsed);
            if (failure.Usage is { } usage)
            {
                foreach (string problem in usage.Problems)
                {
                    _error.MarkupLine("[red]error[/] " + Markup.Escape(problem));
                }

                _error.MarkupLine("Usage: " + Markup.Escape(UsageException.UsageLine));
                _error.MarkupLine("[dim]" + Markup.Escape(UsageException.HelpHint) + "[/]");
                return;
            }

            _error.MarkupLine(Invariant($"[red]error[/] {Markup.Escape(failure.Message)} [dim](exit {(int)failure.Code})[/]"));
            if (failure.Exception is not null)
            {
                _error.Write(new Text(failure.Exception + "\n"));
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            CloseArea();
            FlushTables();
        }
    }

    private static IAnsiConsole Console(TextWriter writer, bool terminal, TerminalProbe probe)
    {
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = terminal ? AnsiSupport.Detect : AnsiSupport.No,
            ColorSystem = probe.NoColor || !terminal ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Interactive = terminal ? InteractionSupport.Detect : InteractionSupport.No,
        });
        if (probe.Width is { } width)
        {
            console.Profile.Width = width;
        }

        return console;
    }

    /// <summary>Keeps a report row until its table is complete; the metadata prints at once.</summary>
    private void Collect(StructureValue row)
    {
        switch (row.TypeTag)
        {
            case "metadata":
                FlushTables();
                _console.Write(MetadataGrid(row));
                break;
            case "sample":
                _sampleNames[Convert.ToInt32(row.Get("n"), CultureInfo.InvariantCulture)] = row.Get("name") as string ?? string.Empty;
                _samples.Add(row);
                break;
            case "output":
                FlushTables();
                _output = row;
                break;
            case "instrument":
                _instruments.Add(row);
                break;
            case "result":
                FlushTables();
                _result = row;
                break;
        }
    }

    /// <summary>Prints the sample table and the output table once their last row has come (the next other line).</summary>
    private void FlushTables()
    {
        if (_samples.Count > 0)
        {
            _console.Write(SampleTable(_samples));
            _samples.Clear();
        }

        if (_output is not null)
        {
            _console.Write(OutputTable(_output, _instruments));
            _output = null;
            _instruments.Clear();
        }
    }

    private void CloseArea()
    {
        _area?.Dispose();
        _area = null;
    }

    private static Grid MetadataGrid(StructureValue row)
    {
        Grid grid = new();
        grid.AddColumn(new GridColumn().NoWrap().PadRight(2));
        grid.AddColumn();
        AddRow(grid, "File", Invariant($"{row.Get("file")} ({row.Get("bytes")} bytes)"));
        AddRow(grid, "Title", "\"" + (row.Get("title") as string ?? string.Empty) + "\"");
        AddRow(grid, "Format", Invariant($"{row.Get("tracker")}: {row.Get("channels")} channels, {row.Get("slots")} sample slots"));
        AddRow(grid, "Song length", Invariant($"{row.Get("orders")} orders, restart position {row.Get("restart")}"));
        AddRow(grid, "Patterns", Invariant($"{row.Get("patterns")}"));
        AddRow(grid, "Tempo", Invariant($"speed {row.Get("speed")}, {Convert.ToDouble(row.Get("bpm"), CultureInfo.InvariantCulture):0.##} BPM (initial)"));
        return grid;

        static void AddRow(Grid grid, string label, string value) =>
            grid.AddRow(new Text(label, new Style(decoration: Decoration.Bold)), new Text(value));
    }

    private static Table SampleTable(IReadOnlyList<StructureValue> rows)
    {
        Table table = new Table().Border(TableBorder.Rounded).Title("Samples");
        table.AddColumn(new TableColumn("#").RightAligned());
        table.AddColumn("Name");
        table.AddColumn(new TableColumn("Length").RightAligned());
        table.AddColumn("Loop");
        table.AddColumn(new TableColumn("Vol").RightAligned());
        table.AddColumn(new TableColumn("Fine").RightAligned());
        table.AddColumn(new TableColumn("Notes").RightAligned());
        table.AddColumn("Status");
        foreach (StructureValue row in rows)
        {
            string status = row.Get("status") as string ?? string.Empty;
            Style style = status switch
            {
                "used" => new Style(Color.Green),
                "ignored" => new Style(Color.Yellow),
                _ => new Style(decoration: Decoration.Dim),
            };
            string loop = row.Get("loop_start") is { } start ? Invariant($"{start}+{row.Get("loop_length")}") : "—";
            table.AddRow(
                new Text(Invariant($"{row.Get("n"):D2}")),
                new Text("\"" + (row.Get("name") as string ?? string.Empty) + "\""),
                new Text(Invariant($"{row.Get("length")}")),
                new Text(loop),
                new Text(Invariant($"{row.Get("volume")}")),
                new Text(Invariant($"{row.Get("finetune")}")),
                new Text(Invariant($"{row.Get("notes")}")),
                new Text(status, style));
        }

        return table;
    }

    private Table OutputTable(StructureValue output, IReadOnlyList<StructureValue> instruments)
    {
        string title = Invariant($"Output ({(output.Get("format") as string ?? string.Empty).ToUpperInvariant()}): {output.Get("fm")} FM, {output.Get("psg")} PSG, {output.Get("dac")} DAC, {output.Get("pcm_bytes")} B PCM");
        Table table = new Table().Border(TableBorder.Rounded).Title(Markup.Escape(title));
        table.AddColumn("Kind");
        table.AddColumn(new TableColumn("#").RightAligned());
        table.AddColumn("From");
        table.AddColumn("Detail");
        foreach (StructureValue row in instruments)
        {
            string from = string.Join(", ", row.Numbers("from").Select(n => Invariant($"{n:D2} \"{_sampleNames.GetValueOrDefault(n, string.Empty)}\"")));
            List<string> detail = [];
            if (row.Get("detail") is string d)
            {
                detail.Add(d);
            }

            if (row.Get("rate_hz") is { } rate)
            {
                detail.Add(Invariant($"{rate} Hz, {row.Get("bytes")} B"));
            }

            if (row.Get("offset") is { } offset)
            {
                detail.Add(Invariant($"at {offset}"));
            }

            if (row.Get("volume_db") is { } db)
            {
                detail.Add(Invariant($"{Convert.ToDouble(db, CultureInfo.InvariantCulture):0.#} dB"));
            }

            table.AddRow(
                new Text(row.Get("kind") as string ?? string.Empty),
                new Text(Invariant($"{row.Get("n"):D2}")),
                new Text(from),
                new Text(string.Join(", ", detail)));
        }

        return table;
    }

    /// <summary>The timing table: each finished step's time and work, then the total.</summary>
    private void WriteTimings(IReadOnlyList<PhaseTiming> timings, TimeSpan elapsed)
    {
        if (timings.Count == 0)
        {
            return;
        }

        Table table = new Table().Border(TableBorder.Simple).Title("Time");
        table.AddColumn("Step");
        table.AddColumn(new TableColumn("Time").RightAligned());
        table.AddColumn(new TableColumn("Work").RightAligned());
        foreach (PhaseTiming timing in timings)
        {
            table.AddRow(
                new Text(Invariant($"{timing.Phase.Number}. {timing.Phase.Name}")),
                new Text(Seconds(timing.Elapsed)),
                new Text(timing.Phase.Unit.Length > 0 ? Invariant($"{timing.Done} {timing.Phase.Unit}") : string.Empty));
        }

        Style bold = new(decoration: Decoration.Bold);
        table.AddRow(new Text("Total", bold), new Text(Seconds(elapsed), bold), new Text(string.Empty));
        _console.Write(table);
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The live area: a row per running step (the harness runs <c>--jobs</c> songs at once; between two steps, the one
    /// just finished) and a total row, redrawn in place by Spectre's <see cref="LiveDisplay"/>. A live display
    /// runs its callback to the end on the calling thread, but the conversion calls the reporter step by step, so the
    /// display runs on its own thread and is fed with <see cref="LiveDisplayContext.UpdateTarget"/>. Lines written to the
    /// console meanwhile are printed above it by Spectre.
    /// </summary>
    private sealed class LiveArea : IDisposable
    {
        private const int LabelWidth = 22;
        private const int BarWidth = 20;

        private readonly Thread _thread;
        private readonly ManualResetEventSlim _started = new();
        private readonly ManualResetEventSlim _stop = new();
        private readonly IAnsiConsole _console;
        private readonly List<PhaseUpdate> _running = [];
        private LiveDisplayContext? _context;
        private PhaseUpdate? _update;
        private int _ended;

        /// <summary>Starts the live display on <paramref name="console"/>.</summary>
        public LiveArea(IAnsiConsole console)
        {
            _console = console;
            _thread = new Thread(Run) { IsBackground = true, Name = "mod2xgm live area" };
            _thread.Start();
            _started.Wait();
        }

        /// <summary>Redraws the rows for <paramref name="update"/>; <c>true</c> once every step has ended.</summary>
        public bool Show(PhaseUpdate update)
        {
            _update = update;
            int index = _running.FindIndex(u => u.Phase.Id == update.Phase.Id);
            if (update.Event == PhaseEvent.End)
            {
                _ended++;
                if (index >= 0)
                {
                    _running.RemoveAt(index);
                }
            }
            else if (index >= 0)
            {
                _running[index] = update;
            }
            else
            {
                _running.Add(update);
            }

            _context?.UpdateTarget(Rows());
            return _ended >= update.Of || (update.Event == PhaseEvent.End && update.Phase.Number == update.Of && _running.Count == 0);
        }

        /// <summary>Draws the last rows once more and ends the display; they stay on the screen.</summary>
        public void Dispose()
        {
            _stop.Set();
            _thread.Join();
            _started.Dispose();
            _stop.Dispose();
        }

        private void Run() =>
            _console.Live(new Text(string.Empty)).AutoClear(false).Start(context =>
            {
                _context = context;
                _started.Set();
                _stop.Wait();
                context.UpdateTarget(Rows());
            });

        private Rows Rows()
        {
            if (_update is not { } u)
            {
                return new Rows(new Text(string.Empty));
            }

            IReadOnlyList<PhaseUpdate> shown = _running.Count > 0 ? _running : [u];
            double total = u.TotalRemaining is { } r && (u.TotalElapsed + r).TotalSeconds > 0
                ? u.TotalElapsed.TotalSeconds / (u.TotalElapsed + r).TotalSeconds
                : Math.Min(1, (_ended + _running.Sum(p => p.Fraction ?? 0)) / u.Of);
            List<IRenderable> rows = [.. shown.Select(p => Row(Invariant($"[{p.Phase.Number}/{p.Of}] {p.Phase.Name}"), p.Event == PhaseEvent.End ? 1 : p.Fraction, p.Elapsed, p.Remaining, false, Decoration.None))];
            rows.Add(Row("Total", total, u.TotalElapsed, u.TotalRemaining, u.TotalPartial, Decoration.Bold));
            return new Rows(rows);
        }

        /// <summary>One line: label, bar, percentage, time spent and time left; it fits in 80 columns.</summary>
        private static Paragraph Row(string label, double? fraction, TimeSpan elapsed, TimeSpan? remaining, bool partial, Decoration decoration)
        {
            string left = remaining is { } r ? "about " + TextValues.Eta(r, partial) + " left" : "time left unknown";
            string shortLabel = label.Length > LabelWidth ? label[..(LabelWidth - 1)] + "…" : label;
            Paragraph row = new();
            row.Append(shortLabel.PadRight(LabelWidth + 1), new Style(decoration: decoration));
            Bar(row, fraction);
            row.Append((fraction is { } f ? TextValues.Percent(f) : "…").PadLeft(5) + "  ");
            row.Append(Seconds(elapsed) + " · " + left, new Style(decoration: Decoration.Dim));
            return row;
        }

        /// <summary>Appends a bar of <see cref="BarWidth"/> cells to <paramref name="row"/>, the done part green; all dim while the share is unknown.</summary>
        private static void Bar(Paragraph row, double? fraction)
        {
            int done = fraction is { } f ? (int)Math.Round(Math.Clamp(f, 0, 1) * BarWidth) : 0;
            row.Append(new string('━', done), new Style(Color.Green));
            row.Append(new string('━', BarWidth - done), new Style(Color.Grey, decoration: Decoration.Dim));
        }
    }
}
