# ADR 0030: Amiga pitch: period → note + cents, finetune, PAL/NTSC clock (T3.2)

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

Plan task T3.2: turning Amiga periods into pitches, which depends on the Amiga's clock, and the Genesis output region.

## Decision

- **Built standalone** (`src/Model/AmigaPitch.cs`); `ModToSong` and the tick player call it in T3.3–T3.4 (David).
- **Two regions replace `--region`** (David): `--input-region` (the Amiga the MOD was made on; sets the Paula clock)
  and `--output-region` (the Genesis that plays the result), both `pal|ntsc`, both **default `pal`** (David, after
  first choosing NTSC: most MODs are PAL and openmpt123 assumes PAL). Options considered: always PAL with no option;
  the clock following one `--region`; a separate `--amiga-clock`. `--region` is **removed**, not aliased: nothing has
  been released (David).
- **The regions set tempo too** (David): `--input-region` is the source's real pitch **and** speed (50 ticks/s on a
  PAL Amiga, 60 on NTSC, at BPM 125; other BPMs scale the same way); `--output-region` is the console. Matching
  regions play one tick per frame; mismatched ones are adjusted so the output keeps the source's speed and pitch.
  Options considered: NTSC output always one tick per frame (20 % faster for PAL songs; David's first answer,
  replaced by this one); `--input-region` changing pitch only. Built in the tick player (T3.4) and the writers.
- **Finetune uses ProTracker 2's exact tables and lookup** (David, over the formula `period × 2^(−ft/96)`): matches
  what composers heard and what openmpt123 plays. Tables taken from pt2-clone `src/pt2_tables.c` (BSD-3-Clause,
  commit 554bf79), checked against the expected pitches by a test. Not Furnace (GPL).
- **Finetune is applied by the tick player** (David): cell notes stay as written (finetune 0) because a cell often
  doesn't name its instrument; `AmigaPitch.PeriodOf` recovers the period from a note's playback rate.
- Decided while implementing: clocks are the crystals ÷ 8 (3 546 895 / 3 579 545 Hz) instead of the doc's
  7 093 789.2 / 7 159 090.5 ÷ 2 (0.2 ppm apart); period 428 on PAL is exactly C-2 at 0 cents, so pitch names don't
  move with the clock but cents do (NTSC +15.9). ProTracker 2 plays period 0 for written periods below 113 (its
  tables' zero padding); Mod2Xgm keeps the written period and the caller adds a conversion note.

## Consequences

Regions in [cli.md](../cli.md#regions).
