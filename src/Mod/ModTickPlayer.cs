using Mod2Xgm.Model;

namespace Mod2Xgm.Mod;

/// <summary>
/// Plays a MOD tick by tick (plan T3.4) and records every channel's sound on every tick as a format-neutral
/// <see cref="Playback"/>. A port of ProTracker 2.3D's replayer as pt2-clone reproduces it (<c>src/pt2_replayer.c</c>,
/// BSD-3-Clause, THIRD-PARTY-NOTICES.md), quirks included: it reads the raw pattern cells, as ProTracker did, so the
/// song's flow (<c>Bxx</c>, <c>Dxx</c>, <c>E6x</c>, <c>EEx</c>, <c>Fxx</c>), effect memory and every effect's tick
/// behavior match it exactly (David, 2026-10-27).
/// <para>The song plays once through, ending the way pt2-clone's MOD2WAV export decides it: after the tick that would
/// move to a row already played (or past the song's end), which becomes the loop point; or on <c>F00</c>.</para>
/// <para>Pitch follows OpenMPT instead, measured with libopenmpt (docs/adr/0060-openmpt-pitch-and-loops.md): periods are
/// kept in quarters, a finetuned note is its finetune's C-1 octave halved for each octave up, slides stop at the
/// finetune's own top and bottom notes, vibrato uses OpenMPT's finer sine, and an arpeggio reads past B-3 the way OpenMPT
/// does. A looping sample also ends at its loop end the first time through, as in OpenMPT.</para>
/// <para>A song with notes outside every ProTracker finetune row (period below 108 or above 907) was written in a tracker
/// with more octaves, so, as in OpenMPT (docs/adr/0055-notes-beyond-protracker-range.md), those notes play at their own
/// pitch, snapped to the nearest note from C-0 to B-5, and slides run from period 14 to 3424. ProTracker 2 would play
/// silence or a stuck tone there.</para>
/// <para>Under Ultimate Soundtracker rules (<see cref="ModPlayer.UltimateSoundtracker"/>) the cells are first turned into
/// ProTracker commands the way OpenMPT does (1xy and 0xy from 03 up → arpeggio, 2xy → slide up y or else down x, the
/// rest dropped), samples have no finetune and start at their loop start, the speed stays 6 and the tempo comes from
/// byte 471 (docs/formats/mod.md#players).</para>
/// </summary>
internal static class ModTickPlayer
{
    /// <summary>Stop after this many ticks (over 11 hours at 50 Hz); no real song comes close.</summary>
    private const int MaxTicks = 2_000_000;

