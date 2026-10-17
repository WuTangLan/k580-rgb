// Macros and key assignments stored on the keyboard (stock firmware, no patch needed).
//
// Keymap (cmd 0x07/0x08, flash 0xA100 + profile * 0x17A): 126 entries of 3 bytes, one per
// matrix position pos = col * 6 + row:
//   02 02 kk   HID key            02 01 mm   modifier bit        02 00 00   nothing
//   03 lo hi   media (consumer)   05 01 nn   play macro nn       05 1A nn   REC slot nn (G1-G5)
// The Fn layer (Fn + F1..F12 media, Fn + arrows, ...) is a fixed table in the firmware and
// always wins while Fn is held, so remapping never touches it. The Fn key itself is never remapped.
//
// Macros (cmd 0x09/0x0A, flash 0xAC00): u16 offsets at 0xAC10 + n * 2 point (relative to 0xAC00)
// at a 4-byte header [event count lo, hi, repeat, 0] followed by 4-byte events:
//   [delay lo, 0x20 | 0x80 if release | delay hi nibble, 1 = modifier / 2 = key, code or modifier bit]
// The delay runs after the event, in steps of DelayUnitMs (firmware tick: 32 SysTicks).
using System.Text.Json.Nodes;

namespace K580Rgb.Core;

public static class Macros
{
    public const int KeymapSize = 126 * 3;
    public const int MacroBase = 0xAC00;
    public const int TableOffset = 0x10;       // index table at 0xAC10
    public const int MaxMacros = 48;           // table stays inside the 0xAC00-0xAC7F block the factory reset clears
    public const int DataOffset = 0x80;        // macro data from 0xAC80
    public const int AreaEnd = 0xC800;         // REC-recorded G-key macros start here
    public const double DelayUnitMs = 9.6;
    public const int MaxDelay = 0xFFF;         // 12 bits per event (~39 s)
    public const int MaxRepeat = 255;

    const int FnPos = 5 * 6 + 5;               // Fn key (col 5, row 5): never remapped
    static readonly (string, int)[] GKeys = [("G1", 100), ("G2", 88), ("G3", 99), ("G4", 93), ("G5", 87)];   // firmware table 0x810E
    static readonly (string, int)[] MediaKeys = [("Previous", 114), ("Play/Pause", 125), ("Next", 123)];

    public static int PosOfLed(int index) => index % 21 * 6 + index / 21;

    /// <summary>[{pos, label, group}] for every key that can be remapped.</summary>
    public static JsonArray Assignable()
    {
        var output = new JsonArray();
        foreach (var (n, p) in GKeys) output.Add(new JsonObject { ["pos"] = p, ["label"] = n, ["group"] = "macro" });
        foreach (var (n, p) in MediaKeys) output.Add(new JsonObject { ["pos"] = p, ["label"] = n, ["group"] = "media" });
        foreach (var k in Layout.Keys)
        {
            int pos = PosOfLed(k.Index);
            if (pos != FnPos && !k.Iso)
                output.Add(new JsonObject { ["pos"] = pos, ["label"] = k.Label.Length > 0 ? k.Label : "Space",
                                            ["group"] = "key", ["led"] = k.Index });
        }
        return output;
    }

    static readonly HashSet<int> ValidPos = [.. Assignable().Select(a => JsonNum.Int(a!["pos"]))];

    // -- macros -------------------------------------------------------------------------------

    static int DelayUnits(double ms) => Math.Clamp((int)Math.Round(ms / DelayUnitMs), 0, MaxDelay);

    static double ToDouble(JsonNode? n) => JsonNum.Get(n);

    /// <summary>Validate one macro {name, events: [[down|up, usage, delay_ms]...], repeat} from the UI.</summary>
    public static JsonObject NormalizeMacro(JsonNode? node)
    {
        var m = node as JsonObject ?? throw new ArgumentException("bad macro");
        var name = ((string?)m["name"] ?? "").Trim();
        name = name[..Math.Min(40, name.Length)];
        if (name.Length == 0)
            throw new ArgumentException("a macro needs a name");
        var events = new JsonArray();
        foreach (var ev in m["events"] as JsonArray ?? [])
        {
            var e = ev!.AsArray();
            string action = (string)e[0]!;
            int usage = (int)ToDouble(e[1]);
            double delay = ToDouble(e[2]);
            if (action is not ("down" or "up") || !Keycodes.Names.ContainsKey(usage))
                throw new ArgumentException($"bad event in macro \"{name}\"");
            events.Add(new JsonArray(action, usage, Math.Max(0, Math.Min(MaxDelay * DelayUnitMs, Math.Round(delay)))));
        }
        if (events.Count == 0)
            throw new ArgumentException($"macro \"{name}\" is empty");
        int repeat = m["repeat"] is JsonNode r ? (int)ToDouble(r) : 1;
        return new JsonObject { ["name"] = name, ["events"] = events, ["repeat"] = Math.Clamp(repeat, 1, MaxRepeat) };
    }

