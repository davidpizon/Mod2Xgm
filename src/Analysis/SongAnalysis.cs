using Mod2Xgm.Model;

namespace Mod2Xgm.Analysis;

/// <summary>
/// What a conversion measured about a song for the optimizer's candidates (passes 1 and 2 of docs/multi-pass.md): the
/// song, how it plays, each sample's analysis and how many notes it sounds at once. The accuracy harness gets it from the
/// conversion's analysis hook, so nothing is analyzed twice.
/// </summary>
/// <param name="Song">The song.</param>
/// <param name="Playback">The song played tick by tick.</param>
/// <param name="Samples">Every analyzed sample (those with sound that aren't ignored), in sample order.</param>
/// <param name="Voices">How many notes the song sounds at once.</param>
internal sealed record SongAnalysis(Song Song, Playback Playback, IReadOnlyList<SampleAnalysis> Samples, SongVoices Voices)
{
    /// <summary>Every note the song plays (<see cref="SongNote.Find"/>), found once on first use.</summary>
    public IReadOnlyList<SongNote> Notes => field ??= SongNote.Find(Song, Playback);

    /// <summary>
    /// What the analysis was made from: the MOD file's bytes and every option that changes it, the key of the song's
    /// candidates in the candidate cache (docs/adr/0074-candidate-cache.md); <c>null</c> when unknown, and then they are
    /// never cached.
    /// </summary>
    public Optimizer.Fingerprint? Source { get; init; }
}
