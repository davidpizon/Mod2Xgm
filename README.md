# Mod2Xgm

## What is this?

A long time ago, people made music on computers like the Commodore Amiga and saved it in **MOD files**.
A MOD file is like a recipe for a song. It holds two things:

- **Little recordings of sounds**, called *samples* — a drum hit, a guitar note, a voice saying "yeah!"
- **The sheet music** — which sample to play, how high or low, and exactly when.

The **Sega Genesis** (called the **Mega Drive** outside North America) is a video game console from the same
time. It makes music in a different way. Most of its sounds come from a **sound chip** that *builds* sounds out
of math, a bit like an electronic keyboard. It has only a little room for recorded sounds, and it can play just
a few of them at once.

**Mod2Xgm is a translator.** It reads a MOD song and rewrites it so a Sega Genesis can play it. When the Genesis
can't play a sound exactly, Mod2Xgm picks the closest sound it can make, so the song sounds as much like the
original as possible.

It can save the result in three kinds of files:

| File | What it's for |
|---|---|
| `.dmf` | Opens in **DefleMask**, a music program, so you can listen to it and change it |
| `.xgm` | Plays on a real Genesis in games made with **SGDK** (the XGM sound driver) |
| `.xgm2` | The same, for SGDK's newer XGM2 sound driver |

> **Project status:** early development. The command line below is the planned design
> ([`docs/cli.md`](docs/cli.md)); the conversion itself is still being built
> ([`docs/implementation-plan.md`](docs/implementation-plan.md)).

## Using the command line

### Get it

Release downloads aren't available yet. To build it yourself, install the
[.NET 10 SDK](https://dotnet.microsoft.com/download), then:

```
git clone https://github.com/davidpizon/Mod2Xgm
cd Mod2Xgm
dotnet build
dotnet run --project src/Mod2Xgm.csproj -- song.mod
```

Everything after `--` is passed to Mod2Xgm. The examples below use the program name `mod2xgm`.

### Convert a song

```
mod2xgm song.mod                    # makes song.dmf next to song.mod
mod2xgm song.mod --format xgm       # makes song.xgm  (SGDK XGM driver)
mod2xgm song.mod --format xgm2      # makes song.xgm2 (SGDK XGM2 driver)
mod2xgm song.mod -o music/level1.xgm2 --format xgm2
```

With `--format xgm2`, Mod2Xgm reminds you:
`NOTE: SGDK expects XGM2 files to use the .XGM file extension.` Rename the file before adding it to an SGDK project.

### What it prints

Every conversion first prints the information stored in the MOD file: its title, format, length and tempo, and
a table of **all** its samples with their names (composers often hide messages in sample names). After
converting, it lists the instruments and samples in the new file.

Anything that couldn't be converted exactly, such as an effect the Genesis can't do or a sample that had to be
shortened, is printed as a `WRN` line. Use `--info` to see all of this without writing a file:

```
mod2xgm song.mod --format xgm2 --info
```

### Common options

| Option | What it does |
|---|---|
| `-f, --format dmf\|xgm\|xgm2` | Output type (default `dmf`) |
| `-o, --output <path>` | Where to write the file (default: next to the MOD) |
| `--ignore-samples 3,7,12-14` | Leave these samples out (numbers start at 1; `0x1F` style also works) |
| `--require-fm 4,9-11` | These samples must be played by FM synthesis |
| `--require-pcm 1,2` | These samples must stay recorded sound (PCM) |
| `--quality fast\|normal\|best` | How hard to try to match the original sound (default `normal`, up to about 5 minutes) |
| `--max-pcm-bytes 128K` | Space allowed for recorded sounds in XGM/XGM2 (default 128K) |
| `--reserve-sfx-channels <n>` | Keep `n` sample channels free for your game's sound effects |
| `--input-region pal\|ntsc` | Which Amiga the MOD was made on; tunes the notes (default `pal`) |
| `--output-region pal\|ntsc` | Which Genesis the music is made for (default `pal`) |
| `--player protracker\|ust` | Whose rules to read the MOD by (default: `ust` for 15-sample files, `protracker` otherwise) |
| `--info` | Show the report only; don't write a file |
| `--force` | Overwrite an existing output file |
| `-v, --verbose` | Print extra detail |
| `-h, --help` | Show help |

The full list and every detail are in [`docs/cli.md`](docs/cli.md).

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Wrong or missing arguments |
| 2 | MOD file not found or unreadable |
| 3 | Not a valid or supported MOD (4-channel ProTracker and 15-sample Soundtracker are supported) |
| 4 | Output file exists (use `--force`) or can't be written |
| 5 | Unexpected error |
| 6 | The song doesn't fit: `--no-consolidate` was given, or the `--require-pcm` samples alone are too big |

## For developers

Start with [`AGENTS.md`](AGENTS.md) and [`docs/`](docs/README.md). Changes go through pull requests; see the
Git workflow in AGENTS.md.

## License

[MIT](LICENSE)
