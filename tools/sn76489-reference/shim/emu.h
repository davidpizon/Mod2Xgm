// The least of MAME's device framework that src/devices/sound/sn76496.cpp needs to compile unchanged, for
// tools/sn76489-reference (docs/adr/0057-sn76489-model.md). Nothing here is MAME's code: the stream keeps the integers
// the chip puts into it, and timers, save states and the READY line do nothing.
//
// This file is part of Mod2Xgm (MIT). It is a test tool only: it is never shipped and never run by the converter.

#ifndef MOD2XGM_EMU_SHIM_H
#define MOD2XGM_EMU_SHIM_H

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <vector>

using u8 = uint8_t;
using s32 = int32_t;
using u32 = uint32_t;

#define ATTR_COLD
#define BIT(x, n) (((x) >> (n)) & 1)
#define NAME(x) x, #x
#define FUNC(x) &x, #x
#define TIMER_CALLBACK_MEMBER(name) void name(s32 param)
#define DECLARE_DEVICE_TYPE(Type, Class) class Class; extern const char *const Type;
#define DEFINE_DEVICE_TYPE(Type, Class, Short, Full) const char *const Type = Short;

enum { CLEAR_LINE = 0, ASSERT_LINE = 1 };

using device_type = const char *;

struct machine_config
{
};

struct attotime
{
	static const attotime zero;
	static attotime from_hz(double) { return attotime(); }
};
inline const attotime attotime::zero{};

struct emu_timer
{
	void adjust(const attotime &) {}
};

[[noreturn]] inline void fatalerror(const char *message, ...)
{
	std::fputs(message, stderr);
	std::exit(3);
}

class device_t;

// The READY line: never connected.
class devcb_write_line
{
public:
	explicit devcb_write_line(device_t &) {}
	int bind() { return 0; }
	bool isunset() const { return true; }
	void operator()(int) {}
};

// Keeps the integers the chip writes, one per stream sample; the tool reads them back.
class sound_stream
{
public:
	explicit sound_stream(int samples) : m_samples(samples), m_data(size_t(samples), 0) {}
	int samples() const { return m_samples; }
	void put_int(int, int index, int32_t value, int32_t) { m_data[size_t(index)] = value; }
	int32_t at(int index) const { return m_data[size_t(index)]; }
	void update() {}
	void set_sample_rate(uint32_t) {}

private:
	int m_samples;
	std::vector<int32_t> m_data;
};

class device_t
{
public:
	device_t(const machine_config &, device_type, const char *, device_t *, uint32_t clock) : m_clock(clock) {}
	virtual ~device_t() = default;
	uint32_t clock() const { return m_clock; }

protected:
	virtual void device_start() {}
	virtual void device_clock_changed() {}
	template <typename T> void save_item(T &, const char *) {}
	template <typename F, typename T> emu_timer *timer_alloc(F, const char *, T *) { return &m_timer; }
	void logerror(const char *, ...) {}

private:
	uint32_t m_clock;
	emu_timer m_timer;
};

class device_sound_interface
{
public:
	device_sound_interface(const machine_config &, device_t &) {}
	virtual ~device_sound_interface() = default;

protected:
	virtual void sound_stream_update(sound_stream &stream) = 0;
	sound_stream *stream_alloc(int, int, int) { return &m_stream; }

private:
	sound_stream m_stream{ 0 };
};

#endif
