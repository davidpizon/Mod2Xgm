using Mod2Xgm.Dmf;
using Mod2Xgm.Mapping;
using Mod2Xgm.Tests.Mod;

namespace Mod2Xgm.Tests.Dmf;

/// <summary>
/// Tests for <see cref="DmfSongBuilder"/> and <see cref="DmfPatternTranslator"/> (plan T5.5, ADR 0044): each MOD effect's
/// DMF effect and value, the song grid (notes, octaves, volumes, instruments, pans), DAC sample copies, and the notes for
/// what DMF can't do. Every song is a MOD built in code and converted by the real pipeline (<see cref="DmfConversion"/>).
/// </summary>
public sealed class DmfSongBuilderTests
{
    /// <summary>ProTracker periods (finetune 0).</summary>
    private const int C2 = 428;

    private const int E2 = 339;
    private const int C3 = 214;

    /// <summary>Sample numbers in <see cref="Song"/>.</summary>
    private const int Lead = 1;

    private const int Drum = 2;
    private const int Hat = 3;

    /// <summary>The lead: a 32-frame sine loop, so it sounds at 8287 / 32 = 259 Hz at C-2 (C-4, 17 cents flat).</summary>
    private const int LeadCycle = 32;

    /// <summary>
    /// The lead's level against the Sine patch that plays it (ADR 0050): a sine at 100/128 of full scale, played by a
    /// full-scale sine, so its FM volume at MOD volume 64 is this much below 7F.
    /// </summary>
    private const double LeadLevel = 100 / 128.0;

    private static readonly GenesisChannel Fm1 = GenesisChannel.Fm1;
    private static readonly GenesisChannel Fm2 = GenesisChannel.Fm2;
    private static readonly GenesisChannel Dac = GenesisChannel.Fm6;
    private static readonly GenesisChannel Psg1 = GenesisChannel.Psg1;
    private static readonly GenesisChannel Noise = GenesisChannel.Psg4;

    /// <summary>A song with a looped lead (FM) at finetune <paramref name="leadFineTune"/>, a one-shot drum (DAC) and a noise hit, plus the given cells.</summary>
    private static ModBuilder Song(int leadFineTune = 0) =>
        new ModBuilder()
            .WithSample(Lead, "lead", LeadCycle, loopStart: 0, loopLength: LeadCycle, fineTune: leadFineTune, data: i => (sbyte)Math.Round(100 * Math.Sin(2 * Math.PI * i / LeadCycle)))
            .WithSample(Drum, "drum", 4000, data: i => (sbyte)Math.Round(100 * Math.Sin(2 * Math.PI * i / 40) * Math.Exp(-i / 800.0)))
            .WithSample(Hat, "hat", HatData.Length, data: i => HatData[i]);

    /// <summary>White noise from a fixed seed, so the hat is the same on every run.</summary>
    private static readonly sbyte[] HatData = [.. new Random(7).GetItems<sbyte>([-100, -50, 50, 100], 4000)];

    private static DmfConversion.Result Convert(ModBuilder mod, MappingOptions? options = null) => DmfConversion.Convert(mod.Build(), options);

    /// <summary>A lead note on channel 1 with <paramref name="effect"/> on row 0 and nothing on row 1.</summary>
    private static DmfConversion.Result LeadWith(int effect, int parameter, int period = C2) =>
        Convert(Song().WithCell(0, 0, 0, Lead, period).WithCell(0, 1, 0, 0, 0, effect, parameter));

