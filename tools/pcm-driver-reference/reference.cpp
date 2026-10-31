// Plays a PCM driver probe script (tests/golden/pcm-drivers/*.txt) through SGDK's real XGM or XGM2 Z80 driver on an
// emulated Z80 and writes every YM2612 $2A (DAC data) and $2B (DAC enable) write the driver makes, with its time, for
// the tests that check Mod2Xgm's PCM driver models (docs/adr/0066-pcm-drivers-checked-against-sgdk.md).
//
//   reference <libmd-dir> <script.txt> <out.raw>
//
// <libmd-dir> holds drv_xgm.o and drv_xgm2.o, extracted from SGDK's lib/libmd.a: the driver bytes every SGDK game
// ships, in each object's .rodata section.
//
// Script lines (the C# side builds and parses the same text, PcmDriverScript):
//   driver xgm|xgm2              which driver; first line
//   sample <id> <length> <step>  sample <id> (1-based): byte i is (int8)(uint8)((i + 1) * step)
//   play <ch> <id> <priority> [half]   a PCM play command in the music, in the current frame
//   stop <ch> <priority>         a PCM stop command (id 0)
//   frame <count>                ends the current frame and waits <count> frames
//   # ...                        comment
//
// The emulated machine is what the drivers touch: 8 KB of Z80 RAM, the YM2612's four ports (with Timer A and its
// status flag; the busy flag always clear), the bank register and the 32 KB window on the 68000's memory (each access
// costs extra Z80 cycles, wait_cycles), the PSG port (ignored), and the Z80's VBlank interrupt once per NTSC frame.
// The 68000's side is done as SGDK's xgm.c / xgm2.c do it, instantly (the bus requests' few cycles are not modeled).
//
// Output: little-endian records of 8 bytes, one per $2A or $2B write: int32 time in master clocks from the VBlank
// of the song's frame 0 (negative before it), uint8 register, uint8 value, 2 bytes 0.
//
// This file is part of Mod2Xgm (MIT). It is a test tool only: it is never shipped and never run by the converter.

#define CHIPS_IMPL
#include "z80.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

