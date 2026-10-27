namespace Mod2Xgm.Tests;

public sealed class ProgramTests
{
    [Fact]
    public void MainReturnsSuccessForAnExistingInput()
    {
        using TempDirectory temp = new();
        Assert.Equal((int)ExitCode.Success, Program.Main([temp.CreateFile("song.mod")]));
    }

    [Fact]
    public void MainReturnsInputNotFoundForAMissingInput()
    {
        using TempDirectory temp = new();
        Assert.Equal((int)ExitCode.InputNotFound, Program.Main([temp.Combine("missing.mod")]));
    }

    [Fact]
    public void MainReturnsUsageErrorWithoutArguments()
    {
        Assert.Equal((int)ExitCode.UsageError, Program.Main([]));
    }

    [Fact]
    public void RunReturnsTheBodysExitCode()
    {
        Assert.Equal((int)ExitCode.InvalidInput, Program.Run([], _ => ExitCode.InvalidInput));
    }

    [Fact]
    public void RunReturnsTheCodeOfAConversionException()
    {
        Assert.Equal(
            (int)ExitCode.OutputNotWritable,
            Program.Run([], _ => throw new ConversionException(ExitCode.OutputNotWritable, "exists")));
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

    [Fact]
    public void ConversionExceptionKeepsItsInnerException()
    {
        IOException inner = new("disk");
        ConversionException error = new(ExitCode.InputNotFound, "unreadable", inner);

        Assert.Same(inner, error.InnerException);
        Assert.Equal(ExitCode.InputNotFound, error.Code);
    }
}
