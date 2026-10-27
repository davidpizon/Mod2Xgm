namespace Mod2Xgm.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void ApplicationAssemblyIsReferenced()
    {
        Assert.Equal("Mod2Xgm", typeof(Program).Assembly.GetName().Name);
    }
}
