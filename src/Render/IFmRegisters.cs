namespace Mod2Xgm.Render;

/// <summary>Something that takes YM3438 register writes: the chip model itself, or the chips with a recorder listening.</summary>
internal interface IFmRegisters
{
    /// <summary>Writes <paramref name="value"/> to <paramref name="register"/> on <paramref name="part"/> 0 or 1.</summary>
    void WriteRegister(int part, byte register, byte value);
}