    /// <summary>A looped sample is an FM note at the pitch measured from it: C-2 of a 32-frame loop sounds C-4, stored as note 12 of octave 3, with its 17 cents in E5xx.</summary>
    [Fact]
    public void FmNotesArePlacedAtTheMeasuredPitch()
    {
        DmfConversion.Result result = Convert(Song().WithCell(0, 0, 0, Lead, C2));

        DmfCell cell = result.Cell(Fm1, 0);
        Assert.Equal((12, 3), (cell.Note, cell.Octave));
        Assert.Equal(0, cell.Instrument);
        Assert.Equal(DmfUnits.FmVolume(LeadLevel), cell.Volume);
        int fine = result.Effects(Fm1, 0).Single(e => e.Code == 0xE5).Value;
        Assert.InRange(fine, 0x80 - 24, 0x80 - 20);
        Assert.Contains(result.Notes, n => n.Contains("\"lead\": its FM notes are placed at the pitch measured", StringComparison.Ordinal));
    }

    /// <summary>
    /// A finetuned FM note sounds where the tick player plays it (ADR 0061): at +1, C-2 plays period 425, 12.2 cents up
    /// from 428; at −8 it plays 453.5, 100.2 cents down, so the note below with almost the same E5xx.
    /// </summary>
    [Fact]
    public void FinetunedFmNotesArePlacedAtTheTickPlayersPitch()
    {
        DmfConversion.Result plain = Convert(Song().WithCell(0, 0, 0, Lead, C2));
        DmfConversion.Result up = Convert(Song(leadFineTune: 1).WithCell(0, 0, 0, Lead, C2));
        DmfConversion.Result down = Convert(Song(leadFineTune: -8).WithCell(0, 0, 0, Lead, C2));

        int plainFine = FinePitch(plain);
        Assert.Equal((12, 3), (up.Cell(Fm1, 0).Note, up.Cell(Fm1, 0).Octave));
        Assert.InRange(FinePitch(up) - plainFine, 15, 16);
        Assert.Equal((11, 3), (down.Cell(Fm1, 0).Note, down.Cell(Fm1, 0).Octave));
        Assert.InRange(FinePitch(down) - plainFine, -1, 0);
    }

    /// <summary>An E5x on the note's row tunes the note once: E51 at finetune 0 sounds the same as finetune +1.</summary>
    [Fact]
    public void E5xOnTheNotesRowTunesItOnce()
    {
        DmfConversion.Result written = Convert(Song().WithCell(0, 0, 0, Lead, C2, 0xE, 0x51));
        DmfConversion.Result sample = Convert(Song(leadFineTune: 1).WithCell(0, 0, 0, Lead, C2));

        Assert.Equal(FinePitch(sample), FinePitch(written));
        Assert.Equal(sample.Cell(Fm1, 0).Note, written.Cell(Fm1, 0).Note);
        Assert.DoesNotContain(written.Notes, n => n.Contains("finetune (E5x)", StringComparison.Ordinal));
    }

    /// <summary>The E5xx value of the FM note on channel 1, row 0.</summary>
    private static int FinePitch(DmfConversion.Result result) => result.Effects(Fm1, 0).Single(e => e.Code == 0xE5).Value;

    /// <summary>The same sample an octave up (C-3) is an FM note an octave up.</summary>
    [Fact]
    public void AnOctaveUpInTheModIsAnOctaveUpOnFm()
    {
        DmfConversion.Result result = Convert(Song().WithCell(0, 0, 0, Lead, C3));

        DmfCell cell = result.Cell(Fm1, 0);
        Assert.Equal((12, 4), (cell.Note, cell.Octave));
    }

    /// <summary>Amiga channels 1 and 4 are left, 2 and 3 right: FM notes get <c>0810</c> or <c>0801</c>, once per change.</summary>
    [Fact]
    public void FmNotesArePannedLikeTheAmiga()
    {
        DmfConversion.Result result = Convert(Song()
            .WithCell(0, 0, 0, Lead, C2)
            .WithCell(0, 0, 1, Lead, C2)
            .WithCell(0, 4, 0, Lead, E2));

        Assert.Contains((0x08, 0x10), result.Effects(Fm1, 0));
        Assert.Contains((0x08, 0x01), result.Effects(Fm2, 0));
        Assert.DoesNotContain(result.Effects(Fm1, 4), e => e.Code == 0x08);
    }

