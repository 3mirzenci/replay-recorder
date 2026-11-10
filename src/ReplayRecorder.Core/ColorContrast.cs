namespace ReplayRecorder.Core;

/// <summary>WCAG contrast maths for text on the accent colour.</summary>
public static class ColorContrast
{
    /// <summary>Dark text used on light accents (#141210).</summary>
    public const byte DarkR = 0x14, DarkG = 0x12, DarkB = 0x10;

    /// <summary>Relative luminance (WCAG 2.x) of an sRGB colour.</summary>
    public static double Luminance(byte r, byte g, byte b)
    {
        static double Lin(byte c) { double s = c / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }

    public static double Ratio(double l1, double l2) => (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);

    /// <summary>True when dark text reads better than white on this accent (higher contrast wins).</summary>
    public static bool DarkTextOn(byte r, byte g, byte b)
    {
        double l = Luminance(r, g, b);
        return Ratio(l, Luminance(DarkR, DarkG, DarkB)) >= Ratio(l, 1.0);
    }
}
