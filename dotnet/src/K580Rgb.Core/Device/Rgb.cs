namespace K580Rgb.Core.Device;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = default;

    public static Rgb[] Blank(int n = K580.NumLeds) => new Rgb[n];

    public override string ToString() => $"#{R:x2}{G:x2}{B:x2}";
}
