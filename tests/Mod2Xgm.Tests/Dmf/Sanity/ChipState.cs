namespace Mod2Xgm.Tests.Dmf.Sanity;

/// <summary>
/// One thing a write changed: a register, a channel's key, a PSG tone or volume, the noise register, or what a PCM
/// channel plays. <see cref="IsEvent"/> marks the ones whose every occurrence matters in order (a key going down and up
/// again within a frame retriggers the note; every noise write restarts the noise; every play starts a sample).
/// </summary>
/// <param name="Key">What changed, e.g. <c>FM0:A4</c>, <c>KEY 0</c>, <c>PSG tone 2</c>, <c>PCM 0</c>.</param>
/// <param name="Before">Its value before (−1: never set).</param>
/// <param name="After">Its value after.</param>
/// <param name="IsEvent">True when the change is a sequence item, not just a new value.</param>
internal readonly record struct ChipChange(string Key, int Before, int After, bool IsEvent);

/// <summary>
/// The sound chips' state as the sanity check compares it (docs/testing.md#dmf-sanity-check): every YM2612 register
/// that shapes the sound, each FM channel's key bits, each PSG channel's tone and attenuation, the noise register and
/// the sample each PCM channel last started. Registers the PCM players own (<c>$2A</c>, <c>$2B</c>), the timers and
/// unused addresses are left out; so is everything but the CH3 mode bit of <c>$27</c>.
/// </summary>
internal sealed class ChipState
{
    private readonly Dictionary<string, int> _values = new(StringComparer.Ordinal);
    private int _latchChannel;
    private bool _latchVolume;

    /// <summary>The current values, by key.</summary>
    public IReadOnlyDictionary<string, int> Values => _values;

    /// <summary>The category a change key belongs to, for the report.</summary>
    public static string Category(string key)
    {
        if (key.StartsWith("FM", StringComparison.Ordinal))
        {
            int register = Convert.ToInt32(key[4..], 16);
            return register switch
            {
                >= 0xA0 and <= 0xAE => "FM frequency",
                >= 0x40 and <= 0x4F => "FM total level",
                >= 0x30 and <= 0x9F => "FM operator",
                >= 0xB0 and <= 0xB2 => "FM algorithm/feedback",
                >= 0xB4 and <= 0xB6 => "FM pan/LFO sensitivity",
                _ => "FM other",
            };
        }

        return key switch
        {
            _ when key.StartsWith("KEY", StringComparison.Ordinal) => "FM key",
            "LFO" => "LFO",
            "CH3 mode" => "CH3 mode",
            _ when key.StartsWith("PSG tone", StringComparison.Ordinal) => "PSG tone",
            _ when key.StartsWith("PSG volume", StringComparison.Ordinal) => "PSG volume",
            "PSG noise" => "PSG noise",
            _ => "PCM",
        };
    }

    /// <summary>Applies <paramref name="write"/>, adding what it changed to <paramref name="changes"/>.</summary>
    public void Apply(ChipWrite write, List<ChipChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        switch (write.Target)
        {
            case ChipTarget.Fm:
                ApplyFm(write.Part, write.Register, write.Value, changes);
                break;
            case ChipTarget.Psg:
                ApplyPsg(write.Value, changes);
                break;
            case ChipTarget.PcmPlay:
                Set($"PCM {write.Channel}", write.Sample, isEvent: true, changes);
                break;
            default:
                Set($"PCM {write.Channel}", -1, isEvent: true, changes);
                break;
        }
    }

    private void ApplyFm(int part, int register, int value, List<ChipChange> changes)
    {
        switch (register)
        {
            case 0x22:
                Set("LFO", value, isEvent: false, changes);
                return;
            case 0x27:
                Set("CH3 mode", value & 0x40, isEvent: false, changes);
                return;
            case 0x28:
                int channel = value & 0x07;
                if (channel is not (3 or 7))
                {
                    // A key write that changes nothing is no event: DefleMask lets keys up that are already up.
                    int before = _values.GetValueOrDefault($"KEY {channel}", -1);
                    if (before != (value & 0xF0))
                    {
                        Set($"KEY {channel}", value & 0xF0, isEvent: true, changes);
                    }
                }

                return;
        }

        bool slot = register is >= 0x30 and <= 0x9F && (register & 0x03) != 3;
        bool channelRegister = register is (>= 0xA0 and <= 0xA6) or (>= 0xA8 and <= 0xAE) or (>= 0xB0 and <= 0xB6)
            && (register & 0x03) != 3;
        if (slot || channelRegister)
        {
            Set($"FM{part}:{register:X2}", value, isEvent: false, changes);
        }
    }

    private void ApplyPsg(int value, List<ChipChange> changes)
    {
        if ((value & 0x80) != 0)
        {
            _latchChannel = (value >> 5) & 0x03;
            _latchVolume = (value & 0x10) != 0;
            if (_latchVolume)
            {
                Set($"PSG volume {_latchChannel}", value & 0x0F, isEvent: false, changes);
            }
            else if (_latchChannel == 3)
            {
                Set("PSG noise", value & 0x07, isEvent: true, changes);
            }
            else
            {
                int tone = Math.Max(0, _values.GetValueOrDefault($"PSG tone {_latchChannel}", 0));
                Set($"PSG tone {_latchChannel}", (tone & 0x3F0) | (value & 0x0F), isEvent: false, changes);
            }
        }
        else if (!_latchVolume && _latchChannel < 3)
        {
            int tone = Math.Max(0, _values.GetValueOrDefault($"PSG tone {_latchChannel}", 0));
            Set($"PSG tone {_latchChannel}", (tone & 0x0F) | ((value & 0x3F) << 4), isEvent: false, changes);
        }
    }

    private void Set(string key, int value, bool isEvent, List<ChipChange> changes)
    {
        int before = _values.GetValueOrDefault(key, -1);
        _values[key] = value;
        changes.Add(new ChipChange(key, before, value, isEvent));
    }
}
