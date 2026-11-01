using Mod2Xgm.Analysis;
using Mod2Xgm.Mod;
using Mod2Xgm.Model;
using Serilog.Core;

namespace Mod2Xgm.Tests.Optimizer;

/// <summary>Songs for the PCM candidates' tests: a MOD read, played and analyzed as the conversion does.</summary>
internal static class PcmSongs
{
    /// <summary>Default speed 6 at 125 BPM: a tick is 0.02 s.</summary>
    public const double TickSeconds = 0.02;

    /// <summary><paramref name="mod"/> read, played on a PAL Amiga and analyzed: what the conversion hands the harness.</summary>
    public static SongAnalysis Analyze(byte[] mod)
    {
        ConversionNotes notes = new(Logger.None);
        ModModule module = ModReader.Read(mod, notes);
        Song song = ModToSong.Convert(module, AmigaPitch.PalClockHz, notes);
        Playback playback = ModTickPlayer.Play(module, AmigaPitch.PalClockHz, 1.0, notes);
        return new SongAnalysis(song, playback, SampleAnalyzer.AnalyzeSong(song, playback, new HashSet<int>()), SongVoices.Count(song, playback));
    }
}
