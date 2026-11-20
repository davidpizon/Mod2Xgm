using Mod2Xgm.Cli;
using Mod2Xgm.Dmf;
using Mod2Xgm.Render;
using Mod2Xgm.Render.Fm;
using Mod2Xgm.Render.Pcm;
using Mod2Xgm.Xgm;

namespace Mod2Xgm.Tests.Dmf.Sanity;

/// <summary>One path from a song to an XGM2 file, and what the checks found against DefleMask's VGM export.</summary>
/// <param name="Name">The path's name in the report.</param>
/// <param name="Notes">The XGM2 writer's conversion notes.</param>
/// <param name="FileBytes">The XGM2 file's size.</param>
/// <param name="Writes">Check 1.</param>
/// <param name="State">Check 2.</param>
/// <param name="Sound">Check 3, or <c>null</c> when it wasn't run.</param>
internal sealed record PathResult(string Name, IReadOnlyList<string> Notes, int FileBytes, LogComparison Writes, LogComparison State, SoundComparison? Sound);

/// <summary>What the sanity check found for one song.</summary>
/// <param name="Song">The file name without extension.</param>
/// <param name="Error">Why the song couldn't be checked, or <c>null</c>.</param>
/// <param name="Version">The DMF file version.</param>
/// <param name="System">Genesis or Genesis with extended CH3.</param>
/// <param name="TickRateHz">DefleMask's tick rate.</param>
/// <param name="XgmFrameRateHz">The rate SGDK's XGM2 driver plays frames at on the console the file is for.</param>
/// <param name="Unsupported">What the DMF player met but doesn't play.</param>
/// <param name="Player">Checks 1 and 2 of the DMF player's own log: how close the test's DefleMask model is.</param>
/// <param name="PlayerState">Check 2 of the DMF player's own log.</param>
/// <param name="Paths">The DMF path (player → writer) and the control path (VGM → writer).</param>
internal sealed record SongResult(
    string Song,
    string? Error,
    int Version,
    DmfSystem? System,
    int TickRateHz,
    double XgmFrameRateHz,
    IReadOnlyList<string> Unsupported,
    LogComparison? Player,
    LogComparison? PlayerState,
    IReadOnlyList<PathResult> Paths);

/// <summary>
/// Runs the DMF sanity check on one DMF and its DefleMask VGM export (docs/testing.md#dmf-sanity-check): the DMF through
/// the test's DefleMask model and the project's XGM2 writer, and — as a control that leaves the model out — the VGM's own
/// writes through the same writer; each XGM2 file is decoded and checked against the VGM three ways.
/// </summary>
internal static class SanityRun
{
    /// <summary>Checks the song in <paramref name="dmfPath"/> against the VGM next to it; <paramref name="sound"/> runs check 3 too (slow).</summary>
    public static SongResult Check(string dmfPath, bool sound)
    {
        ArgumentNullException.ThrowIfNull(dmfPath);
        string song = Path.GetFileNameWithoutExtension(dmfPath);
        DmfFile file;
        try
        {
            file = DmfReader.Read(File.ReadAllBytes(dmfPath), lenient: true);
        }
        catch (InvalidDataException error)
        {
            return new SongResult(song, error.Message, 0, null, 0, 0, [], null, null, []);
        }

        ChipLog vgm = ChipLog.FromVgm(VgmLog.Read(File.ReadAllBytes(Path.ChangeExtension(dmfPath, ".vgm"))));
        DmfPlayback playback = DmfPlayer.Play(file);
        bool pal = file.Timing.TickRateHz < 55;
        double ymClock = pal ? Ym3438.PalClockHz : Ym3438.NtscClockHz;
        double xgmRate = GenesisChips.FrameRateHz(pal ? Region.Pal : Region.Ntsc);
        int lead = Xgm2PcmDriver.LeadFrames(ymClock, xgmRate);
        // The three renders are the slow part, so the reference and both paths run side by side.
        Task<StereoAudio>? reference = sound ? Task.Run(() => SoundCheck.Render(vgm, PcmDriver.Dmf, 1 / vgm.FrameRateHz)) : null;
        PathResult[] paths = [.. new[] { ("DMF → player → XGM2", playback.Log), ("VGM → XGM2 (control)", vgm) }
            .AsParallel()
            .AsOrdered()
            .Select(path =>
            {
                (string name, ChipLog source) = path;
                CollectingSink sink = new();
                ConversionNotes notes = new(sink.CreateLogger());
                GenesisStream stream = source.ToXgm2Stream(pal);
                Xgm2Output output = Xgm2Writer.Write(stream, notes);
                ChipLog decoded = Xgm2Decoder.Decode(output.Bytes, output.Samples);
                ChipLog heard = LogComparer.ShiftPcm(decoded, lead);
                // The renderer's XGM2 driver model takes each PCM command on the frame it is heard and starts it the lead
                // earlier (ADR 0119), so it gets the shifted log: the decoded one, already sent ahead, would be led twice.
                StereoAudio? candidate = reference is null ? null : SoundCheck.Render(heard, PcmDriver.Xgm2, 1 / vgm.FrameRateHz);
                SoundComparison? soundResult = reference is null || candidate is null ? null : SoundCheck.Compare(reference.Result, candidate);
                return new PathResult(name, [.. notes.Messages], output.Bytes.Length, LogComparer.CompareWrites(vgm, heard), LogComparer.CompareState(vgm, heard), soundResult);
            })];

        return new SongResult(
            song,
            null,
            file.Version,
            file.System,
            file.Timing.TickRateHz,
            xgmRate,
            playback.Unsupported,
            LogComparer.CompareWrites(vgm, playback.Log),
            LogComparer.CompareState(vgm, playback.Log),
            paths);
    }

}
