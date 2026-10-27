namespace Mod2Xgm.Tests;

public sealed class ProgramTests
{
    [Fact]
    public void MainReturnsSuccess()
    {
        Assert.Equal((int)ExitCode.Success, Program.Main(["song.mod"]));
    }

    [Fact]
    public void RunReturnsTheBodysExitCode()
    {
        Assert.Equal((int)ExitCode.InvalidInput, Program.Run([], _ => ExitCode.InvalidInput));
    }

    [Fact]
    public void RunTurnsAnUnexpectedExceptionIntoInternalError()
    {
        Assert.Equal(
            (int)ExitCode.InternalError,
            Program.Run([], _ => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public void CreateLoggerReturnsALogger()
    {
        Assert.NotNull(Program.CreateLogger());
    }
}
