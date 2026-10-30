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
/// <para>One departure, as OpenMPT does (docs/adr/0055-notes-beyond-protracker-range.md): a song with notes outside
/// every ProTracker finetune row (period below 108 or above 907) was written in a tracker with more octaves, so those notes
/// play at their own pitch, snapped to the nearest note from C-0 to B-5, and slides run from period 14 to 3424.
/// ProTracker 2 would play silence or a stuck tone there.</para>
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
        private const int MaxPeriod = 856;
        private const int TableStride = 37;

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

        /// <summary>The smallest period slides reach and Paula plays: ProTracker's 113, or 14 with <see cref="_extendedNotes"/>.</summary>
        private readonly int _minPeriod;

        /// <summary>The largest period slides reach: ProTracker's 856, or 3424 with <see cref="_extendedNotes"/>.</summary>
        private readonly int _maxPeriod;

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
            _maxPeriod = _extendedNotes > 0 ? ExtendedMaxPeriod : MaxPeriod;
        }

        /// <summary>True for a note period in no ProTracker finetune row.</summary>
        private static bool IsBeyondTable(int period) => period is > 0 and < LowestTablePeriod or > HighestTablePeriod;

        /// <summary>
        /// A note beyond ProTracker's range as OpenMPT plays it: the nearest note of <see cref="ExtendedPeriods"/>, tuned by
        /// <paramref name="fineTune"/> (0–15, 8–15 meaning −8 … −1) in eighths of a semitone.
        /// </summary>
        private static short ExtendedPeriod(int note, int fineTune)
        {
            short nearest = ExtendedPeriods.MinBy(p => Math.Abs(p - note));
            int signed = fineTune < 8 ? fineTune : fineTune - 16;
            return (short)Math.Round(nearest * Math.Pow(2, -signed / 96.0));
        }

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

            return new Playback(ticks, end == PlaybackEnd.Loops ? loopTick : null, end);
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

                // pt2_paula.c audxper/audxvol: period 0 is 65536, below 113 plays at 113; volume uses 7 bits, capped at 64.
                int period = v.PerReg == 0 ? 65536 : Math.Max(v.PerReg, _minPeriod);
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
                ch.PerReg = (ushort)ch.Period;
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

                ushort repeat = (ushort)(s.LoopStart >> 1);
                if (repeat > 0)
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

        private void SetPeriod(Voice ch)
        {
            int note = ch.Note & 0xFFF;
            int i;
            for (i = 0; i < TableStride; i++)
            {
                // periodTable[36] = 0, so i=36 is safe
                if (note >= PeriodTable[i])
                {
                    break;
                }
            }

            // yes it's safe if i=37 because of zero-padding
            ch.Period = _extendedNotes > 0 && IsBeyondTable(note) ? ExtendedPeriod(note, ch.FineTune) : PeriodTable[(ch.FineTune * TableStride) + i];

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

                ch.PerReg = (ushort)ch.Period;
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

            ch.PerReg = (ushort)ch.Period;
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

            ch.PerReg = (ushort)ch.Period;
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
            int arpNote;
            int arpTick = _tick % 3;
            if (arpTick == 1)
            {
                arpNote = ch.Cmd >> 4 & 0xF;
            }
            else if (arpTick == 2)
            {
                arpNote = ch.Cmd & 0xF;
            }
            else
            {
                ch.PerReg = (ushort)ch.Period;
                return;
            }

            int row = ch.FineTune * TableStride;
            if (_extendedNotes > 0 && ch.Period < PeriodTable[row + TableStride - 2])
            {
                // Above ProTracker's top note: arpeggio by semitones, as OpenMPT does.
                ch.PerReg = (ushort)Math.Max(_minPeriod, Math.Round(ch.Period * Math.Pow(2, -arpNote / 12.0)));
                return;
            }

            for (int baseNote = 0; baseNote < TableStride; baseNote++)
            {
                if (ch.Period >= PeriodTable[row + baseNote])
                {
                    ch.PerReg = (ushort)PeriodTable[row + baseNote + arpNote];
                    break;
                }
            }
        }

        private void PortaUp(Voice ch)
        {
            ch.Period = unchecked((short)(ch.Period - ((ch.Cmd & 0xFF) & _lowMask)));
            _lowMask = 0xFF;

            // PT BUG: sign removed before comparison, underflow not clamped! OpenMPT's extended range clamps it (ADR 0055).
            if ((_extendedNotes > 0 && ch.Period < _minPeriod) || (ch.Period & 0xFFF) < _minPeriod)
            {
                ch.Period = unchecked((short)((ch.Period & 0xF000) | _minPeriod));
            }

            ch.PerReg = (ushort)(ch.Period & 0xFFF);
        }

        private void PortaDown(Voice ch)
        {
            ch.Period = unchecked((short)(ch.Period + ((ch.Cmd & 0xFF) & _lowMask)));
            _lowMask = 0xFF;

            if ((ch.Period & 0xFFF) > _maxPeriod)
            {
                ch.Period = unchecked((short)((ch.Period & 0xF000) | _maxPeriod));
            }

            ch.PerReg = (ushort)(ch.Period & 0xFFF);
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
            int note = ch.Note & 0xFFF;
            int row = ch.FineTune * TableStride;
            if (_extendedNotes > 0 && IsBeyondTable(note))
            {
                SetTonePortaDirection(ch, ExtendedPeriod(note, ch.FineTune));
                return;
            }

            int i = 0;
            while (true)
            {
                // portaPointer[36] = 0, so i=36 is safe
                if (note >= PeriodTable[row + i])
                {
                    break;
                }

                if (++i >= TableStride)
                {
                    i = 35;
                    break;
                }
            }

            if ((ch.FineTune & 8) != 0 && i > 0)
            {
                i--;
            }

            SetTonePortaDirection(ch, PeriodTable[row + i]);
        }

        /// <summary>Sets where a tone portamento goes and which way.</summary>
        private static void SetTonePortaDirection(Voice ch, short wanted)
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
                ch.Period = unchecked((short)(ch.Period - ch.TonePortSpeed));
                if (ch.Period <= ch.WantedPeriod)
                {
                    ch.Period = ch.WantedPeriod;
                    ch.WantedPeriod = 0;
                }
            }
            else
            {
                ch.Period = unchecked((short)(ch.Period + ch.TonePortSpeed));
                if (ch.Period >= ch.WantedPeriod)
                {
                    ch.Period = ch.WantedPeriod;
                    ch.WantedPeriod = 0;
                }
            }

            if ((ch.GlissFunk & 0xF) == 0 || (_extendedNotes > 0 && ch.Period < PeriodTable[(ch.FineTune * TableStride) + TableStride - 2]))
            {
                // No glissando, or above ProTracker's top note where its table has no semitones to snap to.
                ch.PerReg = (ushort)ch.Period;
            }
            else
            {
                int row = ch.FineTune * TableStride;
                int i = 0;
                while (true)
                {
                    if (ch.Period >= PeriodTable[row + i])
                    {
                        break;
                    }

                    if (++i >= TableStride)
                    {
                        i = 35;
                        break;
                    }
                }

                ch.PerReg = (ushort)PeriodTable[row + i];
            }
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
            int vibratoPos = (ch.VibratoPos >> 2) & 0x1F;
            int vibratoData = (ch.WaveControl & 3) switch
            {
                0 => VibratoTable[vibratoPos],
                1 => ch.VibratoPos < 128 ? vibratoPos << 3 : 255 - (vibratoPos << 3),
                _ => 255,
            };

            vibratoData = (vibratoData * (ch.VibratoCmd & 0xF)) >> 7;
            vibratoData = ch.VibratoPos < 128 ? ch.Period + vibratoData : ch.Period - vibratoData;
            ch.PerReg = unchecked((ushort)vibratoData);

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
            ch.PerReg = (ushort)ch.Period;
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
            public short Period;
            public short Note;
            public short WantedPeriod;
            public ushort Cmd;
            public ushort Length;
            public ushort RepLen;

            // Paula registers and what the snapshot reports.
            public ushort PerReg;
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
