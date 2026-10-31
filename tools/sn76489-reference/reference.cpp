// Plays an SN76489 probe script (tests/golden/sn76489/*.txt) through MAME's Sega PSG and Nuked-PSG and writes what
// each chip emulator outputs, for the tests that check Mod2Xgm's C# port (docs/adr/0057-sn76489-model.md).
//
//   reference <script.txt> <mame-out.raw> <nuked-out.raw>
//
// Script lines: "w value" (hex) writes a byte to the PSG port; "s count" (decimal) plays that many samples; "#" comments.
// A sample is 16 PSG clocks. Both outputs are raw little-endian int32s, one per sample:
//   mame  - MAME's segapsg_device (src/devices/sound/sn76496.cpp, built against shim/emu.h): its stream runs at
//           clock / 2 and the chip steps every 8th stream sample, so each output is the stream sample right after a
//           step. Writes land between steps, exactly as the port must reproduce it.
//   nuked - Nuked-PSG (the YM7101 VDP's PSG) clocked at the PSG clock, read after every 16th clock, x 8192 as its
//           YMPSG_Generate does. Its writes take time (see write_clocks), MAME's don't, so each run of writes is
//           scheduled to end on the sample boundary where the script puts it.
//
// This file is part of Mod2Xgm (MIT). It is a test tool only: it is never shipped and never run by the converter.

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <limits>
#include <sstream>
#include <string>
#include <vector>

#include "emu.h"
#include "sn76496.h"

extern "C" {
#include "ympsg.h"
}

namespace
{

constexpr uint32_t ntsc_clock = 3579545;
constexpr int clocks_per_sample = 16;
constexpr int mame_stream_per_sample = 8; // MAME's stream runs at clock / 2

struct step
{
	int value;
	int samples; // > 0 for a run of samples, 0 for a write
};

std::vector<step> parse(const char *path)
{
	std::ifstream in(path);
	if (!in)
	{
		std::fprintf(stderr, "cannot open %s\n", path);
		std::exit(2);
	}

	std::vector<step> steps;
	std::string line;
	int number = 0;
	while (std::getline(in, line))
	{
		number++;
		if (!line.empty() && line.back() == '\r')
			line.pop_back();
		if (line.empty() || line[0] == '#')
			continue;

		std::istringstream fields(line);
		std::string kind;
		fields >> kind;
		step s{};
		if (kind == "w")
		{
			fields >> std::hex >> s.value;
			if (s.value < 0 || s.value > 0xff)
				fields.setstate(std::ios::failbit);
		}
		else if (kind == "s")
		{
			fields >> std::dec >> s.samples;
			if (s.samples <= 0)
				fields.setstate(std::ios::failbit);
		}
		else
		{
			fields.setstate(std::ios::failbit);
		}

		if (!fields)
		{
			std::fprintf(stderr, "%s:%d: not a step: %s\n", path, number, line.c_str());
			std::exit(2);
		}
		steps.push_back(s);
	}
	return steps;
}

void write_raw(const char *path, const std::vector<int32_t> &data)
{
	std::FILE *out = std::fopen(path, "wb");
	if (out == nullptr)
	{
		std::fprintf(stderr, "cannot write %s\n", path);
		std::exit(2);
	}
	for (int32_t value : data)
	{
		uint32_t u = uint32_t(value);
		uint8_t bytes[4] = { uint8_t(u), uint8_t(u >> 8), uint8_t(u >> 16), uint8_t(u >> 24) };
		std::fwrite(bytes, 1, 4, out);
	}
	std::fclose(out);
}

machine_config config;

// MAME's Sega PSG with its start and stream update reachable from here.
class probe_psg : public segapsg_device
{
public:
	probe_psg() : segapsg_device(config, "psg", nullptr, ntsc_clock) {}
	void start() { device_start(); }
	void run(sound_stream &stream) { sound_stream_update(stream); }
};

std::vector<int32_t> play_mame(const std::vector<step> &steps)
{
	probe_psg chip;
	chip.start();

	std::vector<int32_t> output;
	for (const step &s : steps)
	{
		if (s.samples == 0)
		{
			chip.write(uint8_t(s.value));
			continue;
		}
		sound_stream stream(s.samples * mame_stream_per_sample);
		chip.run(stream);
		for (int i = 0; i < s.samples; i++)
			output.push_back(stream.at(i * mame_stream_per_sample + mame_stream_per_sample - 1));
	}
	return output;
}

struct nuked_event
{
	long long clock; // when (PSG clocks from the first sample; negative before it)
	int data;
};

// Clocks a write occupies: the chip latches the byte on the next clock and applies it on that clock or the one
// after, so a second byte any sooner would overwrite the first.
constexpr int write_clocks = 2;

// Schedules each run of writes to end on the sample boundary where the script makes it; the writes before the first
// sample start at negative clocks. A run that can't fit in the samples before it starts once the previous run is done.
std::vector<nuked_event> schedule(const std::vector<step> &steps, long long &first_clock)
{
	std::vector<nuked_event> events;
	long long boundary = 0;
	long long previous_end = std::numeric_limits<long long>::min();
	first_clock = 0;
	size_t index = 0;
	while (index < steps.size())
	{
		if (steps[index].samples > 0)
		{
			boundary += (long long)clocks_per_sample * steps[index].samples;
			index++;
			continue;
		}

		size_t burst_end = index;
		while (burst_end < steps.size() && steps[burst_end].samples == 0)
			burst_end++;

		long long clock = std::max(boundary - (long long)write_clocks * (long long)(burst_end - index), previous_end);
		first_clock = std::min(first_clock, clock);
		for (; index < burst_end; index++)
		{
			events.push_back({ clock, steps[index].value });
			clock += write_clocks;
		}
		previous_end = clock;
	}
	return events;
}

std::vector<int32_t> play_nuked(const std::vector<step> &steps)
{
	size_t samples = 0;
	for (const step &s : steps)
		samples += size_t(s.samples);

	long long first_clock;
	std::vector<nuked_event> events = schedule(steps, first_clock);

	// the chip masks every write until its reset has worked through (about 30 clocks after YMPSG_Init), so the
	// pre-roll starts well before the first write
	const long long settle_clocks = 1024;
	first_clock -= settle_clocks;

	static ympsg_t chip; // large (its write buffer), so not on the stack
	YMPSG_Init(&chip);

	std::vector<int32_t> output(samples, 0);
	size_t next = 0;
	for (long long clock = first_clock; clock < (long long)clocks_per_sample * (long long)samples; clock++)
	{
		while (next < events.size() && events[next].clock == clock)
		{
			YMPSG_Write(&chip, uint8_t(events[next].data));
			next++;
		}

		YMPSG_Clock(&chip);
		if (clock >= 0 && (clock + 1) % clocks_per_sample == 0)
			output[size_t(clock / clocks_per_sample)] = int32_t(YMPSG_GetOutput(&chip) * 8192.f);
	}
	return output;
}

}

int main(int argc, char *argv[])
{
	if (argc != 4)
	{
		std::fprintf(stderr, "usage: reference <script.txt> <mame-out.raw> <nuked-out.raw>\n");
		return 1;
	}

	std::vector<step> steps = parse(argv[1]);
	write_raw(argv[2], play_mame(steps));
	write_raw(argv[3], play_nuked(steps));
	return 0;
}
