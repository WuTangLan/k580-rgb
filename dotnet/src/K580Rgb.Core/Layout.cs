// Physical layout of the K580's 107 LEDs.
//
// LED index = row * 21 + col of the key matrix (firmware keymap at 0xA100, LED list at
// 0x8232). Positions are in key units for a full-size ANSI board. The three ISO/JIS
// positions have LEDs but no key on ANSI boards, so they are marked Iso.
using System.Text.Json.Nodes;

namespace K580Rgb.Core;

public sealed record Key(int Index, string Label, double X, double Y, double W, double H,
                         bool Iso = false, string? Side = null)   // Side: null for keys, "left" / "right" for side-strip LEDs
{
    public double Cx => X + W / 2;
    public double Cy => Y + H / 2;

    public JsonObject ToJson() => new()
    {
        ["index"] = Index, ["label"] = Label, ["x"] = X, ["y"] = Y, ["w"] = W, ["h"] = H,
        ["iso"] = Iso, ["side"] = Side,
    };
}

public static class Layout
{
    public const int Cols = 21;
    public const double Width = 22.5, Height = 6.5;

    // Side light strips: 7 LEDs per side, listed front (spacebar end) to back. The firmware's
    // table (0x8335) runs back to front; confirmed on the keyboard with the VU meter, where the
    // strips must rise together with the key rows. They sit just outside the key area so
    // spatial effects flow onto them.
    public static readonly int[] SideLeft = [118, 117, 116, 115, 114, 112, 13];
    public static readonly int[] SideRight = [98, 100, 79, 20, 19, 18, 17];
    public const double SideGap = 0.35, SideW = 0.4;

    public static readonly IReadOnlyList<Key> Keys = BuildKeys();
    // LEDs present only for ISO/JIS key positions
    public static readonly IReadOnlyList<Key> IsoKeys =
        [Iso("#", 3, 12), Iso("\\", 4, 1), Iso("Ro", 4, 12)];
    public static readonly IReadOnlyList<Key> SideKeys =
        [.. Strip(SideLeft, -SideGap - SideW, "left"), .. Strip(SideRight, Width + SideGap, "right")];
    public static readonly IReadOnlyDictionary<int, Key> ByIndex =
        Keys.Concat(IsoKeys).Concat(SideKeys).ToDictionary(k => k.Index);
    /// <summary>Every LED with a key or strip segment (no ISO-only positions), in BY_INDEX order.</summary>
    public static readonly Key[] Lit = [.. ByIndex.Values.Where(k => !k.Iso)];
    public static readonly double[] Bounds = [-SideGap - SideW, 0, Width + SideGap + SideW, Height];   // x0, y0, x1, y1 incl. strips

    static Key Iso(string label, int r, int c) => new(r * Cols + c, label, 0, 0, 0, 0, Iso: true);

    static IEnumerable<Key> Strip(int[] indices, double x, string side)
    {
        double seg = Height / 7;
        return indices.Select((i, n) => new Key(i, "", x, Height - (n + 1) * seg, SideW, seg, Side: side));
    }

    static List<Key> BuildKeys()
    {
        var keys = new List<Key>();
        void K(string label, int r, int c, double x, double y, double w = 1, double h = 1) =>
            keys.Add(new Key(r * Cols + c, label, x, y, w, h));

        K("Esc", 0, 0, 0, 0);
        double[] fx = [2, 3, 4, 5, 6.5, 7.5, 8.5, 9.5, 11, 12, 13, 14];
        for (int i = 0; i < 12; i++) K($"F{i + 1}", 0, i + 1, fx[i], 0);
        K("PrtSc", 0, 14, 15.25, 0); K("ScrLk", 0, 15, 16.25, 0); K("Pause", 0, 16, 17.25, 0);

        K("`", 1, 0, 0, 1.5);
        for (int i = 0; i < 10; i++) K("1234567890"[i].ToString(), 1, i + 1, i + 1, 1.5);
        K("-", 1, 11, 11, 1.5); K("=", 1, 12, 12, 1.5); K("Bksp", 1, 13, 13, 1.5, 2);
        K("Ins", 1, 14, 15.25, 1.5); K("Home", 1, 15, 16.25, 1.5); K("PgUp", 1, 16, 17.25, 1.5);
        K("Num", 1, 17, 18.5, 1.5); K("/", 1, 18, 19.5, 1.5); K("*", 1, 19, 20.5, 1.5);
        K("-", 1, 20, 21.5, 1.5);

        K("Tab", 2, 0, 0, 2.5, 1.5);
        for (int i = 0; i < 10; i++) K("QWERTYUIOP"[i].ToString(), 2, i + 1, 1.5 + i, 2.5);
        K("[", 2, 11, 11.5, 2.5); K("]", 2, 12, 12.5, 2.5); K("\\", 2, 13, 13.5, 2.5, 1.5);
        K("Del", 2, 14, 15.25, 2.5); K("End", 2, 15, 16.25, 2.5); K("PgDn", 2, 16, 17.25, 2.5);
        K("7", 2, 17, 18.5, 2.5); K("8", 2, 18, 19.5, 2.5); K("9", 2, 19, 20.5, 2.5);
        K("+", 2, 20, 21.5, 2.5, 1, 2);

        K("Caps", 3, 0, 0, 3.5, 1.75);
        for (int i = 0; i < 9; i++) K("ASDFGHJKL"[i].ToString(), 3, i + 1, 1.75 + i, 3.5);
        K(";", 3, 10, 10.75, 3.5); K("'", 3, 11, 11.75, 3.5); K("Enter", 3, 13, 12.75, 3.5, 2.25);
        K("4", 3, 17, 18.5, 3.5); K("5", 3, 18, 19.5, 3.5); K("6", 3, 19, 20.5, 3.5);

        K("Shift", 4, 0, 0, 4.5, 2.25);
        for (int i = 0; i < 7; i++) K("ZXCVBNM"[i].ToString(), 4, i + 2, 2.25 + i, 4.5);
        K(",", 4, 9, 9.25, 4.5); K(".", 4, 10, 10.25, 4.5); K("/", 4, 11, 11.25, 4.5);
        K("Shift", 4, 13, 12.25, 4.5, 2.75); K("↑", 4, 15, 16.25, 4.5);
        K("1", 4, 17, 18.5, 4.5); K("2", 4, 18, 19.5, 4.5); K("3", 4, 19, 20.5, 4.5);
        K("Enter", 4, 20, 21.5, 4.5, 1, 2);

        K("Ctrl", 5, 0, 0, 5.5, 1.25); K("Win", 5, 1, 1.25, 5.5, 1.25); K("Alt", 5, 2, 2.5, 5.5, 1.25);
        K("", 5, 3, 3.75, 5.5, 6.25); K("Alt", 5, 4, 10, 5.5, 1.25); K("Fn", 5, 5, 11.25, 5.5, 1.25);
        K("Menu", 5, 6, 12.5, 5.5, 1.25); K("Ctrl", 5, 8, 13.75, 5.5, 1.25);
        K("←", 5, 14, 15.25, 5.5); K("↓", 5, 15, 16.25, 5.5); K("→", 5, 16, 17.25, 5.5);
        K("0", 5, 18, 18.5, 5.5, 2); K(".", 5, 19, 20.5, 5.5);
        return keys;
    }

    static Layout()
    {
        if (Keys.Concat(IsoKeys).Select(k => k.Index).Distinct().Count() != 107 || ByIndex.Count != 121)
            throw new InvalidOperationException($"layout: {ByIndex.Count} LEDs");
    }
}
