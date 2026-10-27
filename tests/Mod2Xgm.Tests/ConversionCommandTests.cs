using Mod2Xgm.Cli;

namespace Mod2Xgm.Tests;

public sealed class ConversionCommandTests
{
    private static ExitCode CodeOf(Action action) => Assert.Throws<ConversionException>(action).Code;

    [Fact]
    public void ExistingInputAndFreeOutputSucceeds()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod") };

        Assert.Equal(ExitCode.Success, ConversionCommand.Execute(options));
    }

    [Fact]
    public void MissingInputIsInputNotFound()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.Combine("missing.mod") };

        Assert.Equal(ExitCode.InputNotFound, CodeOf(() => ConversionCommand.Execute(options)));
    }

    [Fact]
    public void DirectoryInputIsInputNotFound()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.Path };

        Assert.Equal(ExitCode.InputNotFound, CodeOf(() => ConversionCommand.Execute(options)));
    }

    [Fact]
    public void ExistingOutputIsNotOverwrittenWithoutForce()
    {
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song.dmf");

        ConversionException error = Assert.Throws<ConversionException>(() => ConversionCommand.Execute(new() { InputPath = input }));

        Assert.Equal(ExitCode.OutputNotWritable, error.Code);
        Assert.Contains("--force", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForceAllowsAnExistingOutput()
    {
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song.dmf");

        Assert.Equal(ExitCode.Success, ConversionCommand.Execute(new() { InputPath = input, Force = true }));
    }

    [Fact]
    public void InfoSkipsOutputChecks()
    {
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        temp.CreateFile("song.dmf");

        Assert.Equal(ExitCode.Success, ConversionCommand.Execute(new() { InputPath = input, InfoOnly = true }));
    }

    [Fact]
    public void OutputMayNeverBeTheInput()
    {
        using TempDirectory temp = new();
        string input = temp.CreateFile("song.mod");
        ConversionOptions options = new() { InputPath = input, OutputPath = input, Force = true };

        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(options)));
    }

    [Fact]
    public void OutputDirectoryMustExist()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new()
        {
            InputPath = temp.CreateFile("song.mod"),
            OutputPath = Path.Combine(temp.Path, "missing", "song.dmf"),
        };

        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(options)));
    }

    [Fact]
    public void OutputMustNotBeADirectory()
    {
        using TempDirectory temp = new();
        ConversionOptions options = new() { InputPath = temp.CreateFile("song.mod"), OutputPath = temp.Path };

        Assert.Equal(ExitCode.OutputNotWritable, CodeOf(() => ConversionCommand.Execute(options)));
    }
}
