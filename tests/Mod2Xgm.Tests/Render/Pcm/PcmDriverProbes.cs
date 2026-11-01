namespace Mod2Xgm.Tests.Render.Pcm;

/// <summary>
/// The PCM driver probes (plan T7.9, tests/golden/pcm-drivers/README.md): short songs of PCM commands, played by the
/// models and by SGDK's real XGM and XGM2 Z80 drivers on an emulated Z80 (<c>tools/pcm-driver-reference</c>), whose
/// DAC writes are compared with the models'. Each sample in a start probe is played once, so its first byte shows when
/// it starts.
/// </summary>
internal static class PcmDriverProbes
{
    /// <summary>Every probe, by name.</summary>
    public static IReadOnlyDictionary<string, Func<PcmDriverScript>> All { get; } = new Dictionary<string, Func<PcmDriverScript>>
    {
        ["xgm-starts"] = XgmStarts,
        ["xgm-mix"] = XgmMix,
        ["xgm2-starts"] = Xgm2Starts,
        ["xgm2-mix"] = Xgm2Mix,
        ["xgm2-stop"] = Xgm2Stop,
        ["xgm2-half"] = Xgm2Half,
    };

    /// <summary>XGM: frames between the start probe's plays; 13 frames move each one 60 YM3438 samples against the driver's 256-beat blocks.</summary>
    public const int XgmStartSpacing = 13;

    /// <summary>XGM2: frames between the start probe's plays.</summary>
    public const int Xgm2StartSpacing = 7;

    /// <summary>The frame the start probes' first play is in.</summary>
    public const int FirstPlayFrame = 3;

    /// <summary>A start probe's sample <paramref name="id"/>'s step: odd, so its bytes never repeat within 256.</summary>
    public static int StartStep(int id) => (2 * id) + 1;

    private static PcmDriverScript XgmStarts() =>
        Starts(
            new PcmDriverScript(
                "xgm-starts",
                "XGM: 24 short sounds, one every 13 frames on channels 1, 2, 3, 0, ..., at priority 12; each frame lands\n" +
                "60 YM3438 samples further into the driver's 256-beat mixing blocks.",
                xgm2: false),
            24,
            XgmStartSpacing,
            12);

    private static PcmDriverScript Xgm2Starts() =>
        Starts(
            new PcmDriverScript("xgm2-starts", "XGM2: 12 short sounds, one every 7 frames on channels 1, 2, 0, ..., at priority 15.", xgm2: true),
            12,
            Xgm2StartSpacing,
            15);

    private static PcmDriverScript Starts(PcmDriverScript script, int count, int spacing, int priority)
    {
        for (int id = 1; id <= count; id++)
        {
            script.Sample(300, StartStep(id));
        }

        script.Frame(FirstPlayFrame);
        int channels = script.Xgm2 ? 3 : 4;
        for (int id = 1; id <= count; id++)
        {
            script.Play(id % channels, id, priority).Frame(spacing);
        }

        return script;
    }

    private static PcmDriverScript XgmMix() =>
        new PcmDriverScript(
                "xgm-mix",
                "XGM: four loud sounds started in the same frame, so the mix sticks at +127 / -128; then a play and a stop\n" +
                "at a lower priority than the playing sound's (both ignored) and a stop at a higher one.",
                xgm2: false)
            .Sample(2000, 3)
            .Sample(1500, 5)
            .Sample(1800, 7)
            .Sample(1200, 11)
            .Sample(600, 13)
            .Frame(2)
            .Play(0, 1, 8).Play(1, 2, 8).Play(2, 3, 8).Play(3, 4, 8)
            .Frame(4)
            .Play(0, 5, 4)
            .Stop(1, 4)
            .Frame(2)
            .Stop(2, 12)
            .Frame(6);

    private static PcmDriverScript Xgm2Mix() =>
        new PcmDriverScript(
                "xgm2-mix",
                "XGM2: three loud sounds started 2 frames apart, one at half speed, so the mix sticks at\n" +
                "+127 / -128; then a play at priority 7 on a channel playing at 15 (ignored).",
                xgm2: true)
            .Sample(2000, 3)
            .Sample(1000, 5)
            .Sample(1800, 7)
            .Sample(600, 13)
            .Frame(2)
            .Play(0, 1, 15)
            .Frame(2)
            .Play(1, 2, 15, halfSpeed: true)
            .Frame(2)
            .Play(2, 3, 15)
            .Frame(2)
            .Play(0, 4, 7)
            .Frame(8);

    private static PcmDriverScript Xgm2Half() =>
        new PcmDriverScript("xgm2-half", "XGM2: one sound at half speed, alone.", xgm2: true)
            .Sample(1000, 5)
            .Frame(2)
            .Play(1, 1, 15, halfSpeed: true)
            .Frame(8);

    private static PcmDriverScript Xgm2Stop() =>
        new PcmDriverScript("xgm2-stop", "XGM2: a long sound stopped 5 frames after it was played.", xgm2: true)
            .Sample(4000, 3)
            .Frame(2)
            .Play(0, 1, 15)
            .Frame(5)
            .Stop(0, 15)
            .Frame(6);
}