    /// <summary>A MOD volume becomes the FM volume that sounds as loud: half volume is 6 dB below the lead at full volume, 8 steps of 0.75 dB.</summary>
    [Fact]
    public void VolumesAreConvertedToDecibelSteps()
    {
        DmfConversion.Result result = Convert(Song()
            .WithCell(0, 0, 0, Lead, C2, 0xC, 32)
            .WithCell(0, 2, 0, 0, 0, 0xC, 16));

        Assert.Equal(DmfUnits.FmVolume(LeadLevel) - 8, result.Cell(Fm1, 0).Volume);
        Assert.Equal(DmfUnits.FmVolume(LeadLevel) - 16, result.Cell(Fm1, 2).Volume);
    }

    /// <summary>Arpeggio <c>0xy</c> is <c>00xy</c>, stopped with <c>0000</c> on the next row because DMF keeps it running.</summary>
    [Fact]
    public void ArpeggioIsCopiedAndStopped()
    {
        DmfConversion.Result result = Convert(Song().WithCell(0, 0, 0, Lead, C2, 0x0, 0x47));

        Assert.Contains((0x00, 0x47), result.Effects(Fm1, 0));
        Assert.Contains((0x00, 0x00), result.Effects(Fm1, 1));
    }

    /// <summary>Slide up <c>1xx</c> becomes <c>01xx</c> in F-number steps covering the same pitch change per row, then <c>0100</c>.</summary>
    [Fact]
    public void PitchSlideUpIsConvertedToFrequencySteps()
    {
        DmfConversion.Result result = Convert(Song().WithCell(0, 0, 0, Lead, C2, 0x1, 4));

        (int code, int value) = result.Effects(Fm1, 0).Single(e => e.Code is 0x01 or 0x02);
        Assert.Equal(0x01, code);

        // 4 periods a tick for 5 ticks at period 428 is 4.7 % up; over 6 DMF ticks from the written C's F-number (644),
        // not the heard pitch's (17 cents flat of it, in the octave below).
        double expected = DmfUnits.FnumDistance(48, 12 * Math.Log2(C2 / (C2 - 20.0))) / 6;
        Assert.InRange(value, (int)expected - 1, (int)expected + 1);
        Assert.Contains((0x01, 0x00), result.Effects(Fm1, 1));
    }

    /// <summary>Slide down <c>2xx</c> becomes <c>02xx</c>, stopped with <c>0100</c> (one speed for both directions).</summary>
    [Fact]
    public void PitchSlideDownIsConverted()
    {
        DmfConversion.Result result = Convert(Song().WithCell(0, 0, 0, Lead, C2, 0x2, 4));

        Assert.Contains(result.Effects(Fm1, 0), e => e is { Code: 0x02, Value: > 0 });
        Assert.Contains((0x01, 0x00), result.Effects(Fm1, 1));
    }

    /// <summary>Fine slide <c>E1x</c> is one step at the row's start: a one-row <c>01xx</c> covering it.</summary>
    [Fact]
    public void FineSlideIsOneRowOfSlide()
    {
        DmfConversion.Result result = Convert(Song().WithCell(0, 0, 0, Lead, C2, 0xE, 0x18));

        Assert.Contains(result.Effects(Fm1, 0), e => e is { Code: 0x01, Value: > 0 });
        Assert.Contains((0x01, 0x00), result.Effects(Fm1, 1));
    }

    /// <summary>
    /// Tone portamento <c>3xx</c> with a note is <c>03xx</c> on that note, without an instrument (which would restart it);
    /// rows that continue it (<c>300</c>) write nothing, because DefleMask freezes a glide written again without a note.
    /// </summary>
    [Fact]
    public void TonePortamentoGlidesToTheNote()
    {
        DmfConversion.Result result = Convert(Song()
            .WithCell(0, 0, 0, Lead, C2)
            .WithCell(0, 1, 0, Lead, E2, 0x3, 8)
            .WithCell(0, 2, 0, 0, 0, 0x3));

        DmfCell glide = result.Cell(Fm1, 1);
        Assert.Equal(-1, glide.Instrument);
        Assert.Contains(result.Effects(Fm1, 1), e => e is { Code: 0x03, Value: > 0 });
        Assert.DoesNotContain(result.Effects(Fm1, 2), e => e.Code == 0x03);
        Assert.Contains((0x03, 0x00), result.Effects(Fm1, 3));
    }

