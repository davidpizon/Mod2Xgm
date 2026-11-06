using Mod2Xgm.Optimizer;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Tests for <see cref="Fingerprint"/> and <see cref="Hasher"/>: the short keys the pre-mixer matches windows by.</summary>
public sealed class FingerprintTests
{
    /// <summary>The same numbers give the same fingerprint.</summary>
    [Fact]
    public void Of_WithSameNumbers_GivesSameFingerprint()
    {
        // Act
        Fingerprint first = Of(1, 0.5);
        Fingerprint second = Of(1, 0.5);

        // Assert
        Assert.Equal(first, second);
    }

    /// <summary>Another number gives another fingerprint.</summary>
    [Fact]
    public void Of_WithOtherNumber_GivesOtherFingerprint()
    {
        // Act
        Fingerprint first = Of(1, 0.5);
        Fingerprint second = Of(1, 0.25);

        // Assert
        Assert.NotEqual(first, second);
    }

    /// <summary>The same numbers in another order give another fingerprint.</summary>
    [Fact]
    public void Of_WithOtherOrder_GivesOtherFingerprint()
    {
        // Act
        Fingerprint first = Of(1, 2.0);
        Fingerprint second = Of(2, 1.0);

        // Assert
        Assert.NotEqual(first, second);
    }

    /// <summary>A hasher started from a fingerprint goes on from it, and finishing early doesn't stop it.</summary>
    [Fact]
    public void From_WithFingerprint_ContinuesHash()
    {
        // Arrange
        using Hasher first = new();
        first.Add(7);
        Fingerprint sut = first.Finish();
        first.Add(8);
        using Hasher next = new(sut);

        // Act
        next.Add(8);

        // Assert
        Assert.NotEqual(sut, first.Finish());
        Assert.NotEqual(next.Finish(), first.Finish());
        Assert.Equal(next.Finish(), Of(sut, 8));
    }

    /// <summary>A fingerprint of bytes is SHA-256's, read back the way it is written.</summary>
    [Fact]
    public void Of_WithBytes_HashesAsSha256()
    {
        // Act
        Fingerprint sut = Fingerprint.Of([]);

        // Assert
        byte[] hash = new byte[Fingerprint.Size];
        sut.WriteTo(hash);
        Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", Convert.ToHexString(hash));
        Assert.Equal(sut, Fingerprint.From(hash));
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
