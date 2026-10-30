using Mod2Xgm.Render;

namespace Mod2Xgm.Tests.Render;

/// <summary>Tests for <see cref="StereoAudio"/>, the stereo audio the renderers return and the harness compares.</summary>
public sealed class StereoAudioTests
{
    /// <summary>A sine tone, the same array in both channels.</summary>
    private static StereoAudio Tone(double seconds, int sampleRate = 8000)
    {
        float[] samples = new float[(int)(seconds * sampleRate)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / sampleRate);
        }

        return new StereoAudio(samples, samples, sampleRate);
    }

    /// <summary>Resampling keeps the length in seconds, a mono recording stays one shared array, and the mix averages the channels.</summary>
    [Fact]
    public void ResamplesAndMixes()
    {
        StereoAudio mono = Tone(1.0);
        StereoAudio stereo = new([1f, 0f], [0f, -1f], 8000);

        StereoAudio up = mono.At(16_000);

        Assert.Same(mono, mono.At(8000));
        Assert.Equal(16_000, up.Length);
        Assert.Same(up.Left, up.Right);
        Assert.Equal(4, stereo.At(16_000).Length);
        Assert.Equal([0.5f, -0.5f], stereo.ToMono().Samples.ToArray());
    }

    /// <summary>The head is the first seconds of both channels, or the whole audio when it is shorter.</summary>
    [Fact]
    public void HeadCutsBothChannels()
    {
        StereoAudio mono = Tone(1.0);
        StereoAudio stereo = new([1f, 2f, 3f, 4f], [5f, 6f, 7f, 8f], 4);

        StereoAudio head = stereo.Head(0.5);

        Assert.Equal([1f, 2f], head.Left);
        Assert.Equal([5f, 6f], head.Right);
        Assert.Equal(0.5, head.Seconds);
        Assert.Same(stereo, stereo.Head(2));
        StereoAudio monoHead = mono.Head(0.25);
        Assert.Same(monoHead.Left, monoHead.Right);
        Assert.Equal(2000, monoHead.Length);
    }
}