    /// <summary>
    /// Vibrato <c>4xy</c>: both are 64 steps a cycle, but ProTracker skips a row's first tick, so speed 4 at 6 ticks a row
    /// is 4 × 5 / 6 ≈ 3 in DefleMask; the depth is converted to DefleMask's 1/16 semitone.
    /// </summary>
    [Fact]
    public void VibratoKeepsSpeedAndConvertsDepth()
    {
        DmfConversion.Result result = LeadWith(0x4, 0x48);

        (int _, int value) = result.Effects(Fm1, 1).Single(e => e.Code == 0x04);
        Assert.Equal(3, value >> 4);

        // Depth 8: 255 × 8 / 128 ≈ 16 periods at 428 is 0.66 semitone, about 11 DMF steps.
        Assert.InRange(value & 0xF, 10, 12);
        Assert.Contains((0x04, 0x00), result.Effects(Fm1, 2));
    }

    /// <summary>Tremolo <c>7xy</c>: DefleMask's cycle is 128 steps and moves on every tick, so speed 4 is 4 × 2 × 5 / 6 ≈ 7; the depth is the swing in volume steps.</summary>
    [Fact]
    public void TremoloIsConverted()
    {
        DmfConversion.Result result = LeadWith(0x7, 0x44);

        (int _, int value) = result.Effects(Fm1, 1).Single(e => e.Code == 0x07);
        Assert.Equal(7, value >> 4);
        Assert.InRange(value & 0xF, 1, 15);
        Assert.Contains((0x07, 0x00), result.Effects(Fm1, 2));
    }

    /// <summary>Volume slide <c>Axy</c> becomes <c>0Axy</c> reaching the MOD's row-end volume in decibel steps; a later row restores the exact volume.</summary>
    [Fact]
    public void VolumeSlideReachesTheSameLevel()
    {
        DmfConversion.Result result = LeadWith(0xA, 0x04);

        (int _, int value) = result.Effects(Fm1, 1).Single(e => e.Code == 0x0A);
        Assert.Equal(0, value >> 4);
        Assert.InRange(value & 0xF, 1, 15);
        Assert.Contains((0x0A, 0x00), result.Effects(Fm1, 2));
    }

    /// <summary>A fine volume slide (<c>EAx</c>/<c>EBx</c>) is one step: it sets the volume column instead.</summary>
    [Fact]
    public void FineVolumeSlideSetsTheVolume()
    {
        DmfConversion.Result result = Convert(Song()
            .WithCell(0, 0, 0, Lead, C2, 0xC, 32)
            .WithCell(0, 1, 0, 0, 0, 0xE, 0xB8));

        Assert.Equal(DmfUnits.FmVolume(LeadLevel * 24 / 64.0), result.Cell(Fm1, 1).Volume);
    }

    /// <summary>Retrigger <c>E9x</c>, note cut <c>ECx</c> and note delay <c>EDx</c> keep their tick counts (the DMF ticks at the MOD's rate).</summary>
    [Fact]
    public void TickEffectsKeepTheirTicks()
    {
        DmfConversion.Result result = Convert(Song()
            .WithCell(0, 0, 0, Lead, C2, 0xE, 0x93)
            .WithCell(0, 1, 0, 0, 0, 0xE, 0xC2)
            .WithCell(0, 2, 0, Lead, E2, 0xE, 0xD4));

        Assert.Contains((0x0C, 3), result.Effects(Fm1, 0));
        Assert.Contains((0xEC, 2), result.Effects(Fm1, 1));
        Assert.Contains((0xED, 4), result.Effects(Fm1, 2));
    }

