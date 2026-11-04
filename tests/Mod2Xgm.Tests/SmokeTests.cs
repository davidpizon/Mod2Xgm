namespace Mod2Xgm.Tests;

/// <summary>Checks that the test project is wired to the application it tests.</summary>
public sealed class SmokeTests
{
    /// <summary>The <see cref="Program"/> type comes from the Mod2Xgm assembly.</summary>
    [Fact]
    public void ProgramAssembly_WhenReferenced_IsMod2Xgm()
    {
        // Act
        string? name = typeof(Program).Assembly.GetName().Name;

        // Assert
        Assert.Equal("Mod2Xgm", name);
    }
}
