namespace Mod2Xgm.Model.Window;

/// <summary>Which ends of the converted part move in past the silence there (<c>--trim-silence</c>, docs/adr/0122-trim-silence.md).</summary>
[Flags]
internal enum TrimSilence
{
    /// <summary>Keep the silence at both ends.</summary>
    None = 0,

    /// <summary>Start at the row where the first sound is.</summary>
    Start = 1,

    /// <summary>End on the row after the last sound.</summary>
    End = 2,

    /// <summary>Both.</summary>
    Both = Start | End,
}