    /// <summary>Position jump <c>Bxx</c> and pattern break <c>Dxx</c> keep their targets; they go to PSG1's free effect columns.</summary>
    [Fact]
    public void JumpsAndBreaksAreCopied()
    {
        DmfConversion.Result result = Convert(Song()
            .WithOrders(0, 1, 2)
            .WithCell(0, 0, 0, Lead, C2)
            .WithCell(0, 10, 0, 0, 0, 0xD, 0x16)
            .WithCell(1, 20, 0, 0, 0, 0xB, 0x02));

        Assert.Contains((0x0D, 16), result.Effects(Psg1, 10, order: 0));
        Assert.Contains((0x0B, 2), result.Effects(Psg1, 20, order: 1));
    }

    /// <summary>
    /// What DMF can't jump to is played as near as it can, with a note: <c>Bxx</c>+<c>Dxx</c> starts at row 0; <c>Dxx</c> on the
    /// last order (ignored by DefleMask) jumps to the loop; <c>F00</c> jumps to the loop (DefleMask always loops).
    /// </summary>
    [Fact]
    public void UnwritableJumpsAreApproximatedWithNotes()
    {
        DmfConversion.Result result = Convert(Song()
            .WithOrders(0, 1, 2)
            .WithCell(0, 0, 0, Lead, C2)
            .WithCell(0, 5, 0, 0, 0, 0xB, 0x01)
            .WithCell(0, 5, 1, 0, 0, 0xD, 0x08)
            .WithCell(1, 6, 0, 0, 0, 0xF)
            .WithCell(2, 7, 0, 0, 0, 0xD, 0x04));

        Assert.Contains((0x0B, 1), result.RowEffects(5, order: 0));
        Assert.DoesNotContain(result.RowEffects(5, order: 0), e => e.Code == 0x0D);
        Assert.Contains((0x0B, 0), result.RowEffects(6, order: 1));
        Assert.Contains((0x0B, 0), result.RowEffects(7, order: 2));
        Assert.Contains(result.Notes, n => n.Contains("Bxx with Dxx", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("song stops (F00)", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("on the last order", StringComparison.Ordinal));
    }

    /// <summary>Pattern delay <c>EEx</c> stretches its row with both tick times, and the next row restores them; a pattern loop gets a note.</summary>
    [Fact]
    public void PatternDelayStretchesTheRow()
    {
        DmfConversion.Result result = Convert(Song()
            .WithCell(0, 0, 0, Lead, C2)
            .WithCell(0, 2, 0, 0, 0, 0xE, 0xE2)
            .WithCell(0, 4, 0, 0, 0, 0xE, 0x60)
            .WithCell(0, 5, 0, 0, 0, 0xE, 0x61));

        Assert.Contains((0x09, 18), result.RowEffects(2));
        Assert.Contains((0x0F, 18), result.RowEffects(2));
        Assert.Contains((0x09, 6), result.RowEffects(3));
        Assert.Contains(result.Notes, n => n.Contains("pattern loops (E6x)", StringComparison.Ordinal));
    }

    /// <summary>A loop to an order other than 0 is a <c>0Bxx</c> on the last row; DefleMask would otherwise go back to order 0.</summary>
    [Fact]
    public void TheSongLoopsToItsRestartOrder()
    {
        byte[] mod = Song()
            .WithOrders(0, 1)
            .WithCell(0, 0, 0, Lead, C2)
            .Build();

        DmfConversion.Result result = DmfConversion.Convert(mod, transform: plan => plan with { LoopOrder = 1 });

        Assert.Contains((0x0B, 1), result.RowEffects(63, order: 1));
    }

    /// <summary>A drum on the DAC: <c>1701</c> once, the note picks its sample, and the DAC is panned like its MOD channel.</summary>
    [Fact]
    public void DrumsPlayOnTheDac()
    {
        DmfConversion.Result result = Convert(Song()
            .WithCell(0, 0, 1, Drum, C2)
            .WithCell(0, 4, 1, Drum, C2));

        Assert.Contains((0x17, 0x01), result.Effects(Dac, 0));
        Assert.Contains((0x08, 0x01), result.Effects(Dac, 0));
        Assert.Empty(result.Effects(Dac, 4));
        Assert.Equal(12, result.Cell(Dac, 0).Note);
        Assert.Single(result.File.Samples);
        Assert.Equal(-1, result.Cell(Dac, 0).Volume);
    }

    /// <summary>A quieter drum hit plays a quieter copy of the sample (the DAC has no volume), in 3 dB steps.</summary>
    [Fact]
    public void QuietDrumsPlayQuieterCopies()
    {
        DmfConversion.Result result = Convert(Song()
            .WithCell(0, 0, 0, Drum, C2)
            .WithCell(0, 4, 0, Drum, C2, 0xC, 32)
            .WithCell(0, 8, 0, Drum, C2, 0xC, 30));

        Assert.Equal(2, result.File.Samples.Count);
        Assert.Equal(12, result.Cell(Dac, 0).Note);
        Assert.Equal(1, result.Cell(Dac, 4).Note);
        Assert.Equal(1, result.Cell(Dac, 8).Note);
        double loud = result.File.Samples[0].Data.Max(v => Math.Abs((int)v));
        double quiet = result.File.Samples[1].Data.Max(v => Math.Abs((int)v));
        Assert.InRange(quiet / loud, 0.45, 0.55);
        Assert.Contains(result.Notes, n => n.Contains("DAC sample copies", StringComparison.Ordinal));
    }

    /// <summary>
    /// A DAC sample that clips when resampled is named by its number as well as its name, so the note still says which
    /// sample it is when the name is empty (many real MODs leave it empty; found in the T5.7 corpus check).
    /// </summary>
    [Fact]
    public void ClippingNoteNamesTheSampleNumber()
    {
        DmfConversion.Result result = Convert(new ModBuilder()
            .WithSample(Drum, string.Empty, 4000, data: i => (sbyte)(i % 20 < 10 ? 127 : -128))
            .WithCell(0, 0, 0, Drum, C2));

        Assert.Contains(result.Notes, n => n.StartsWith($"Sample {Drum} \"\": ", StringComparison.Ordinal) && n.Contains("clipped converting to 16-bit PCM", StringComparison.Ordinal));
    }

    /// <summary>Sample offset <c>9xx</c> on the DAC plays a copy cut at that point.</summary>
    [Fact]
    public void SampleOffsetPlaysACutCopy()
    {
        DmfConversion.Result result = Convert(Song()
            .WithCell(0, 0, 0, Drum, C2)
            .WithCell(0, 4, 0, Drum, C2, 0x9, 0x08));

        Assert.Equal(2, result.File.Samples.Count);
        Assert.True(result.File.Samples[1].Data.Count < result.File.Samples[0].Data.Count);
        Assert.Contains("+2048", result.File.Samples[1].Name, StringComparison.Ordinal);
    }

    /// <summary>More than 12 DAC copies need bank changes (<c>EBxx</c>): copy 12 is note C of bank 1.</summary>
    [Fact]
    public void ThirteenthCopyIsInBankOne()
    {
        ModBuilder mod = Song();
        for (int i = 0; i < 13; i++)
        {
            mod.WithCell(0, i * 2, 0, Drum, C2, 0x9, i + 1);
        }

        DmfConversion.Result result = Convert(mod);

        Assert.Equal(13, result.File.Samples.Count);
        Assert.Contains((0xEB, 1), result.Effects(Dac, 24));
        Assert.Equal(12, result.Cell(Dac, 24).Note);
    }

    /// <summary>
    /// An FM voice plays its sample as loud as the MOD does (ADR 0050): a lead a quarter as loud (12 dB) is 16 steps
    /// quieter than the lead, both at MOD volume 64.
    /// </summary>
    [Fact]
    public void FmVolumeFollowsTheSampleLevel()
    {
        const int quiet = 4;
        DmfConversion.Result result = Convert(Song()
            .WithSample(quiet, "quiet lead", LeadCycle, loopStart: 0, loopLength: LeadCycle, data: i => (sbyte)Math.Round(25 * Math.Sin(2 * Math.PI * i / LeadCycle)))
            .WithCell(0, 0, 0, Lead, C2)
            .WithCell(0, 0, 1, quiet, C2));

        int loud = result.Cell(Fm1, 0).Volume;
        int soft = result.Cell(Fm2, 0).Volume;
        Assert.Equal(DmfUnits.FmVolume(LeadLevel), loud);
        Assert.Equal(loud - 16, soft);
    }

    /// <summary>
    /// A sample louder than its patch can play plays at the patch's loudest at full MOD volume, and softer notes in
    /// proportion, with a conversion note: a full-scale square loop is louder than any sine.
    /// </summary>
    [Fact]
    public void ASampleLouderThanItsPatchIsNoted()
    {
        const int square = 4;
        DmfConversion.Result result = Convert(Song()
            .WithSample(square, "square", LeadCycle, loopStart: 0, loopLength: LeadCycle, data: i => (sbyte)(i < LeadCycle / 2 ? 127 : -128))
            .WithCell(0, 0, 0, square, C2)
            .WithCell(0, 1, 0, square, C2, 0xC, 32));

        int full = result.Cell(Fm1, 0).Volume;
        Assert.InRange(full, DmfUnits.MaxFmVolume - 8, DmfUnits.MaxFmVolume);
        Assert.Equal(full - 8, result.Cell(Fm1, 1).Volume);
        Assert.Contains(result.Notes, n => n.Contains("FM notes play quieter than in the MOD", StringComparison.Ordinal));
    }

    /// <summary>
    /// With <c>--no-dac</c> a noise hit plays on PSG4 with fixed-rate white noise and the noise instrument, at full volume:
    /// the hat is louder than the PSG's noise can play, which is noted.
    /// </summary>
    [Fact]
    public void NoisePlaysOnThePsgNoiseChannel()
    {
        DmfConversion.Result result = Convert(Song().WithCell(0, 0, 2, Hat, C2), MappingOptions.Default with { UseDac = false });

        DmfCell cell = result.Cell(Noise, 0);
        Assert.Contains((0x20, 0x01), result.Effects(Noise, 0));
        Assert.Equal(DmfUnits.MaxPsgVolume, cell.Volume);
        Assert.NotNull(result.File.Instruments[cell.Instrument].Std);
        Assert.Contains(result.Notes, n => n.Contains("noise notes play quieter than in the MOD", StringComparison.Ordinal));
        Assert.InRange(cell.Note, 1, 12);
    }

    /// <summary>A quiet noise hit plays the PSG noise quieter, as loud as the MOD plays it (ADR 0050).</summary>
    [Fact]
    public void QuietNoiseIsQuieterOnThePsg()
    {
        const int soft = 4;
        DmfConversion.Result result = Convert(
            Song().WithSample(soft, "soft hat", HatData.Length, data: i => (sbyte)(HatData[i] / 8)).WithCell(0, 0, 2, soft, C2),
            MappingOptions.Default with { UseDac = false });

        double level = VoiceLevel.Loudest(result.Song.Samples[soft - 1]);
        Assert.Equal(DmfUnits.PsgVolume(level / VoiceLevel.PsgNoise), result.Cell(Noise, 0).Volume);
        Assert.True(result.Cell(Noise, 0).Volume < DmfUnits.MaxPsgVolume);
    }

    /// <summary>
    /// A noise instrument's volume macro follows its sample's loudness tick by tick at the chip's nearest levels, whose
    /// steps grow towards the quiet end (docs/adr/0067-psg-volume-levels.md): a hit that fades by about 6 dB a tick goes
    /// F, C, 9, 7, 4 (where even 2 dB steps would give 3) and ends at 0; a looped sample holds its level instead.
    /// </summary>
    [Fact]
    public void NoiseInstrumentsFadeLikeTheirSample()
    {
        const int framesPerTick = 100;
        float[] fading = [.. Enumerable.Range(0, 5 * framesPerTick).Select(i => (float)(HatData[i] / 128.0 * Math.Pow(0.5, Math.DivRem(i, framesPerTick).Quotient)))];

        DmfInstrument fade = DmfSongBuilder.NoiseInstrument(new Mod2Xgm.Model.Sample("fade", fading, null), framesPerTick * 50, 50);
        DmfInstrument held = DmfSongBuilder.NoiseInstrument(new Mod2Xgm.Model.Sample("held", fading, new Mod2Xgm.Model.SampleLoop(0, framesPerTick)), framesPerTick * 50, 50);

        Assert.Equal([15, 12, 9, 7, 4, 0], fade.Std!.Volume.Values);
        Assert.Equal([15], held.Std!.Volume.Values);
    }

    /// <summary>A speed change (<c>Fxx</c>) is a tick-time pair on its row (plan T5.4), placed in a free column.</summary>
    [Fact]
    public void SpeedChangesAreTickTimes()
    {
        DmfConversion.Result result = Convert(Song()
            .WithCell(0, 0, 0, Lead, C2)
            .WithCell(0, 8, 0, 0, 0, 0xF, 3));

        Assert.Contains((0x09, 3), result.RowEffects(8));
        Assert.Contains((0x0F, 3), result.RowEffects(8));
    }

    /// <summary>Effects DMF has no equivalent for (here the Amiga filter <c>E0x</c>) are counted in a note.</summary>
    [Fact]
    public void UntranslatableEffectsGetANote()
    {
        DmfConversion.Result result = LeadWith(0xE, 0x01);

        Assert.Contains(result.Notes, n => n.Contains("Amiga filter (E0x)", StringComparison.Ordinal));
    }

    /// <summary>Effects on a DAC note that the DAC can't play (pitch slides) are left out with a note saying why.</summary>
    [Fact]
    public void PitchEffectsOnTheDacGetANote()
    {
        DmfConversion.Result result = Convert(Song()
            .WithCell(0, 0, 0, Drum, C2)
            .WithCell(0, 1, 0, 0, 0, 0x1, 4));

        Assert.DoesNotContain(result.Effects(Dac, 1), e => e.Code == 0x01);
        Assert.Contains(result.Notes, n => n.Contains("pitch slide", StringComparison.Ordinal) && n.Contains("DAC notes", StringComparison.Ordinal));
    }

    /// <summary>Every pattern is one DMF pattern per channel, numbered by its matrix row; effect columns fit what each channel needs.</summary>
    [Fact]
    public void TheGridHasOnePatternPerOrder()
    {
        DmfConversion.Result result = Convert(Song()
            .WithOrders(0, 1, 0)
            .WithCell(0, 0, 0, Lead, C2, 0x4, 0x48));

        Assert.Equal(3, result.File.MatrixRows);
        Assert.All(result.File.Channels, c => Assert.Equal([0, 1, 2], c.Orders));
        Assert.Equal(64, result.File.RowsPerPattern);
        Assert.All(result.File.Channels, c => Assert.InRange(c.EffectColumns, 1, 4));
    }

    /// <summary>Rate codes: the lowest rate at or above the sample's, never above 32 kHz.</summary>
    [Theory]
    [InlineData(3000, 0)]
    [InlineData(8287, 2)]
    [InlineData(16574, 4)]
    [InlineData(28000, 5)]
    [InlineData(40000, 5)]
    public void RateCodeIsTheLowestLosslessRate(double rate, int code) => Assert.Equal(code, DmfSongBuilder.RateCodeFor(rate));
}
