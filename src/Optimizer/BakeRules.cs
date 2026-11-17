namespace Mod2Xgm.Optimizer;

/// <summary>
/// How recording bytes are shared out, and which drum bakes the allocator may use (plan T9.4t10,
/// docs/adr/0115-drum-bakes.md). No option sets them: the harness does, with <c>rule:bytes</c> and <c>rule:bake</c>, until
/// blind rounds decide the default.
/// </summary>
/// <param name="BytesByScore">True: when bytes run short, they are held first for the samples whose other sounds fall furthest from them, per byte (<c>rule:bytes=score</c>).</param>
/// <param name="Together">True: drum hits that start on the same tick may play as one recording (<c>rule:bake=together</c>).</param>
/// <param name="Beat">True: a stretch of the drum tracks that repeats may play as one recording (<c>rule:bake=beat</c>).</param>
internal sealed record BakeRules(bool BytesByScore, bool Together, bool Beat)
{
    /// <summary>The converter's own: bytes in the order of importance, no bakes.</summary>
    public static BakeRules None { get; } = new(false, false, false);
}
