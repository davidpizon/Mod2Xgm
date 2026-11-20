using System.Globalization;
using System.Text;

namespace Mod2Xgm.Tests.Dmf.Sanity;

/// <summary>Writes the DMF sanity check's results as Markdown (docs/testing.md#dmf-sanity-check).</summary>
internal static class SanityReport
{
    /// <summary>The report for <paramref name="songs"/>.</summary>
    public static string Write(IReadOnlyList<SongResult> songs, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(songs);
        StringBuilder md = new();
        Line(md, "# DMF → XGM2 sanity check");
        Line(md);
        Line(md, $"Run {when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}. Every difference below is a **potential** defect, not a confirmed one.");
        Line(md);
        Line(md, "Each song goes two ways to an XGM2 file, which is decoded back into chip writes and compared with DefleMask's own VGM export of the song:");
        Line(md);
        Line(md, "- **DMF → player → XGM2**: the DMF played by the test's DefleMask model (`DmfPlayer`), then the project's `Xgm2Writer`.");
        Line(md, "- **VGM → XGM2 (control)**: DefleMask's VGM writes straight into the writer. This leaves the model out, so what differs here is the writer's (or SGDK's driver's) doing.");
        Line(md, "- **Player alone**: the DMF player's log against the VGM, with no writer: how close the model is.");
        Line(md);
        Line(md, "Checks: **writes** = what each frame changes (registers' new values; key changes, noise writes and PCM plays in order); **state** = the chips' state at the end of each frame; **sound** = both rendered with the project's chip models and scored (log-mel distance in dB, 0 = identical; onset F1). PCM commands in the XGM2 files are compared on the frame they are heard, two frames after they are sent (ADR 0119). Even on the right frame, SGDK's XGM2 driver starts a sample about 10 ms late (the lead is whole frames) and plays it at 13.3 kHz, so a song with samples never scores 0 dB on the control path: expect about 1–2 dB, from the treble above 4 kHz.");
        Line(md);
        Line(md, "## Summary");
        Line(md);
        Line(md, "Frames that differ, out of the song's frames. Sound: whole-song log-mel distance (median 4 s window).");
        Line(md);
        Line(md, "| Song | DMF | Rate | Player alone (state) | DMF→XGM2 writes | DMF→XGM2 state | DMF→XGM2 sound | Control writes | Control state | Control sound |");
        Line(md, "|---|---|---|---|---|---|---|---|---|---|");
        foreach (SongResult s in songs)
        {
            if (s.Error is not null)
            {
                Line(md, $"| {s.Song} | not read | | {Escape(s.Error)} | | | | | | |");
                continue;
            }

            PathResult dmf = s.Paths[0];
            PathResult control = s.Paths[1];
            Line(md, $"| {s.Song} | v{s.Version}{(s.System == Mod2Xgm.Dmf.DmfSystem.GenesisExtendedChannel3 ? " ext. CH3" : string.Empty)} | {s.TickRateHz} Hz | {Share(s.PlayerState!)} | {Share(dmf.Writes)} | {Share(dmf.State)} | {SoundCell(dmf.Sound)} | {Share(control.Writes)} | {Share(control.State)} | {SoundCell(control.Sound)} |");
        }

        foreach (SongResult s in songs.Where(s => s.Error is null))
        {
            Line(md);
            Line(md, $"## {s.Song}");
            Line(md);
            double hardwareSeconds = s.Paths[0].Writes.CandidateFrames / s.XgmFrameRateHz;
            double deflemaskSeconds = s.PlayerState!.FramesCompared / (double)s.TickRateHz;
            Line(md, $"- **Timing**: DefleMask ticks at {s.TickRateHz} Hz; SGDK's XGM2 driver plays a frame per VBlank, {F(s.XgmFrameRateHz)} Hz, so one pass lasts {F(hardwareSeconds)} s on the console against {F(deflemaskSeconds)} s in DefleMask ({Percent((s.XgmFrameRateHz - s.TickRateHz) / s.TickRateHz)} tempo).");
            if (s.Unsupported.Count > 0)
            {
                Line(md, $"- **Not played by the DMF player**: {string.Join(", ", s.Unsupported)}.");
            }

            Comparison(md, "Player alone — writes", s.Player!);
            Comparison(md, "Player alone — state", s.PlayerState);
            foreach (PathResult p in s.Paths)
            {
                Line(md);
                Line(md, $"### {p.Name}");
                Line(md);
                Line(md, $"XGM2 file: {p.FileBytes} bytes.");
                if (p.Notes.Count > 0)
                {
                    Line(md);
                    Line(md, "Writer notes:");
                    foreach (string note in p.Notes)
                    {
                        Line(md, $"- {Escape(note)}");
                    }
                }

                Comparison(md, "Writes", p.Writes);
                Comparison(md, "State", p.State);
                if (p.Sound is { } sound)
                {
                    Line(md);
                    Line(md, $"**Sound**: log-mel {F(sound.Score.MelDistanceDb)} dB, MFCC {F(sound.Score.MfccDistance)}, onset F1 {F(sound.Score.OnsetF1)}, lag {F(sound.Score.LagSeconds * 1000)} ms, length {F(sound.Score.LengthDifferenceSeconds)} s; median {F(sound.WindowSeconds)} s window {F(sound.MedianWindowDb)} dB. Furthest apart: {string.Join(", ", sound.Worst.Select(w => $"{F(w.StartSeconds)} s ({F(w.MelDistanceDb)} dB)"))}.");
                }
            }
        }

        return md.ToString();
    }

    private static void Comparison(StringBuilder md, string title, LogComparison c)
    {
        Line(md);
        string loop = c.ReferenceLoop == c.CandidateLoop ? string.Empty : $" Loop frame: VGM {Show(c.ReferenceLoop)}, here {Show(c.CandidateLoop)}.";
        string length = c.FramesCompared == c.CandidateFrames ? string.Empty : $" Length: VGM {c.FramesCompared} frames, here {c.CandidateFrames}.";
        if (c.FramesDiffering == 0)
        {
            Line(md, $"**{title}**: no frame differs.{length}{loop}");
            return;
        }

        Line(md, $"**{title}**: {Share(c)} frames differ, the first at frame {c.FirstDifference} ({F(c.FirstDifference!.Value / 60.0)} s at 60 Hz).{length}{loop}");
        Line(md);
        Line(md, "| What | Frames | Differences |");
        Line(md, "|---|---|---|");
        foreach (CategoryCount category in c.Categories)
        {
            Line(md, $"| {category.Category} | {category.Frames} | {category.Items} |");
        }

        Line(md);
        Line(md, "<details><summary>Examples (frame: what — VGM vs here)</summary>");
        Line(md);
        foreach (FrameDifference e in c.Examples)
        {
            Line(md, $"- {e.Frame}: `{e.Key}` — {e.Reference} vs {e.Candidate}");
        }

        Line(md);
        Line(md, "</details>");
    }

    private static string Share(LogComparison c) => c.FramesCompared == 0
        ? "0"
        : $"{c.FramesDiffering}/{c.FramesCompared} ({Percent((double)c.FramesDiffering / c.FramesCompared)})";

    private static string SoundCell(SoundComparison? sound) => sound is null ? "—" : $"{F(sound.Score.MelDistanceDb)} dB ({F(sound.MedianWindowDb)})";

    private static string Percent(double share) => share.ToString("+0.0%;-0.0%;0%", CultureInfo.InvariantCulture).TrimStart('+');

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Show(int? frame) => frame?.ToString(CultureInfo.InvariantCulture) ?? "none";

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static void Line(StringBuilder md, string text = "") => md.Append(text).Append('\n');
}
