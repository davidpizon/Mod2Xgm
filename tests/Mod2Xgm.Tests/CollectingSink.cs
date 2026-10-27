using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Mod2Xgm.Tests;

/// <summary>A Serilog sink that keeps every event, for asserting on log output.</summary>
internal sealed class CollectingSink : ILogEventSink
{
    public List<LogEvent> Events { get; } = [];

    public void Emit(LogEvent logEvent) => Events.Add(logEvent);

    /// <summary>A logger at <paramref name="minimumLevel"/> writing only to this sink.</summary>
    public ILogger CreateLogger(LogEventLevel minimumLevel = LogEventLevel.Verbose) =>
        new LoggerConfiguration().MinimumLevel.Is(minimumLevel).WriteTo.Sink(this).CreateLogger();

    public IEnumerable<string> Messages(LogEventLevel level) =>
        Events.Where(e => e.Level == level).Select(e => e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture));
}