    /// <summary>Release anything still held at the end, so a macro can never leave a key stuck.</summary>
    static List<(string Action, int Usage, double Delay)> Balanced(JsonArray events)
    {
        var list = events.Select(e => ((string)e![0]!, (int)ToDouble(e[1]), ToDouble(e[2]))).ToList();
        var held = new List<int>();
        foreach (var (action, usage, _) in list)
        {
            if (action == "down" && !held.Contains(usage)) held.Add(usage);
            else if (action == "up") held.Remove(usage);
        }
        held.Reverse();
        list.AddRange(held.Select(u => ("up", u, 0.0)));
        return list;
    }

    static byte[] EncodeEvent(string action, int usage, double delayMs)
    {
        int d = DelayUnits(delayMs);
        var (kind, code) = usage is >= Keycodes.ModFirst and <= Keycodes.ModLast ? (1, 1 << (usage - Keycodes.ModFirst)) : (2, usage);
        return [(byte)(d & 0xFF), (byte)(0x20 | (action == "up" ? 0x80 : 0) | (d >> 8)), (byte)kind, (byte)code];
    }

    /// <summary>Macro list -> (bytes for flash 0xAC00.., index of each macro by name).</summary>
    public static (byte[] Image, Dictionary<string, int> Index) Compile(IReadOnlyList<JsonObject> macros)
    {
        if (macros.Count > MaxMacros)
            throw new ArgumentException($"at most {MaxMacros} macros");
        var table = new byte[DataOffset - TableOffset];
        var data = new List<byte>();
        var index = new Dictionary<string, int>();
        for (int n = 0; n < macros.Count; n++)
        {
            var m = macros[n];
            var events = Balanced(m["events"]!.AsArray());
            int off = DataOffset + data.Count;
            (table[n * 2], table[n * 2 + 1]) = ((byte)(off & 0xFF), (byte)(off >> 8));
            data.AddRange([(byte)(events.Count & 0xFF), (byte)(events.Count >> 8), (byte)JsonNum.Int(m["repeat"], 1), 0]);
            foreach (var (a, u, d) in events)
                data.AddRange(EncodeEvent(a, u, d));
            index[(string)m["name"]!] = n;
        }
        byte[] image = [.. new byte[TableOffset], .. table, .. data];
        if (MacroBase + image.Length > AreaEnd)
            throw new ArgumentException($"macros too long for the keyboard ({image.Length - DataOffset} of {AreaEnd - MacroBase - DataOffset} bytes)");
        return (image, index);
    }

    // -- keymap ---------------------------------------------------------------------------------

    /// <summary>Assignment from the UI -> 3 keymap bytes, or null for the factory default.</summary>
    static byte[]? Entry(JsonObject assign, IReadOnlyDictionary<string, int> macroIndex)
    {
        switch ((string?)assign["type"])
        {
            case null or "default":
                return null;
            case "macro":
                return (string?)assign["macro"] is string name && macroIndex.TryGetValue(name, out int n) ? [0x05, 0x01, (byte)n] : null;
            case "key":
                int u = (int)ToDouble(assign["code"]);
                if (u is >= Keycodes.ModFirst and <= Keycodes.ModLast)
                    return [0x02, 0x01, (byte)(1 << (u - Keycodes.ModFirst))];
                if (!Keycodes.Names.ContainsKey(u))
                    throw new ArgumentException($"unknown key code {u}");
                return [0x02, 0x02, (byte)u];
            case "media":
                int c = (int)ToDouble(assign["code"]);
                if (!Keycodes.MediaNames.ContainsKey(c))
                    throw new ArgumentException($"unknown media code {c}");
                return [0x03, (byte)(c & 0xFF), (byte)(c >> 8)];
            case "disabled":
                return [0x02, 0x00, 0x00];
            case var kind:
                throw new ArgumentException($"unknown assignment '{kind}'");
        }
    }

    /// <summary>Factory keymap with the assignments applied (assignments: {pos: assign}).</summary>
    public static byte[] BuildKeymap(byte[] defaultKeymap, JsonObject assignments, IReadOnlyDictionary<string, int> macroIndex)
    {
        var km = (byte[])defaultKeymap.Clone();
        foreach (var (key, value) in assignments)
        {
            int pos = int.Parse(key);
            if (!ValidPos.Contains(pos))
                throw new ArgumentException($"key {pos} can't be remapped");
            if (Entry(value!.AsObject(), macroIndex) is byte[] e)
                e.CopyTo(km, pos * 3);
        }
        return km;
    }

    public static JsonObject NormalizeAssignments(JsonObject? assignments, IReadOnlyCollection<string> macroNames)
    {
        var output = new JsonObject();
        var dummyIndex = macroNames.ToDictionary(n => n, _ => 0);
        foreach (var (key, value) in assignments ?? [])
        {
            if (!int.TryParse(key, out int pos) || !ValidPos.Contains(pos) || value is not JsonObject a
                || (string?)a["type"] is null or "default")
                continue;
            if ((string?)a["type"] == "macro" && !macroNames.Contains((string?)a["macro"] ?? ""))
                continue;                     // its macro was deleted
            var clean = new JsonObject();
            foreach (var k in new[] { "type", "macro", "code" })
                if (a.ContainsKey(k))
                    clean[k] = a[k]?.DeepClone();
            Entry(clean, dummyIndex);         // validates
            output[pos.ToString()] = clean;
        }
        return output;
    }
}
