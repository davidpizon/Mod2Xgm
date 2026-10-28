# DMF probe files (plan T4.3)

Five tiny songs that check, **by ear**, the parts of [docs/formats/dmf.md](../../../docs/formats/dmf.md) that came
from reading other programs (Furnace, DefleMask's effect list) rather than from DefleMask itself. Each probe is built
so that a wrong guess sounds clearly wrong, or is silent.

The `.dmf` files are made by `tests/Mod2Xgm.Tests/Dmf/DmfProbes.cs`. The tests check them against the code, and
`UPDATE_GOLDEN=1 dotnet test` rewrites them. Each `.txt` file next to a probe lists everything in it, for review.

## What to do (David)

For **each** probe below:

1. **Open** it in DefleMask. It must open without an error.
2. **Play** it and compare with "You should hear". A phone tuner app helps with the notes.
3. **Look** at the pattern and check "You should see".
4. **Save As** into the `deflemask/` folder here, with the **same file name** (for example `deflemask/fm-note.dmf`).
   The tests then check that DefleMask's copy holds the same song as ours (`DmfProbeTests`).
5. **Export VGM** into the `vgm/` folder here, again with the same name (`vgm/fm-note.vgm`).

Then run rescomp once for all five (step 6), and tell Claude what you heard for each probe and whether rescomp
worked. The results go into docs/formats/dmf.md.

6. **rescomp** — in a Command Prompt:

   ```
   cd C:\git\Mod2Xgm\tests\golden\dmf-probes
   mkdir out
   java -jar "%GDK%\bin\rescomp.jar" probes.res out\probes.s
   ```

   It must finish without an error and write `out\probes.s` and `out\probes.h`. The `out\` folder isn't committed.

## The probes

| File | What it checks | You should hear | You should see |
|---|---|---|---|
| `fm-note.dmf` | FM operators are stored in register order (OP1, OP3, OP2, OP4); C is note 12 of the octave below | A pure tone **A-4 (440 Hz)**, then **C-5** (a little higher, 523 Hz), then silence. **Silence from the start** means the operator order is wrong. A C an octave too high or too low means the C rule is wrong | FM1: `A-4`, then `C-5`, then a note off. In the instrument editor, one operator has level (TL) 0 and the others 127: **tell Claude which operator number DefleMask shows for it** (this shows how DefleMask numbers operators) |
| `psg-note.dmf` | PSG channels, the STD volume macro, noise effect `20xy` | A square-wave **A-4**, then a **white-noise hiss** | PSG1: `A-4`. PSG4 (noise): a note with effect `2001` |
| `dac-sample.dmf` | `17xx` turns on the DAC on FM6; the note picks the sample (octave ignored); sample rate codes 5 = 32 kHz, 1 = 8 kHz | Three beeps: **A-4**, **the same A-4 again** (even though the note is two octaves higher), then **E-5** (660 Hz). Wrong pitches mean the rate codes are wrong; a different second beep means the octave isn't ignored | FM6: `1701` on the first row; notes `C-3`, `C-5`, `C#3`. Samples "a440 @ 32 kHz" (rate 32000) and "e660 @ 8 kHz" (rate 8000) |
| `custom-hz.dmf` | Custom Hz `"25"` (3 ASCII bytes), tick times | **One A-4 beep every second**, steady, also when the song loops | Song settings: custom rate **25 Hz**, speed 5/5 |
| `ext-ch3.dmf` | System `0x42` and its channel order (FM1, FM2, FM3 OP1–OP4, FM4–6, PSG) | Four notes entering one by one — **C-4, E-4, G-4, C-5** — making a **C major chord**, then silence | System "Genesis (Ext. CH3)". The four notes sit in the **FM3 OP1, OP2, OP3, OP4** columns (not FM4–FM6 or PSG) |

## Results

Filled in from David's report.

| Probe | Opens | Sounds right | Looks right | VGM → rescomp |
|---|---|---|---|---|
| `fm-note` | | | | |
| `psg-note` | | | | |
| `dac-sample` | | | | |
| `custom-hz` | | | | |
| `ext-ch3` | | | | |
