namespace Mod2Xgm.Optimizer;

/// <summary>
/// How the allocator pre-mixes recordings that would otherwise lose their PCM voice (plan T9.4b,
/// docs/adr/0094-pre-mixed-voice-steals.md): before a note moves to FM/PSG or is cut for want of a voice, it may be mixed
/// with recordings already playing into one sample on one voice.
/// </summary>
/// <param name="Enabled">False: no pre-mixes, as before T9.4b.</param>
/// <param name="Tails">
/// True: a looped note that outlasts the mix goes on as a tail, the rest of its own sample on the same voice from the next
/// row. False: such a note is never pre-mixed. A one-shot note that outlasts the mix is baked to its end either way.
/// </param>
/// <param name="MaxClipDb">The most a mix may be turned down so it doesn't clip; a mix that needs more isn't made.</param>
/// <param name="MakeRoom">
/// True, when a budget is set (<c>--max-pcm-bytes</c>): the shrinker leaves room for the pre-mixes that didn't fit, and the
/// song is allocated again. False: pre-mixes use only the bytes left over.
/// </param>
internal sealed record PreMixRules(bool Enabled, bool Tails, double MaxClipDb, bool MakeRoom)
{
    /// <summary>No pre-mixes: every note that finds no PCM voice moves or is cut, as before T9.4b.</summary>
    public static PreMixRules None { get; } = new(false, false, 0, false);

    /// <summary>The converter's rules (docs/adr/0094-pre-mixed-voice-steals.md).</summary>
    public static PreMixRules Default { get; } = new(true, true, 6, false);
}
