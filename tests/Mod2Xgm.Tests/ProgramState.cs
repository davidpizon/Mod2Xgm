namespace Mod2Xgm.Tests;

/// <summary>
/// Tests that run <c>Program.Run</c>, which sets the static Serilog logger, level switch and progress: they run one at a
/// time, or one run's lines could reach another run's writers.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProgramState
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Program state";
}