namespace
{

constexpr int64_t master_per_z80 = 15;
constexpr int64_t master_per_ym_sample = 7 * 144;
constexpr int64_t master_per_frame = 262 * 3420;    // NTSC: 262 lines of 3420 master clocks
constexpr int64_t int_cycles = 228;                 // the VDP holds the Z80's INT for about one line
constexpr int64_t wait_cycles = 3;                  // average Z80 wait per 68000-bus access (Genesis Plus GX, measured)

constexpr size_t xgm_pcm_shift = 3;              // xgmtool: PCM commands 3 frames ahead on NTSC

constexpr uint32_t null_sample_addr = 0x010000;     // SGDK's smp_null: 256 zero bytes
constexpr uint32_t song_addr = 0x020000;            // the compiled song (.xgc), 256-aligned

[[noreturn]] void fail(const std::string &message)
{
	std::fprintf(stderr, "%s\n", message.c_str());
	std::exit(2);
}

// The .rodata section of a 32-bit big-endian ELF object: where SGDK's libmd.a keeps each driver's bytes.
std::vector<uint8_t> load_driver(const std::string &path)
{
	std::ifstream in(path, std::ios::binary);
	if (!in)
		fail("cannot open " + path);
	std::vector<uint8_t> elf((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
	auto u16 = [&](size_t o) { return uint32_t(elf.at(o) << 8 | elf.at(o + 1)); };
	auto u32 = [&](size_t o) { return u16(o) << 16 | u16(o + 2); };
	if (elf.size() < 52 || std::memcmp(elf.data(), "\x7f" "ELF", 4) != 0 || elf[4] != 1 || elf[5] != 2)
		fail(path + ": not a 32-bit big-endian ELF object");
	uint32_t shoff = u32(0x20), shentsize = u16(0x2E), shnum = u16(0x30), shstrndx = u16(0x32);
	uint32_t strtab = u32(shoff + shstrndx * shentsize + 0x10);
	for (uint32_t i = 0; i < shnum; i++)
	{
		size_t sh = shoff + i * shentsize;
		const char *name = reinterpret_cast<const char *>(&elf.at(strtab + u32(sh)));
		if (std::strcmp(name, ".rodata") == 0)
		{
			uint32_t offset = u32(sh + 0x10), size = u32(sh + 0x14);
			return std::vector<uint8_t>(elf.begin() + offset, elf.begin() + offset + size);
		}
	}
	fail(path + ": no .rodata section");
}

struct command
{
	enum kind_t { play, stop } kind;
	int channel, id, priority;
	bool half;
};

struct script
{
	bool xgm2 = false;
	std::vector<std::vector<int8_t>> samples; // index id - 1
	std::vector<std::vector<command>> frames; // the commands at the start of each frame
};

script parse(const char *path)
{
	std::ifstream in(path);
	if (!in)
		fail(std::string("cannot open ") + path);
	script s;
	std::vector<command> current;
	std::string line;
	int number = 0;
	bool has_driver = false;
	while (std::getline(in, line))
	{
		number++;
		if (!line.empty() && line.back() == '\r')
			line.pop_back();
		if (line.empty() || line[0] == '#')
			continue;
		std::istringstream f(line);
		std::string kind;
		f >> kind;
		auto bad = [&]() { fail(std::string(path) + ":" + std::to_string(number) + ": not a step: " + line); };
		if (kind == "driver")
		{
			std::string name;
			f >> name;
			if (name != "xgm" && name != "xgm2")
				bad();
			s.xgm2 = name == "xgm2";
			has_driver = true;
		}
		else if (kind == "sample")
		{
			int id, length, step;
			if (!(f >> id >> length >> step) || id != int(s.samples.size()) + 1 || length <= 0)
				bad();
			std::vector<int8_t> data(static_cast<size_t>(length));
			for (int i = 0; i < length; i++)
				data[size_t(i)] = int8_t(uint8_t((i + 1) * step));
			s.samples.push_back(data);
		}
		else if (kind == "play" || kind == "stop")
		{
			command c{};
			c.kind = kind == "play" ? command::play : command::stop;
			if (c.kind == command::play)
			{
				if (!(f >> c.channel >> c.id >> c.priority))
					bad();
				std::string half;
				c.half = (f >> half) && half == "half";
			}
			else if (!(f >> c.channel >> c.priority))
				bad();
			current.push_back(c);
		}
		else if (kind == "frame")
		{
			int count;
			if (!(f >> count) || count <= 0)
				bad();
			s.frames.push_back(current);
			current.clear();
			for (int i = 1; i < count; i++)
				s.frames.emplace_back();
		}
		else
			bad();
	}
	if (!has_driver || s.frames.empty())
		fail(std::string(path) + ": needs a driver line and at least one frame");
	return s;
}

uint32_t pad256(uint32_t n) { return (n + 255) & ~255u; }

struct machine
{
	z80_t cpu{};
	uint64_t pins = 0;
	uint8_t ram[0x2000] = {};
	std::vector<uint8_t> rom = std::vector<uint8_t>(0x100000, 0);
	uint32_t bank = 0;
	int64_t cycles = 0; // Z80 cycles, wait states included
	int64_t int_until = -1;

	// YM2612: the address latch of each part, Timer A, and the DAC writes
	int ym_addr[2] = {0, 0};
	int timer_a = 0, timer_a_count = 0, reg27 = 0;
	bool timer_a_flag = false;
	int64_t next_ym_sample = master_per_ym_sample;
	int64_t time_zero = 0;
	struct dac_write { int64_t master; uint8_t reg, value; };
	std::vector<dac_write> writes;

	// The writes as the output file has them, timed from time_zero.
	std::vector<uint8_t> records() const
	{
		std::vector<uint8_t> out;
		for (const dac_write &w : writes)
		{
			int32_t time = int32_t(w.master - time_zero);
			uint8_t record[8] = { uint8_t(time), uint8_t(time >> 8), uint8_t(time >> 16), uint8_t(time >> 24), w.reg, w.value, 0, 0 };
			out.insert(out.end(), record, record + 8);
		}
		return out;
	}

	uint8_t bus_read(uint32_t address)
	{
		address &= 0xFFFFFF;
		return address < rom.size() ? rom[address] : 0;
	}

	void ym_write(int port, uint8_t data)
	{
		int part = port >> 1;
		if ((port & 1) == 0)
		{
			ym_addr[part] = data;
			return;
		}
		int reg = ym_addr[part];
		if (part != 0)
			return;
		if (reg == 0x24)
			timer_a = (timer_a & 3) | (data << 2);
		else if (reg == 0x25)
			timer_a = (timer_a & ~3) | (data & 3);
		else if (reg == 0x27)
		{
			if ((data & 1) && !(reg27 & 1))
				timer_a_count = timer_a;
			if (data & 0x10)
				timer_a_flag = false;
			reg27 = data;
		}
		else if (reg == 0x2A || reg == 0x2B)
		{
			writes.push_back({ cycles * master_per_z80, uint8_t(reg), data });
		}
	}

	// Timer A counts once per YM sample while its load bit is set; on overflow it reloads and, if enabled, sets its flag.
	void ym_clock_to(int64_t master)
	{
		while (next_ym_sample <= master)
		{
			next_ym_sample += master_per_ym_sample;
			if (reg27 & 1)
			{
				if (++timer_a_count >= 1024)
				{
					timer_a_count = timer_a;
					if (reg27 & 4)
						timer_a_flag = true;
				}
			}
		}
	}

	uint8_t mem_read(uint16_t a)
	{
		if (a < 0x4000)
			return ram[a & 0x1FFF];
		if (a < 0x6000)
			return uint8_t((timer_a_flag ? 1 : 0)); // status: Timer A flag; never busy
		if (a >= 0x8000)
		{
			cycles += wait_cycles;
			return bus_read((bank << 15) | (a & 0x7FFF));
		}
		return 0xFF;
	}

	void mem_write(uint16_t a, uint8_t d)
	{
		if (a < 0x4000)
			ram[a & 0x1FFF] = d;
		else if (a < 0x6000)
			ym_write(a & 3, d);
		else if (a < 0x6100)
			bank = ((bank >> 1) | ((d & 1u) << 8)) & 0x1FF;
		else if (a >= 0x8000)
			cycles += wait_cycles; // a write to the 68000's side: nothing there the drivers write to
	}

	void step()
	{
		pins = z80_tick(&cpu, pins);
		cycles++;
		if (pins & Z80_MREQ)
		{
			uint16_t a = Z80_GET_ADDR(pins);
			if (pins & Z80_RD)
			{
				Z80_SET_DATA(pins, mem_read(a));
			}
			else if (pins & Z80_WR)
				mem_write(a, Z80_GET_DATA(pins));
		}
		else if ((pins & Z80_IORQ) && (pins & Z80_M1))
		{
			Z80_SET_DATA(pins, 0xFF); // interrupt acknowledge: IM 1 ignores the bus
		}
		if (cycles < int_until)
			pins |= Z80_INT;
		else
			pins &= ~Z80_INT;
		ym_clock_to(cycles * master_per_z80);
	}

	void run_until(int64_t z80_cycle)
	{
		while (cycles < z80_cycle)
			step();
	}
};

// SGDK's Z80_loadDriverInternal: clear RAM, copy the driver, reset the Z80; then wait for the driver's ready bit.
void load(machine &m, const std::vector<uint8_t> &driver)
{
	std::memset(m.ram, 0, sizeof m.ram);
	std::memcpy(m.ram, driver.data(), driver.size());
	m.pins = z80_init(&m.cpu);
	int64_t limit = m.cycles + 4'000'000;
	while ((m.ram[0x102] & 0x80) == 0)
	{
		if (m.cycles > limit)
			fail("the driver never set its ready bit");
		m.step();
	}
}

// The XGM (v1) song as rescomp compiles it (.xgc, the file without "XGM "): the sample id table, the samples and the
// music as xgmtool compiles it (docs/formats/xgm.txt).
std::vector<uint8_t> build_xgm(const script &s)
{
	std::vector<uint8_t> xgc(0x100, 0);
	std::vector<uint8_t> data;
	for (size_t i = 0; i < 63; i++)
	{
		uint32_t addr = 0xFFFF, len = 1;
		if (i < s.samples.size())
		{
			addr = uint32_t(data.size()) >> 8;
			len = pad256(uint32_t(s.samples[i].size())) >> 8;
			data.insert(data.end(), s.samples[i].begin(), s.samples[i].end());
			data.resize(pad256(uint32_t(data.size())), 0);
		}
		xgc[i * 4 + 0] = uint8_t(addr);
		xgc[i * 4 + 1] = uint8_t(addr >> 8);
		xgc[i * 4 + 2] = uint8_t(len);
		xgc[i * 4 + 3] = uint8_t(len >> 8);
	}
	xgc[0xFC] = uint8_t(data.size() >> 8);
	xgc[0xFD] = uint8_t(data.size() >> 16);
	xgc[0xFE] = 0; // version, as xgmtool writes it
	xgc.insert(xgc.end(), data.begin(), data.end());

	// As SGDK's xgmtool compiles it (tools/xgmtool/src/xgc.c): each frame is a length byte (itself included) and its
	// commands; 3 empty frames are put in front and every PCM command moves 3 frames earlier on NTSC, so the samples
	// sound with the rest of their frame after the driver's buffer delay. Command bytes are doubled, for the driver's
	// 16-bit jump table (xgc.c, XGC_asByteArray). The probes hold only PCM commands, so XGC
	// frame j has song frame j's commands, and 3 more frames follow the song's, the last ending with $7F.
	std::vector<uint8_t> music;
	size_t xgc_frames = s.frames.size() + xgm_pcm_shift;
	for (size_t j = 0; j < xgc_frames; j++)
	{
		std::vector<uint8_t> frame;
		if (j < s.frames.size())
		{
			for (const command &c : s.frames[j])
			{
				frame.push_back(uint8_t((0x50 | (c.priority & 0xC) | (c.channel & 3)) << 1));
				frame.push_back(uint8_t(c.kind == command::play ? c.id : 0));
			}
		}
		if (j + 1 == xgc_frames)
			frame.push_back(0x7F << 1);
		music.push_back(uint8_t(frame.size() + 1));
		music.insert(music.end(), frame.begin(), frame.end());
	}
	uint32_t mlen = uint32_t(music.size());
	for (int i = 0; i < 4; i++)
		xgc.push_back(uint8_t(mlen >> (8 * i)));
	xgc.insert(xgc.end(), music.begin(), music.end());
	return xgc;
}

// SGDK's XGM_loadDriver and XGM_startPlay: the null sample, the sample id table with absolute addresses, the music's
// address and the play command.
void start_xgm(machine &m, const std::vector<uint8_t> &xgc)
{
	uint8_t *id = m.ram + 0x1C00;
	id[0] = uint8_t(null_sample_addr >> 8);
	id[1] = uint8_t(null_sample_addr >> 16);
	id[2] = 1;
	id[3] = 0;
	for (int i = 0; i < 0x3F; i++)
	{
		uint32_t addr = uint32_t(xgc[i * 4]) << 8 | uint32_t(xgc[i * 4 + 1]) << 16;
		addr = addr == 0xFFFF00 ? null_sample_addr : addr + song_addr + 0x100;
		id[4 + i * 4 + 0] = uint8_t(addr >> 8);
		id[4 + i * 4 + 1] = uint8_t(addr >> 16);
		id[4 + i * 4 + 2] = xgc[i * 4 + 2];
		id[4 + i * 4 + 3] = xgc[i * 4 + 3];
	}
	uint32_t music = song_addr + 0x100 + (uint32_t(xgc[0xFC]) << 8) + (uint32_t(xgc[0xFD]) << 16) + 4;
	for (int i = 0; i < 4; i++)
		m.ram[0x104 + i] = uint8_t(music >> (8 * i));
	m.ram[0x100] = uint8_t((m.ram[0x100] & 0x0F) | 0x40);
	m.ram[0x104 + 0x0F] = 0; // PENDING_FRM
}

// The packing the XGM2 driver unpacks its streams from (unpackCmds in drv_xgm2_fct.i80; SGDK's xgm2tool XGCPacker):
// chunks of at most 256 unpacked bytes, each a run of blocks and a $00 header; a block header holds a literal count
// (bits 7-5) and a match length (bits 4-0, below 2 = none). This packer only writes literals, up to 7 a block, and cuts
// a chunk at a frame's end once it holds 48 bytes, so a command never spans two chunks.
std::vector<uint8_t> pack(const std::vector<std::vector<uint8_t>> &frames)
{
	std::vector<uint8_t> out, chunk;
	auto flush = [&]() {
		for (size_t i = 0; i < chunk.size(); i += 7)
		{
			size_t n = std::min<size_t>(7, chunk.size() - i);
			out.push_back(uint8_t(n << 5));
			out.insert(out.end(), chunk.begin() + long(i), chunk.begin() + long(i + n));
		}
		out.push_back(0x00);
		chunk.clear();
	};
	for (const auto &frame : frames)
	{
		if (!chunk.empty() && chunk.size() + frame.size() > 48)
			flush();
		chunk.insert(chunk.end(), frame.begin(), frame.end());
	}
	flush();
	return out;
}

// The XGM2 song as compiled (.xgc, the file without "XGM2"): settings, the sample id table (sample n's address in
// entry n - 1, then an end entry), the samples, the FM stream with the PCM commands and one wait per frame, and a PSG
// stream that only waits, both packed (docs/formats/xgm.md).
std::vector<uint8_t> build_xgm2(const script &s)
{
	std::vector<uint8_t> xgc(0x100, 0xFF);
	std::vector<uint8_t> data;
	for (size_t i = 0; i <= s.samples.size(); i++)
	{
		uint32_t addr = uint32_t(data.size()) >> 8;
		xgc[8 + i * 2] = uint8_t(addr);
		xgc[8 + i * 2 + 1] = uint8_t(addr >> 8);
		if (i < s.samples.size())
		{
			data.insert(data.end(), s.samples[i].begin(), s.samples[i].end());
			data.resize(pad256(uint32_t(data.size())), 0);
		}
	}

	std::vector<std::vector<uint8_t>> fm_frames, psg_frames;
	for (const auto &frame : s.frames)
	{
		std::vector<uint8_t> fm_frame;
		for (const command &c : frame)
		{
			fm_frame.push_back(uint8_t(0x10 | (c.priority >= 15 ? 8 : 0) | (c.half ? 4 : 0) | (c.channel & 3)));
			fm_frame.push_back(uint8_t(c.kind == command::play ? c.id : 0));
		}
		fm_frame.push_back(0x00); // wait 1 frame
		fm_frames.push_back(fm_frame);
		psg_frames.push_back({ 0x00 });
	}
	fm_frames.push_back({ 0xFF, 0xFF, 0xFF, 0xFF }); // end, no loop
	psg_frames.push_back({ 0x0F, 0xFF, 0xFF, 0xFF });
	std::vector<uint8_t> fm = pack(fm_frames), psg = pack(psg_frames);
	fm.resize(pad256(uint32_t(fm.size())), 0);
	psg.resize(pad256(uint32_t(psg.size())), 0);

	xgc[0] = 0x10; // version
	xgc[1] = 0;    // NTSC, single track
	xgc[2] = uint8_t(data.size() >> 8);
	xgc[3] = uint8_t(data.size() >> 16);
	xgc[4] = uint8_t(fm.size() >> 8);
	xgc[5] = uint8_t(fm.size() >> 16);
	xgc[6] = uint8_t(psg.size() >> 8);
	xgc[7] = uint8_t(psg.size() >> 16);
	xgc.insert(xgc.end(), data.begin(), data.end());
	xgc.insert(xgc.end(), fm.begin(), fm.end());
	xgc.insert(xgc.end(), psg.begin(), psg.end());
	return xgc;
}

// SGDK's XGM2_loadDriver, XGM2_load and XGM2_playTrack(0): no loop, tempo 1 frame per VBlank, the sample id table
// with absolute addresses, the streams' addresses and the play command.
void start_xgm2(machine &m, const std::vector<uint8_t> &xgc)
{
	m.ram[0x104 + 0x04] = 0;    // loop: none
	m.ram[0x104 + 0x0A] = 0;    // tempo: 256 / 256 = 1 frame per VBlank (NTSC)
	m.ram[0x104 + 0x0B] = 1;
	uint32_t sample_off = song_addr + 0x100;
	for (int i = 0; i < 124; i++)
	{
		uint32_t addr = uint32_t(xgc[8 + i * 2]) | uint32_t(xgc[8 + i * 2 + 1]) << 8;
		if (addr == 0xFFFF)
			break;
		addr = (addr << 8) + sample_off;
		m.ram[0x1C00 + i * 2] = uint8_t(addr >> 8);
		m.ram[0x1C00 + i * 2 + 1] = uint8_t(addr >> 16);
	}
	uint32_t slen = uint32_t(xgc[2]) << 8 | uint32_t(xgc[3]) << 16;
	uint32_t fmlen = uint32_t(xgc[4]) << 8 | uint32_t(xgc[5]) << 16;
	uint32_t fm = song_addr + 0x100 + slen, psg = fm + fmlen;
	m.ram[0x104 + 0] = uint8_t(fm >> 8);
	m.ram[0x104 + 1] = uint8_t(fm >> 16);
	m.ram[0x104 + 2] = uint8_t(psg >> 8);
	m.ram[0x104 + 3] = uint8_t(psg >> 16);
	m.ram[0x100] = uint8_t((m.ram[0x100] & ~0x28) | 0x10); // XGM2_COM_START_PLAY; resume (b3) and pause (b5) cleared
}

void write_raw(const char *path, const std::vector<uint8_t> &data)
{
	std::FILE *f = std::fopen(path, "wb");
	if (f == nullptr)
		fail(std::string("cannot write ") + path);
	std::fwrite(data.data(), 1, data.size(), f);
	std::fclose(f);
}

}

int main(int argc, char *argv[])
{
	if (argc != 4)
	{
		std::fprintf(stderr, "usage: reference <libmd-dir> <script.txt> <out.raw>\n");
		return 1;
	}
	script s = parse(argv[2]);
	std::vector<uint8_t> driver = load_driver(std::string(argv[1]) + (s.xgm2 ? "/drv_xgm2.o" : "/drv_xgm.o"));
	if (driver.size() > 0x2000)
		fail("driver larger than Z80 RAM");

	static machine m;
	std::vector<uint8_t> xgc = s.xgm2 ? build_xgm2(s) : build_xgm(s);
	if (song_addr + xgc.size() > m.rom.size())
		fail("song too large");
	std::memcpy(&m.rom[song_addr], xgc.data(), xgc.size());

	load(m, driver);
	if (s.xgm2)
		start_xgm2(m, xgc);
	else
		start_xgm(m, xgc);

	// The first VBlank comes one frame after the play command. Time zero is the VBlank of the song's frame 0: the
	// first for XGM2, the fourth for XGM, whose compiled music starts with xgmtool's 3 empty frames.
	int64_t frame_start = m.cycles * master_per_z80 + master_per_frame;
	m.time_zero = frame_start + (s.xgm2 ? 0 : int64_t(xgm_pcm_shift) * master_per_frame);
	size_t frames = s.frames.size() + 12; // a few frames more, for the sounds' and the DAC's ends
	for (size_t f = 0; f < frames; f++)
	{
		m.run_until(frame_start / master_per_z80);
		m.int_until = m.cycles + int_cycles;
		if (!s.xgm2)
		{
			// XGM_nextXFrame from SGDK's VInt: wait while the Z80 modifies PENDING_FRM, then add a frame.
			while (m.ram[0x104 + 0x0E] != 0)
				m.run_until(m.cycles + 20);
			m.ram[0x104 + 0x0F]++;
		}
		if (std::getenv("PCMREF_DEBUG"))
			std::fprintf(stderr, "frame %zu: cmd %02X status %02X pending %02X pc %04X pcm0 %02X %02X%02X %02X%02X bank %03X xgm %02X%02X%02X\n", f, m.ram[0x100], m.ram[0x102], m.ram[0x104 + 0x0F], unsigned(m.cpu.pc), m.ram[0x114], m.ram[0x117], m.ram[0x116], m.ram[0x119], m.ram[0x118], m.bank, m.ram[0x136], m.ram[0x135], m.ram[0x134]);
		frame_start += master_per_frame;
	}
	m.run_until(frame_start / master_per_z80);
	write_raw(argv[3], m.records());
	return 0;
}