    /// <summary>Plays <paramref name="module"/> once through.</summary>
    /// <param name="module">The MOD as read; <see cref="ModModule.Player"/> picks the rules.</param>
    /// <param name="amigaClockHz">Paula clock that turns periods into pitch (<c>--input-region</c>).</param>
    /// <param name="tickRateScale">The source Amiga's tick rate relative to PAL: 1 for PAL, 6/5 for NTSC (60 ticks/s at 125 BPM; docs/cli.md#regions).</param>
    /// <param name="notes">Collects conversion notes.</param>
    public static Playback Play(ModModule module, double amigaClockHz, double tickRateScale, ConversionNotes notes)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amigaClockHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickRateScale);
        return new Replayer(module, amigaClockHz, tickRateScale, notes).Run();
    }

    /// <summary>ProTracker's CIA timer: 709 379 Hz, loaded with 1 773 447 / BPM (truncated) and firing on underflow.</summary>
    internal static double CiaTickHz(int bpm)
    {
        // Integer division on purpose: ProTracker truncates the timer value.
        int timerValue = 1_773_447 / bpm;
        return 709_379.0 / (timerValue + 1);
    }

    /// <summary>One run of the replayer; field names follow pt2-clone's so the port can be checked line by line.</summary>
    private sealed class Replayer
    {
        // pt2_replayer.c constants.
        private const int Rows = 64;
        private const int MaxOrders = 128;
        private const int MinBpm = 32;
        private const int MaxBpm = 255;
        private const int MinPeriod = 113;
        private const int TableStride = 37;

        /// <summary>Periods are kept in quarters, as OpenMPT keeps a MOD's, so finetuned notes and their slides keep its fractions (ADR 0060).</summary>
        private const int Quarters = 4;

        /// <summary>C-1, ProTracker's lowest note, counted from C-0 (the index into <see cref="ExtendedPeriods"/>).</summary>
        private const int BottomNote = 12;

        /// <summary>B-3, ProTracker's top note, counted from C-0.</summary>
        private const int TopNote = 47;

        // OpenMPT's limits for a MOD whose notes go beyond ProTracker's range (measured with libopenmpt, ADR 0055).
        private const int ExtendedMinPeriod = 14;
        private const int ExtendedMaxPeriod = 3424;

        /// <summary>A note period below this or above <see cref="HighestTablePeriod"/> is in no ProTracker finetune row (B-3 at +7, C-1 at −8).</summary>
        private const int LowestTablePeriod = 108;

        /// <summary>See <see cref="LowestTablePeriod"/>.</summary>
        private const int HighestTablePeriod = 907;

        /// <summary>OpenMPT's MOD period table, C-0 to B-5: ProTracker's three octaves with one more below and two above.</summary>
        private static readonly short[] ExtendedPeriods =
        [
            1712, 1616, 1524, 1440, 1356, 1280, 1208, 1140, 1076, 1016, 960, 907,
            856, 808, 762, 720, 678, 640, 604, 570, 538, 508, 480, 453,
            428, 404, 381, 360, 339, 320, 302, 285, 269, 254, 240, 226,
            214, 202, 190, 180, 170, 160, 151, 143, 135, 127, 120, 113,
            107, 101, 95, 90, 85, 80, 75, 71, 67, 63, 60, 56,
            53, 50, 47, 45, 42, 40, 37, 35, 33, 31, 30, 28,
        ];

        /// <summary>pt2_tables.c <c>vibratoTable</c>: half a sine, 0–255.</summary>
        private static readonly byte[] VibratoTable =
        [
            0x00, 0x18, 0x31, 0x4A, 0x61, 0x78, 0x8D, 0xA1,
            0xB4, 0xC5, 0xD4, 0xE0, 0xEB, 0xF4, 0xFA, 0xFD,
            0xFF, 0xFD, 0xFA, 0xF4, 0xEB, 0xE0, 0xD4, 0xC5,
            0xB4, 0xA1, 0x8D, 0x78, 0x61, 0x4A, 0x31, 0x18,
        ];

        /// <summary>
        /// OpenMPT's vibrato sine, the first half of its 64 steps (peak 127; the second half is the same, negated): finer than
        /// <see cref="VibratoTable"/>, measured with libopenmpt (ADR 0060).
        /// </summary>
        private static readonly sbyte[] OpenMptSine =
        [
            0, 12, 25, 37, 49, 60, 71, 81, 90, 98, 106, 112, 117, 122, 125, 126,
            127, 126, 125, 122, 117, 112, 106, 98, 90, 81, 71, 60, 49, 37, 25, 12,
        ];

        /// <summary>
        /// pt2_tables.c <c>periodTable</c> as laid out in memory: 16 finetune rows of 36 periods plus a zero, then the 15
        /// words ProTracker keeps right after it, which arpeggio on finetune −1 reads.
        /// </summary>
        private static readonly short[] PeriodTable = BuildPeriodTable();

        private readonly ModModule _module;
        private readonly double _clockHz;
        private readonly double _tickRateScale;
        private readonly ConversionNotes _notes;
        private readonly bool _soundtracker;
        private readonly SampleView[] _samples;
        private readonly Voice[] _voices;
        private readonly double[] _pans;
        /// <summary>MOD2WAV's <c>rowVisitTable</c>: rows played in this pass (E6x clears the rows it repeats).</summary>
        private readonly bool[] _visited = new bool[MaxOrders * Rows];

        /// <summary>The tick each row was first played on (−1: never), for the loop point; never cleared.</summary>
        private readonly int[] _firstTickOfRow = new int[MaxOrders * Rows];

        /// <summary>The pitch of every written note the first time its row plays (<see cref="Playback.NotePitches"/>).</summary>
        private readonly Dictionary<NotePosition, NotePitch> _notePitches = [];

        // Replayer state (pt2_replayer.c globals and module_t).
        private bool _posJumpAssert;
        private bool _pBreakFlag;
        private bool _doStopSong;
        private bool _modRenderDone;
        private sbyte _pBreakPosition;
        private byte _pattDelTime;
        private byte _pattDelTime2;
        private byte _lowMask = 0xFF;
        private short _modPos;
        private int _modPattern;
        private int _ciaSetBpm = -1;
        private int _modBpm;
        private int _tick;
        private int _speed;
        private sbyte _row;
        private ushort _dmaconTemp;
        private bool _ledFilter;
        private int _playOrder;
        private int _playRow;

        // Tempo from Ultimate Soundtracker's byte 471, which can be fractional.
        private readonly double _soundtrackerBpm;

        // For conversion notes.
        private bool _usesInvertLoop;

        /// <summary>Notes beyond ProTracker's range play at their pitch (ADR 0055); the number of such cells, 0 when off.</summary>
        private readonly int _extendedNotes;

        /// <summary>The smallest period a note plays at: 113, or 14 with <see cref="_extendedNotes"/>.</summary>
        private readonly int _minPeriod;

        public Replayer(ModModule module, double clockHz, double tickRateScale, ConversionNotes notes)
        {
            _module = module;
            _clockHz = clockHz;
            _tickRateScale = tickRateScale;
            _notes = notes;
            _soundtracker = module.Player == ModPlayer.UltimateSoundtracker;
            _samples = [.. module.Samples.Select(s => SampleView.Of(s, _soundtracker))];
            _voices = [.. Enumerable.Range(0, module.ChannelCount).Select(i => new Voice(i))];
            _pans = [.. Enumerable.Range(0, module.ChannelCount).Select(c => c % 4 is 0 or 3 ? -1.0 : 1.0)];
            _soundtrackerBpm = ModPlayers.UltimateSoundtrackerBpm(module.RestartPosition) ?? 125;
            Array.Fill(_firstTickOfRow, -1);
            _extendedNotes = module.Orders.Distinct()
                .Sum(p => Enumerable.Range(0, Rows).Sum(row => Enumerable.Range(0, module.ChannelCount).Count(c => IsBeyondTable(module.Patterns[p][row, c].Period))));
            _minPeriod = _extendedNotes > 0 ? ExtendedMinPeriod : MinPeriod;
        }

        /// <summary>True for a note period in no ProTracker finetune row.</summary>
        private static bool IsBeyondTable(int period) => period is > 0 and < LowestTablePeriod or > HighestTablePeriod;

        /// <summary>
        /// The note a written period plays, counted from C-0: ProTracker's lookup (the first finetune-0 note not above it, B-3
        /// at most) or, for a note beyond the range, OpenMPT's nearest note.
        /// </summary>
        private int NoteOf(int period)
        {
            if (_extendedNotes > 0 && IsBeyondTable(period))
            {
                short nearest = ExtendedPeriods.MinBy(p => Math.Abs(p - period));
                return Array.IndexOf(ExtendedPeriods, nearest);
            }

            for (int i = 0; i < TopNote - BottomNote; i++)
            {
                if (period >= PeriodTable[i])
                {
                    return BottomNote + i;
                }
            }

            return TopNote;
        }

        /// <summary>
        /// The period, in quarters, OpenMPT gives <paramref name="note"/> (counted from C-0) at <paramref name="fineTune"/>
        /// (0–15, 8–15 meaning −8 … −1), before its limits: ProTracker's C-1 octave for that finetune, doubled for C-0 and
        /// halved for each octave up, the quarters truncated. Finetune 0 reads <see cref="ExtendedPeriods"/> instead, except
        /// B-3 in a song within ProTracker's range, which OpenMPT plays at 113¼ (all measured with libopenmpt, ADR 0060).
        /// </summary>
        private int NoteQuarters(int note, int fineTune)
        {
            if (fineTune == 0 && note < ExtendedPeriods.Length && (note != TopNote || _extendedNotes > 0))
            {
                return ExtendedPeriods[note] * Quarters;
            }

            return (PeriodTable[(fineTune * TableStride) + (note % 12)] * 2 * Quarters) >> (note / 12);
        }

        /// <summary>The period, in quarters, a note plays at: <see cref="NoteQuarters"/>, never below <see cref="_minPeriod"/>.</summary>
        private int NotePeriod(int note, int fineTune) => Math.Max(NoteQuarters(note, fineTune), _minPeriod * Quarters);

        /// <summary>
        /// The period, in quarters, of the note <paramref name="semitones"/> above the channel's: OpenMPT's arpeggio and
        /// glissando take the first note of the channel's finetune that is not above its period, so the period snaps to a note.
        /// In a song within ProTracker's range a note past B-3 reads the finetune's notes again from C-1, 37 notes back (36
        /// counts as C-1), as OpenMPT does. A period above every note stays as it is.
        /// </summary>
        private int SnappedPeriod(Voice ch, int semitones)
        {
            int first = _extendedNotes > 0 ? 0 : BottomNote;
            int last = _extendedNotes > 0 ? ExtendedPeriods.Length - 1 : TopNote;
            int baseNote = first;
            while (baseNote <= last && ch.Period < NoteQuarters(baseNote, ch.FineTune))
            {
                baseNote++;
            }

            if (baseNote > last && semitones == 0)
            {
                return ch.Period;
            }

            int note = baseNote + semitones;
            if (_extendedNotes == 0 && note > TopNote)
            {
                note = BottomNote + Math.Max(note - BottomNote - TableStride, 0);
            }

            return NotePeriod(note, ch.FineTune);
        }

        /// <summary>
        /// The smallest period, in quarters, a slide reaches: 14 in a song beyond ProTracker's range, otherwise the B-3 of the
        /// channel's finetune but not below 113 (OpenMPT's limits, measured; no ProTracker underflow).
        /// </summary>
        private int SlideMin(Voice ch) =>
            _extendedNotes > 0 ? ExtendedMinPeriod * Quarters : Math.Max(MinPeriod * Quarters, NoteQuarters(TopNote, ch.FineTune));

        /// <summary>The largest period, in quarters, a slide reaches: 3424, or within ProTracker's range the C-1 of the channel's finetune.</summary>
        private int SlideMax(Voice ch) =>
            _extendedNotes > 0 ? ExtendedMaxPeriod * Quarters : NoteQuarters(BottomNote, ch.FineTune);

        public Playback Run()
        {
            // restartSong() + modPlay(): speed 6, 125 BPM, order 0 row 0, and the first tick reads row 0.
            _speed = 6;
            _modBpm = 125;
            _modPos = 0;
            _modPattern = _module.OrderTable[0];
            _row = 0;
            _tick = _speed - 1;

            List<PlaybackTick> ticks = [];
            double time = 0;
            PlaybackEnd end;
            int? loopTick = null;
            while (true)
            {
                bool more = TickReplayer(ticks.Count);
                double duration = 1.0 / TickHz();
                ticks.Add(new PlaybackTick(time, duration, _playOrder, _playRow, _tick, _ledFilter, Snapshot()));
                time += duration;

                if (_doStopSong)
                {
                    end = PlaybackEnd.Stops;
                    break;
                }

                if (!more)
                {
                    // The next row was played before, or the song wrapped past its end: it continues from that row.
                    int next = _firstTickOfRow[(_modPos * Rows) + _row];
                    if (next >= 0)
                    {
                        loopTick = next;
                        end = PlaybackEnd.Loops;
                    }
                    else
                    {
                        _notes.Add(
                            "The song wraps to order {Order} row {Row}, which it never played; playback ends there (ProTracker's MOD2WAV does the same)",
                            _modPos,
                            _row);
                        end = PlaybackEnd.Stops;
                    }

                    break;
                }

                if (ticks.Count >= MaxTicks)
                {
                    _notes.Add("The song didn't end within {Ticks} ticks; playback stops there", MaxTicks);
                    end = PlaybackEnd.TooLong;
                    break;
                }
            }

            if (_extendedNotes > 0)
            {
                _notes.Add(
                    "{Count} notes lie beyond ProTracker's range (period below {Low} or above {High}), so the song was written in a tracker with more octaves: they play at their own pitch and slides reach periods {Min}–{Max}, as in OpenMPT; ProTracker 2 would play silence or a stuck tone",
                    _extendedNotes,
                    LowestTablePeriod,
                    HighestTablePeriod,
                    ExtendedMinPeriod,
                    ExtendedMaxPeriod);
            }

            if (_usesInvertLoop)
            {
                _notes.Add("The song uses EFx (invert loop), which rewrites a sample while it plays; not reproduced");
            }

            return new Playback(ticks, end == PlaybackEnd.Loops ? loopTick : null, end) { NotePitches = _notePitches };
        }

        /// <summary>Ticks per second at the current tempo, scaled for the source Amiga's region.</summary>
        private double TickHz() =>
            _tickRateScale * (_soundtracker ? _soundtrackerBpm * 2.0 / 5.0 : CiaTickHz(_modBpm));

        /// <summary>Every channel's sound after this tick, from the Paula registers the replayer wrote.</summary>
        private ChannelTick[] Snapshot()
        {
            ChannelTick[] channels = new ChannelTick[_voices.Length];
            for (int i = 0; i < _voices.Length; i++)
            {
                Voice v = _voices[i];

                // pt2_paula.c audxper/audxvol: period 0 is 65536; volume uses 7 bits, capped at 64. Paula's floor of 113 is
                // left out: OpenMPT limits notes and slides where they are set, and lets vibrato go past (ADR 0060).
                double period = v.PerReg <= 0 ? 65536 : v.PerReg / (double)Quarters;
                int volume = Math.Min(v.VolReg & 0x7F, 64);
                channels[i] = new ChannelTick(
                    v.Playing,
                    AmigaPitch.FromPeriod(period, _clockHz),
                    volume / 64.0,
                    _pans[i],
                    v.Started,
                    Block(v.PtrReg, v.LenReg));
                v.Started = null;
            }

            return channels;
        }

        /// <summary>A Paula pointer and length (in words; 0 means 65536) as a stretch of frames.</summary>
        private static SampleBlock Block(Pointer pointer, ushort lengthWords) =>
            new(pointer.Sample, pointer.Offset, (lengthWords == 0 ? 65536 : lengthWords) * 2);

        // ---- pt2_replayer.c tickReplayer() -------------------------------------------------------------------------

        /// <summary>One tick (number <paramref name="tickIndex"/>). Returns false when the song ends after this tick (MOD2WAV's renderEndCheck).</summary>
        private bool TickReplayer(int tickIndex)
        {
            // quirk: CIA BPM changes are delayed by one tick in PT, so handle previous tick's BPM change now
            if (_ciaSetBpm != -1)
            {
                SetTempo(_ciaSetBpm);
                _ciaSetBpm = -1;
            }

            _tick++;
            bool readNewNote = false;
            if (_tick >= _speed)
            {
                _tick = 0;
                readNewNote = true;
            }

            if (readNewNote)
            {
                _playOrder = _modPos;
                _playRow = _row;

                if (_pattDelTime2 == 0)
                {
                    _dmaconTemp = 0;

                    // setCurrRowToVisited (MOD2WAV)
                    int visit = (_modPos * Rows) + _row;
                    _visited[visit] = true;
                    if (_firstTickOfRow[visit] < 0)
                    {
                        _firstTickOfRow[visit] = tickIndex;
                    }

                    foreach (Voice ch in _voices)
                    {
                        PlayVoice(ch);
                        ch.VolReg = (ushort)ch.Volume;
                    }

                    SetDma();
                }
                else
                {
                    foreach (Voice ch in _voices)
                    {
                        CheckEffects(ch);
                    }
                }

                _row++;

                if (_pattDelTime > 0)
                {
                    _pattDelTime2 = _pattDelTime;
                    _pattDelTime = 0;
                }

                if (_pattDelTime2 > 0)
                {
                    _pattDelTime2--;
                    if (_pattDelTime2 > 0)
                    {
                        _row--;
                    }
                }

                if (_pBreakFlag)
                {
                    _row = _pBreakPosition;
                    _pBreakPosition = 0;
                    _pBreakFlag = false;
                }

                if (_row >= Rows || _posJumpAssert)
                {
                    NextPosition();
                }
            }
            else
            {
                foreach (Voice ch in _voices)
                {
                    CheckEffects(ch);
                }

                if (_posJumpAssert)
                {
                    NextPosition();
                }
            }

            return RenderEndCheck();
        }

        /// <summary>MOD2WAV's end check: on the row's last tick, stop if the next row was played before or the song wrapped.</summary>
        private bool RenderEndCheck()
        {
            if (_pattDelTime2 == 0 && _tick == _speed - 1)
            {
                bool rowVisited = _visited[(_modPos * Rows) + _row];
                if (rowVisited || _modRenderDone)
                {
                    _modRenderDone = false;
                    return false;
                }
            }

            return true;
        }

        private void NextPosition()
        {
            _row = _pBreakPosition;
            _pBreakPosition = 0;
            _posJumpAssert = false;

            _modPos = (short)((_modPos + 1) & 127);
            if (_modPos >= _module.SongLength)
            {
                _modPos = 0;
                _modRenderDone = true;
            }

            _modPattern = _module.OrderTable[_modPos];
        }

        private void SetTempo(int bpm)
        {
            if (bpm is >= MinBpm and <= MaxBpm)
            {
                _modBpm = bpm;
            }
        }

        private void SetSpeedValue(int speed)
        {
            _speed = speed;
            _tick = 0;
        }

        private void SetDma()
        {
            foreach (Voice ch in _voices)
            {
                if ((_dmaconTemp & ch.DmaBit) != 0)
                {
                    // DMA starts: Paula latches the pointer and length written so far.
                    ch.Started = Block(ch.PtrReg, ch.LenReg);
                    ch.Playing = true;
                }

                // New pointer and length, taking effect when the current part ends.
                ch.PtrReg = ch.LoopStart;
                ch.LenReg = ch.RepLen;
            }
        }

        // ---- playVoice() / setPeriod() ------------------------------------------------------------------------------

        private void PlayVoice(Voice ch)
        {
            if (ch is { Note: 0, Cmd: 0 })
            {
                ch.PerReg = ch.Period;
            }

            ModCell cell = AsPlayed(_module.Patterns[_modPattern][_row, ch.Index]);
            ch.Note = (short)cell.Period;
            ch.Cmd = (ushort)((cell.Effect << 8) | cell.EffectParameter);

            if (cell.Sample is >= 1 and <= 31)
            {
                SampleView s = cell.Sample <= _samples.Length ? _samples[cell.Sample - 1] : SampleView.Empty;

                ch.Start = s.IsEmpty ? Pointer.Null : new Pointer(cell.Sample - 1, 0);
                ch.FineTune = (byte)(s.FineTune & 0xF);
                ch.Volume = unchecked((sbyte)s.Volume);
                ch.Length = (ushort)(s.Length >> 1);
                ch.RepLen = (ushort)(s.LoopLength >> 1);

                // A looping sample ends at its loop end, as in OpenMPT; ProTracker plays a loop that starts at 0 to the
                // sample's end once first (ADR 0060). A loop of one word is ProTracker's "no loop".
                ushort repeat = (ushort)(s.LoopStart >> 1);
                if (repeat > 0 || ch.RepLen > 1)
                {
                    ch.LoopStart = ch.Start.Plus(repeat << 1);
                    ch.Length = (ushort)(repeat + ch.RepLen);
                }
                else
                {
                    ch.LoopStart = ch.Start;
                }

                // non-PT2 requirement (set safe sample space for uninitialized voices)
                if (ch.Length == 0)
                {
                    ch.LoopStart = Pointer.Null;
                }
            }

            if ((ch.Note & 0xFFF) > 0)
            {
                RecordNotePitch(ch);
                if ((ch.Cmd & 0xFF0) == 0xE50)
                {
                    ch.FineTune = (byte)(ch.Cmd & 0xF);
                    SetPeriod(ch);
                }
                else
                {
                    int cmd = (ch.Cmd & 0x0F00) >> 8;
                    if (cmd is 3 or 5)
                    {
                        SetTonePorta(ch);
                        CheckMoreEffects(ch);
                    }
                    else if (cmd == 9)
                    {
                        CheckMoreEffects(ch);
                        SetPeriod(ch);
                    }
                    else
                    {
                        SetPeriod(ch);
                    }
                }
            }
            else
            {
                CheckMoreEffects(ch);
            }
        }

        /// <summary>
        /// Keeps the pitch the channel's new note plays at, the first time its row plays: OpenMPT's period for the note at
        /// the channel's finetune, or the row's <c>E5x</c> (the target, on a tone portamento row).
        /// </summary>
        private void RecordNotePitch(Voice ch)
        {
            int fineTune = (ch.Cmd & 0xFF0) == 0xE50 ? ch.Cmd & 0xF : ch.FineTune;
            double period = NotePeriod(NoteOf(ch.Note & 0xFFF), fineTune) / (double)Quarters;
            _notePitches.TryAdd(new NotePosition(_modPos, _row, ch.Index), AmigaPitch.FromPeriod(period, _clockHz));
        }

        private void SetPeriod(Voice ch)
        {
            // OpenMPT's period for the note, where ProTracker reads its finetune's table (ADR 0060).
            ch.Period = NotePeriod(NoteOf(ch.Note & 0xFFF), ch.FineTune);

            if ((ch.Cmd & 0xFF0) != 0xED0)
            {
                if ((ch.WaveControl & 0x04) == 0)
                {
                    ch.VibratoPos = 0;
                }

                if ((ch.WaveControl & 0x40) == 0)
                {
                    ch.TremoloPos = 0;
                }

                ch.LenReg = ch.Length;
                ch.PtrReg = ch.Start;
                if (ch.Start.IsNull)
                {
                    ch.LoopStart = Pointer.Null;
                    ch.LenReg = 1;
                    ch.RepLen = 1;
                }

                ch.PerReg = ch.Period;
                _dmaconTemp |= ch.DmaBit;
            }

            CheckMoreEffects(ch);
        }

        // ---- effects ------------------------------------------------------------------------------------------------

        private void CheckEffects(Voice ch)
        {
            Chkefx2(ch);

            // tremolo sets the volume itself
            if ((ch.Cmd & 0x0F00) >> 8 != 0x7)
            {
                ch.VolReg = (ushort)ch.Volume;
            }
        }

        private void Chkefx2(Voice ch)
        {
            if ((ch.Cmd & 0xFFF) == 0)
            {
                return;
            }

            int cmd = (ch.Cmd & 0x0F00) >> 8;
            switch (cmd)
            {
                case 0x0: Arpeggio(ch); return;
                case 0x1: PortaUp(ch); return;
                case 0x2: PortaDown(ch); return;
                case 0x3: TonePortamento(ch); return;
                case 0x4: Vibrato(ch); return;
                case 0x5: TonePortNoChange(ch); VolumeSlide(ch); return;
                case 0x6: Vibrato2(ch); VolumeSlide(ch); return;
                case 0xE: ECommands(ch); return;
            }

            ch.PerReg = ch.Period;
            if (cmd == 0x7)
            {
                Tremolo(ch);
            }
            else if (cmd == 0xA)
            {
                VolumeSlide(ch);
            }
        }

        private void CheckMoreEffects(Voice ch)
        {
            int cmd = (ch.Cmd & 0x0F00) >> 8;
            switch (cmd)
            {
                case 0x9: SampleOffset(ch); return;
                case 0xB: PositionJump(ch); return;
                case 0xD: PatternBreak(ch); return;
                case 0xE: ECommands(ch); return;
                case 0xF: SetSpeed(ch); return;
            }

            if (cmd == 0xC)
            {
                ch.Volume = unchecked((sbyte)(ch.Cmd & 0xFF));
                if ((byte)ch.Volume > 64)
                {
                    ch.Volume = 64;
                }

                return;
            }

            // An arpeggio plays its base note snapped to the table on its first tick too, as in OpenMPT (ADR 0060).
            ch.PerReg = cmd == 0 && (ch.Cmd & 0xFF) != 0 ? SnappedPeriod(ch, 0) : ch.Period;
        }

        private void ECommands(Voice ch)
        {
            switch ((ch.Cmd & 0x00F0) >> 4)
            {
                case 0x0: FilterOnOff(ch); return;
                case 0x1: FinePortaUp(ch); return;
                case 0x2: FinePortaDown(ch); return;
                case 0x3: ch.GlissFunk = (byte)((ch.GlissFunk & 0xF0) | (ch.Cmd & 0x0F)); return;
                case 0x4: ch.WaveControl = (byte)((ch.WaveControl & 0xF0) | (ch.Cmd & 0x0F)); return;
                case 0x5: ch.FineTune = (byte)(ch.Cmd & 0xF); return;
                case 0x6: JumpLoop(ch); return;
                case 0x7: ch.WaveControl = (byte)(((ch.Cmd & 0xF) << 4) | (ch.WaveControl & 0xF)); return;
                case 0x8: return; // E8x Karplus-Strong: off by default in pt2-clone
                case 0x9: RetrigNote(ch); return;
                case 0xA: VolumeFineUp(ch); return;
                case 0xB: VolumeFineDown(ch); return;
                case 0xC: NoteCut(ch); return;
                case 0xD: NoteDelay(ch); return;
                case 0xE: PatternDelay(ch); return;
                default: FunkIt(ch); return;
            }
        }

        private void Arpeggio(Voice ch)
        {
            int arpNote = (_tick % 3) switch
            {
                1 => (ch.Cmd >> 4) & 0xF,
                2 => ch.Cmd & 0xF,
                _ => 0,
            };

            // OpenMPT's notes for the finetune, where ProTracker reads its table (and on into the next one, ADR 0060).
            ch.PerReg = SnappedPeriod(ch, arpNote);
        }

        private void PortaUp(Voice ch)
        {
            // OpenMPT stops at the finetune's top note; ProTracker's underflow (a slide past 0 wraps) is gone (ADR 0060).
            ch.Period = Math.Max(ch.Period - (((ch.Cmd & 0xFF) & _lowMask) * Quarters), SlideMin(ch));
            _lowMask = 0xFF;
            ch.PerReg = ch.Period;
        }

        private void PortaDown(Voice ch)
        {
            ch.Period = Math.Min(ch.Period + (((ch.Cmd & 0xFF) & _lowMask) * Quarters), SlideMax(ch));
            _lowMask = 0xFF;
            ch.PerReg = ch.Period;
        }

        private void FinePortaUp(Voice ch)
        {
            if (_tick == 0)
            {
                _lowMask = 0xF;
                PortaUp(ch);
            }
        }

        private void FinePortaDown(Voice ch)
        {
            if (_tick == 0)
            {
                _lowMask = 0xF;
                PortaDown(ch);
            }
        }

        private void SetTonePorta(Voice ch)
        {
            // OpenMPT slides to the note's own period (ProTracker's table lookup, with its finetune quirk, lands on the same note).
            SetTonePortaDirection(ch, NotePeriod(NoteOf(ch.Note & 0xFFF), ch.FineTune));
        }

        /// <summary>Sets where a tone portamento goes, in quarters, and which way.</summary>
        private static void SetTonePortaDirection(Voice ch, int wanted)
        {
            ch.WantedPeriod = wanted;
            ch.TonePortDirec = 0;

            if (ch.Period == ch.WantedPeriod)
            {
                ch.WantedPeriod = 0;
            }
            else if (ch.Period > ch.WantedPeriod)
            {
                ch.TonePortDirec = 1;
            }
        }

        private void TonePortNoChange(Voice ch)
        {
            if (ch.WantedPeriod <= 0)
            {
                return;
            }

            if (ch.TonePortDirec > 0)
            {
                ch.Period -= ch.TonePortSpeed * Quarters;
                if (ch.Period <= ch.WantedPeriod)
                {
                    ch.Period = ch.WantedPeriod;
                    ch.WantedPeriod = 0;
                }
            }
            else
            {
                ch.Period += ch.TonePortSpeed * Quarters;
                if (ch.Period >= ch.WantedPeriod)
                {
                    ch.Period = ch.WantedPeriod;
                    ch.WantedPeriod = 0;
                }
            }

            // Glissando snaps to the finetune's notes, as OpenMPT's do, beyond ProTracker's range too (ADR 0060).
            ch.PerReg = (ch.GlissFunk & 0xF) == 0 ? ch.Period : SnappedPeriod(ch, 0);
        }

        private void TonePortamento(Voice ch)
        {
            if ((ch.Cmd & 0xFF) > 0)
            {
                ch.TonePortSpeed = (byte)(ch.Cmd & 0xFF);
                ch.Cmd &= 0xFF00;
            }

            TonePortNoChange(ch);
        }

        private static void Vibrato(Voice ch)
        {
            if ((ch.Cmd & 0x0F) > 0)
            {
                ch.VibratoCmd = (byte)((ch.VibratoCmd & 0xF0) | (ch.Cmd & 0x0F));
            }

            if ((ch.Cmd & 0xF0) > 0)
            {
                ch.VibratoCmd = (byte)((ch.Cmd & 0xF0) | (ch.VibratoCmd & 0x0F));
            }

            Vibrato2(ch);
        }

        private static void Vibrato2(Voice ch)
        {
            // OpenMPT's vibrato, measured (ADR 0060): 64 steps from -127 to 127, depth / 16 in quarter periods, rounded toward 0.
            int pos = (ch.VibratoPos >> 2) & 0x3F;
            int half = pos & 0x1F;
            int value = (ch.WaveControl & 3) switch
            {
                0 => OpenMptSine[half],
                1 => pos < 32 ? half << 2 : (half << 2) - 127,
                _ => 127,
            };

            if ((ch.WaveControl & 3) != 1 && pos >= 32)
            {
                value = -value;
            }

            ch.PerReg = Math.Max(ch.Period + (value * (ch.VibratoCmd & 0xF) / 16), 1);

            ch.VibratoPos = unchecked((byte)(ch.VibratoPos + ((ch.VibratoCmd >> 2) & 0x3C)));
        }

        private static void Tremolo(Voice ch)
        {
            if ((ch.Cmd & 0x0F) > 0)
            {
                ch.TremoloCmd = (byte)((ch.TremoloCmd & 0xF0) | (ch.Cmd & 0x0F));
            }

            if ((ch.Cmd & 0xF0) > 0)
            {
                ch.TremoloCmd = (byte)((ch.Cmd & 0xF0) | (ch.TremoloCmd & 0x0F));
            }

            int tremoloPos = (ch.TremoloPos >> 2) & 0x1F;
            int tremoloData = ((ch.WaveControl >> 4) & 3) switch
            {
                0 => VibratoTable[tremoloPos],
                1 => ch.VibratoPos < 128 ? tremoloPos << 3 : 255 - (tremoloPos << 3), // PT bug: reads the vibrato position
                _ => 255,
            };

            tremoloData = (tremoloData * (ch.TremoloCmd & 0xF)) >> 6;
            tremoloData = ch.TremoloPos < 128
                ? Math.Min(ch.Volume + tremoloData, 64)
                : Math.Max(ch.Volume - tremoloData, 0);
            ch.VolReg = (ushort)tremoloData;

            ch.TremoloPos = unchecked((byte)(ch.TremoloPos + ((ch.TremoloCmd >> 2) & 0x3C)));
        }

        private static void VolumeSlide(Voice ch)
        {
            int param = ch.Cmd & 0xFF;
            if ((param & 0xF0) == 0)
            {
                ch.Volume = (sbyte)Math.Max(ch.Volume - (param & 0x0F), 0);
            }
            else
            {
                ch.Volume = (sbyte)Math.Min(ch.Volume + (param >> 4), 64);
            }
        }

        private void VolumeFineUp(Voice ch)
        {
            if (_tick == 0)
            {
                ch.Volume = (sbyte)Math.Min(ch.Volume + (ch.Cmd & 0xF), 64);
            }
        }

        private void VolumeFineDown(Voice ch)
        {
            if (_tick == 0)
            {
                ch.Volume = (sbyte)Math.Max(ch.Volume - (ch.Cmd & 0xF), 0);
            }
        }

        private static void SampleOffset(Voice ch)
        {
            if ((ch.Cmd & 0xFF) > 0)
            {
                ch.OffsetParam = (byte)(ch.Cmd & 0xFF);
            }

            int newOffset = ch.OffsetParam << 7;
            if (newOffset < ch.Length)
            {
                ch.Length = (ushort)(ch.Length - newOffset);
                ch.Start = ch.Start.Plus(newOffset << 1);
            }
            else
            {
                ch.Length = 1;
            }
        }

        private void PositionJump(Voice ch)
        {
            _modPos = (short)((ch.Cmd & 0xFF) - 1); // B00 results in -1, but it safely jumps to order 0
            _pBreakPosition = 0;
            _posJumpAssert = true;
        }

        private void PatternBreak(Voice ch)
        {
            _pBreakPosition = unchecked((sbyte)((((ch.Cmd & 0xF0) >> 4) * 10) + (ch.Cmd & 0x0F)));
            if ((byte)_pBreakPosition > 63)
            {
                _pBreakPosition = 0;
            }

            _posJumpAssert = true;
        }

        private void SetSpeed(Voice ch)
        {
            int param = ch.Cmd & 0xFF;
            if (param > 0)
            {
                if (param < 32)
                {
                    SetSpeedValue(param);
                }
                else
                {
                    _ciaSetBpm = param; // the CIA chip doesn't use its new timer value until the next interrupt
                }
            }
            else
            {
                _doStopSong = true;
            }
        }

        private void JumpLoop(Voice ch)
        {
            if (_tick != 0)
            {
                return;
            }

            if ((ch.Cmd & 0xF) == 0)
            {
                ch.PattPos = _row;
            }
            else
            {
                if (ch.LoopCount == 0)
                {
                    ch.LoopCount = (sbyte)(ch.Cmd & 0xF);
                }
                else if (--ch.LoopCount == 0)
                {
                    return;
                }

                _pBreakPosition = ch.PattPos;
                _pBreakFlag = true;

                // MOD2WAV: the looped rows play again, so they don't count as visited
                for (int row = _pBreakPosition; row <= _row; row++)
                {
                    _visited[(_modPos * Rows) + row] = false;
                }
            }
        }

        private void RetrigNote(Voice ch)
        {
            if ((ch.Cmd & 0xF) > 0)
            {
                if (_tick == 0 && (ch.Note & 0xFFF) > 0)
                {
                    return;
                }

                if (_tick % (ch.Cmd & 0xF) == 0)
                {
                    DoRetrg(ch);
                }
            }
        }

        private void NoteCut(Voice ch)
        {
            if (_tick == (ch.Cmd & 0xF))
            {
                ch.Volume = 0;
            }
        }

        private void NoteDelay(Voice ch)
        {
            if (_tick == (ch.Cmd & 0xF) && (ch.Note & 0xFFF) > 0)
            {
                DoRetrg(ch);
            }
        }

        private void PatternDelay(Voice ch)
        {
            if (_tick == 0 && _pattDelTime2 == 0)
            {
                _pattDelTime = (byte)((ch.Cmd & 0xF) + 1);
            }
        }

        private void FunkIt(Voice ch)
        {
            if (_tick == 0)
            {
                ch.GlissFunk = (byte)(((ch.Cmd & 0xF) << 4) | (ch.GlissFunk & 0xF));
                _usesInvertLoop |= (ch.Cmd & 0xF) != 0;
            }
        }

        /// <summary>E0x: E00 turns the Amiga's LED low-pass filter on, E01 off (bit 0, active low).</summary>
        private void FilterOnOff(Voice ch)
        {
            if (_tick == 0)
            {
                _ledFilter = (ch.Cmd & 1) == 0;
            }
        }

        private static void DoRetrg(Voice ch)
        {
            // DMA off, then on with the note's start and length; the loop takes effect after this part
            ch.Started = Block(ch.Start, ch.Length);
            ch.Playing = true;
            ch.PerReg = ch.Period;
            ch.PtrReg = ch.LoopStart;
            ch.LenReg = ch.RepLen;
        }

        // ---- cells ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The cell as the chosen player sees it. Ultimate Soundtracker's commands are turned into ProTracker's the way
        /// OpenMPT does: 0xy (from 03) and 1xy are arpeggio, 2xy slides up y or else down x, the rest are dropped.
        /// </summary>
        private ModCell AsPlayed(ModCell cell)
        {
            if (!_soundtracker)
            {
                return cell;
            }

            int x = cell.EffectParameter >> 4;
            int y = cell.EffectParameter & 0xF;
            (int effect, int parameter) = cell.Effect switch
            {
                0x0 when cell.EffectParameter >= 0x03 => (0x0, cell.EffectParameter),
                0x1 => (0x0, cell.EffectParameter),
                0x2 when y > 0 => (0x1, y),
                0x2 when x > 0 => (0x2, x),
                _ => (0, 0),
            };

            return cell with { Effect = effect, EffectParameter = parameter };
        }

        private static short[] BuildPeriodTable()
        {
            List<short> table = [];
            for (int index = 0; index < 16; index++)
            {
                table.AddRange(AmigaPitch.Table(index < 8 ? index : index - 16).ToArray());
                table.Add(0);
            }

            // pt2_tables.c: the words ProTracker keeps right after the table (CursorPosTable, UnshiftedKeymap)
            short[] followingWords = [774, 1800, 2314, 3087, 4113, 4627, 5400, 6426, 6940, 7713, 8739, 9253, 24625, 12851, 13365];
            table.AddRange(followingWords);
            return [.. table];
        }

        /// <summary>A sample as the replayer sets it up: lengths in bytes, relative to the song model's sample data.</summary>
        private readonly record struct SampleView(bool IsEmpty, int FineTune, int Volume, int Length, int LoopStart, int LoopLength)
        {
            public static SampleView Empty { get; } = new(true, 0, 0, 0, 0, 2);

            /// <summary>
            /// ProTracker reads a non-looping sample's loop as 1 word at its start. Under Ultimate Soundtracker rules the
            /// sound before the loop is skipped (as <c>ModToSong</c> does), so the loop starts at frame 0, and there is no finetune.
            /// </summary>
            public static SampleView Of(ModSample sample, bool soundtracker)
            {
                int skip = soundtracker && sample.Loops ? sample.LoopStart : 0;
                return new SampleView(
                    sample.IsEmpty,
                    soundtracker ? 0 : sample.FineTune,
                    sample.Volume,
                    sample.Length - skip,
                    sample.Loops ? sample.LoopStart - skip : 0,
                    sample.Loops ? sample.LoopLength : 2);
            }
        }

        /// <summary>A Paula sample pointer: which sample and how far into it, or none.</summary>
        private readonly record struct Pointer(int? Sample, int Offset)
        {
            public static Pointer Null { get; } = new(null, 0);

            public bool IsNull => Sample is null;

            public Pointer Plus(int bytes) => IsNull ? this : this with { Offset = Offset + bytes };
        }

        /// <summary>pt2-clone's <c>moduleChannel_t</c>, plus the Paula registers it writes.</summary>
        /// <param name="index">Channel number (0-based).</param>
        private sealed class Voice(int index)
        {
            public Pointer Start = Pointer.Null;
            public Pointer LoopStart = Pointer.Null;
            public sbyte Volume;
            public sbyte TonePortDirec;
            public sbyte PattPos;
            public sbyte LoopCount;
            public byte WaveControl;
            public byte GlissFunk;
            public byte OffsetParam;
            public byte TonePortSpeed;
            public byte VibratoCmd;
            public byte TremoloCmd;
            public byte FineTune;
            public byte VibratoPos;
            public byte TremoloPos;
            /// <summary>The note's period, in quarters (<see cref="Quarters"/>), as are <see cref="WantedPeriod"/> and <see cref="PerReg"/>.</summary>
            public int Period;
            public short Note;
            public int WantedPeriod;
            public ushort Cmd;
            public ushort Length;
            public ushort RepLen;

            // Paula registers and what the snapshot reports.
            public int PerReg;
            public ushort VolReg;
            public Pointer PtrReg = Pointer.Null;
            public ushort LenReg = 1;
            public SampleBlock? Started;
            public bool Playing;

            /// <summary>Channel number (0-based).</summary>
            public int Index { get; } = index;

            /// <summary>The channel's bit in Paula's DMA control register.</summary>
            public ushort DmaBit { get; } = (ushort)(1 << index);
        }
    }
}
