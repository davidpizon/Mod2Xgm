namespace Mod2Xgm.Optimizer;

/// <summary>
/// How the allocator keeps a sound (a sample on a MOD channel) on one voice through the song (plan T9.4c): a switch
/// between recording, FM and PSG mid-song is heard, and David found it jarring even when the side stays the same.
/// <see cref="SongRank"/> is the converter's rule, chosen by ear (docs/adr/0092-stable-voices.md); the others are kept
/// for comparison.
/// </summary>
internal enum VoiceSwitching
{
    /// <summary>Each section (one order) chooses again: the most important sound first, staying with its voice unless another is clearly closer.</summary>
    PerSection,

    /// <summary>A sound that already has a voice chooses before a newcomer and keeps that voice while it has room.</summary>
    KeepPlaying,

    /// <summary>Each sound chooses one voice for the whole song; a note takes another only where that one has no room.</summary>
    WholeSong,

    /// <summary>
    /// Each section chooses again, but in one order for the whole song (the sounds that matter most over the song first),
    /// and a sound keeps the voice it has while that voice has room: a late but important sound still gets a good voice,
    /// and a sound gives its voice up only when it runs out of room. The converter's rule (rounds 13–15).
    /// </summary>
    SongRank,
}
