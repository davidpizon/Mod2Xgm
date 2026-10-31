// Plays a YM3438 probe script (tests/golden/ym3438/*.txt) through ymfm and Nuked-OPN2 and writes what each chip
// emulator outputs, for the tests that check Mod2Xgm's C# port (docs/adr/0056-ym3438-model.md).
//
//   reference <script.txt> <ymfm-out.raw> <nuked-out.raw>
//
// Script lines: "w part reg value" (hex) writes a register; "s count" (decimal) plays that many samples; "#" comments.
// Both outputs are raw little-endian int32 pairs (left, right), one per sample:
//   ymfm  - ymfm::ym3438::generate, exactly as the port must reproduce it.
//   nuked - Nuked-OPN2 (YM3438 mode) with the MOL/MOR pins summed over the 24 internal clocks of each sample.
//           Nuked's writes take time (see write_clocks), ymfm's don't, so each run of writes is scheduled to end on
//           the sample boundary where the script puts it.
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

#include "ymfm_opn.h"

extern "C" {
#include "ym3438.h"
}

namespace
{

struct step
{
	int part;
	int reg;
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
			fields >> std::hex >> s.part >> s.reg >> s.value;
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

std::vector<int32_t> play_ymfm(const std::vector<step> &steps)
{
	ymfm::ymfm_interface intf;
	ymfm::ym3438 chip(intf);
	chip.reset();

	std::vector<int32_t> output;
	for (const step &s : steps)
	{
		if (s.samples == 0)
		{
			chip.write(s.part * 2, uint8_t(s.reg));
			chip.write(s.part * 2 + 1, uint8_t(s.value));
			continue;
		}
		for (int i = 0; i < s.samples; i++)
		{
			ymfm::ym3438::output_data sample;
			chip.generate(&sample, 1);
			output.push_back(sample.data[0]);
			output.push_back(sample.data[1]);
		}
	}
	return output;
}

// Nuked-OPN2 clocked one internal clock (6 master clocks) at a time; 24 clocks make one sample.
struct nuked_event
{
	long long clock; // when (internal clocks from the first sample; negative before it)
	int port;
	int data;
};

// Clocks a write occupies: the address, one clock, the data, then one clock for a system register ($21-$2F) or 24
// for the key on/off and the channel and operator registers, which wait for their slot (every slot comes round
// within 24 clocks).
int write_clocks(const step &s)
{
	return 1 + ((s.part == 0 && s.reg < 0x30 && s.reg != 0x28) ? 1 : 24);
}

// Schedules each run of writes to end on the sample boundary where the script makes it, so a key on lands where ymfm
// has it; the writes before the first sample start at negative clocks. A run that can't fit in the samples before
// it starts once the previous run is done.
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
			boundary += 24LL * steps[index].samples;
			index++;
			continue;
		}

		size_t burst_end = index;
		long long cost = 0;
		while (burst_end < steps.size() && steps[burst_end].samples == 0)
			cost += write_clocks(steps[burst_end++]);

		long long clock = std::max(boundary - cost, previous_end);
		first_clock = std::min(first_clock, clock);
		for (; index < burst_end; index++)
		{
			const step &s = steps[index];
			events.push_back({ clock, s.part * 2, s.reg });
			events.push_back({ clock + 1, s.part * 2 + 1, s.value });
			clock += write_clocks(s);
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

	// start the pre-roll a whole period of the envelope timer (12 bits, stepped every third sample) before the first
	// sample, plus two samples: the timer then wraps (from 4095 to 1; it skips 0, docs/adr/0063-ym3438-chip-differences.md)
	// on the first sample, as the port's counter leaves 0, and the two count together from there, with Nuked's envelope
	// steps on the same samples as ymfm's (measured in plan T7.7: plain decays at every rate then match sample for sample)
	const long long eg_period = 4096LL * 3 * 24;
	const long long eg_offset = 2 * 24;
	first_clock = (first_clock - eg_period + 1) / eg_period * eg_period + eg_offset;

	ym3438_t chip;
	OPN2_SetChipType(0);
	OPN2_Reset(&chip);

	std::vector<int32_t> output(samples * 2, 0);
	size_t next = 0;
	for (long long clock = first_clock; clock < 24LL * (long long)samples; clock++)
	{
		while (next < events.size() && events[next].clock == clock)
		{
			OPN2_Write(&chip, events[next].port, uint8_t(events[next].data));
			next++;
		}

		Bit16s pins[2];
		OPN2_Clock(&chip, pins);
		if (clock >= 0)
		{
			output[size_t(clock / 24) * 2] += pins[0];
			output[size_t(clock / 24) * 2 + 1] += pins[1];
		}
	}
	return output;
}

}

int main(int argc, char *argv[])
{
	if (argc != 4)
	{
		std::fprintf(stderr, "usage: reference <script.txt> <ymfm-out.raw> <nuked-out.raw>\n");
		return 1;
	}

	std::vector<step> steps = parse(argv[1]);
	write_raw(argv[2], play_ymfm(steps));
	write_raw(argv[3], play_nuked(steps));
	return 0;
}
