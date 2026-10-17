using System.Text.Json.Nodes;

namespace K580Rgb.Core.Device;

/// <summary>The 8 effect parameters at the start of each profile.</summary>
public sealed record Params(int Mode = 6, int Brightness = 5, int Speed = 3, int Direction = 0,
                            bool Random = false, Rgb Color = default)
{
    public static Params FromBytes(ReadOnlySpan<byte> b) =>
        new(b[0], b[1], b[2], b[3] != 0 ? 1 : 0, b[4] != 0, new Rgb(b[5], b[6], b[7]));

    public byte[] ToBytes() =>
        [(byte)Mode, (byte)Brightness, (byte)Speed, (byte)Direction, (byte)(Random ? 1 : 0),
         Color.R, Color.G, Color.B];

    public JsonObject ToJson() => new()
    {
        ["mode"] = Mode, ["brightness"] = Brightness, ["speed"] = Speed, ["direction"] = Direction,
        ["random"] = Random, ["color"] = new JsonArray(Color.R, Color.G, Color.B),
    };

    public static Params FromJson(JsonNode d)
    {
        var c = d["color"]!.AsArray();
        return new((int)Num(d["mode"]), (int)Num(d["brightness"]), (int)Num(d["speed"]), (int)Num(d["direction"]),
                   d["random"] is JsonValue r && (r.TryGetValue(out bool b) ? b : Num(r) != 0),
                   new Rgb((byte)Num(c[0]), (byte)Num(c[1]), (byte)Num(c[2])));
    }

    static double Num(JsonNode? n) => JsonNum.Get(n);
}
