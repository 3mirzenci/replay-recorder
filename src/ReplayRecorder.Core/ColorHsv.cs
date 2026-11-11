namespace ReplayRecorder.Core;

/// <summary>Hue (0-360), saturation and brightness (0-1) to and from RGB, for the colour picker.</summary>
public static class ColorHsv
{
    public static (byte R, byte G, byte B) ToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360 / 60;
        double c = v * s, x = c * (1 - Math.Abs(h % 2 - 1)), m = v - c;
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x),
            3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x),
        };
        static byte B(double f) => (byte)Math.Round(Math.Clamp(f, 0, 1) * 255);
        return (B(r + m), B(g + m), B(b + m));
    }

    public static (double H, double S, double V) FromRgb(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), d = max - Math.Min(rf, Math.Min(gf, bf));
        double h = d == 0 ? 0 : max == rf ? 60 * ((gf - bf) / d % 6) : max == gf ? 60 * ((bf - rf) / d + 2) : 60 * ((rf - gf) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }
}
