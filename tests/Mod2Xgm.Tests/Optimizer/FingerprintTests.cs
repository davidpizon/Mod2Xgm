using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="Fingerprint"/> and <see cref="Hasher"/>: the short keys the pre-mixer matches windows by.</summary>
public sealed class FingerprintTests
{
    /// <summary>The same numbers give the same fingerprint; another number, or the same ones in another order, another.</summary>
    [Fact]
    public void TheSameNumbersGiveTheSameFingerprint()
    {
        Assert.Equal(Of(1, 0.5), Of(1, 0.5));
        Assert.NotEqual(Of(1, 0.5), Of(1, 0.25));
        Assert.NotEqual(Of(1, 2.0), Of(2, 1.0));
    }

    /// <summary>A hasher started from a fingerprint goes on from it, and finishing early doesn't stop it.</summary>
    [Fact]
    public void AHasherCanStartFromAFingerprint()
    {
        using Hasher first = new();
        first.Add(7);
        Fingerprint head = first.Finish();
        first.Add(8);

        using Hasher next = new(head);
        next.Add(8);

        Assert.NotEqual(head, first.Finish());
        Assert.NotEqual(next.Finish(), first.Finish());
        Assert.Equal(next.Finish(), Of(head, 8));
    }

    /// <summary>A fingerprint of bytes is SHA-256's, read back the way it is written.</summary>
    [Fact]
    public void BytesHashAsSha256()
    {
        Fingerprint empty = Fingerprint.Of([]);
        byte[] hash = new byte[Fingerprint.Size];
        empty.WriteTo(hash);

        Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", Convert.ToHexString(hash));
        Assert.Equal(empty, Fingerprint.From(hash));
    }

    private static Fingerprint Of(int a, double b)
    {
        using Hasher hasher = new();
        hasher.Add(a);
        hasher.Add(b);
        return hasher.Finish();
    }

    private static Fingerprint Of(Fingerprint prefix, int a)
    {
        using Hasher hasher = new(prefix);
        hasher.Add(a);
        return hasher.Finish();
    }
}
