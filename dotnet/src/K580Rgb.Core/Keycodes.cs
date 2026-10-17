// USB HID key and media codes, and US-layout text -> key events.
using System.Text.Json.Nodes;

namespace K580Rgb.Core;

public sealed record KeyCode(int Usage, string Name, string BrowserCode);

public static class Keycodes
{
    public static readonly IReadOnlyList<KeyCode> Keys = Build();
    public static readonly IReadOnlyDictionary<int, string> Names = Keys.ToDictionary(k => k.Usage, k => k.Name);
    public const int ModFirst = 0xE0, ModLast = 0xE7;     // modifiers; the firmware sends them as bit 1 << (usage - 0xE0)

    // Consumer-page (media) codes the keyboard can send from a remapped key
    public static readonly IReadOnlyList<(int Code, string Name)> Media =
    [
        (0xCD, "Play / Pause"), (0xB5, "Next track"), (0xB6, "Previous track"), (0xB7, "Stop"),
        (0xE2, "Mute"), (0xE9, "Volume up"), (0xEA, "Volume down"),
        (0x183, "Media player"), (0x18A, "Mail"), (0x192, "Calculator"), (0x194, "File explorer"),
        (0x221, "Search"), (0x223, "Browser home"), (0x224, "Browser back"), (0x225, "Browser forward"),
        (0x227, "Refresh"),
    ];
    public static readonly IReadOnlyDictionary<int, string> MediaNames = Media.ToDictionary(m => m.Code, m => m.Name);

    public const int Shift = 0xE1;
    static readonly Dictionary<char, (int Usage, bool Shift)> Us = BuildUs();

    static List<KeyCode> Build()
    {
        var k = new List<KeyCode>();
        for (int i = 0; i < 26; i++) { var c = ((char)('A' + i)).ToString(); k.Add(new(0x04 + i, c, "Key" + c)); }
        for (int i = 0; i < 10; i++) { var d = "1234567890"[i].ToString(); k.Add(new(0x1E + i, d, "Digit" + d)); }
        k.AddRange([
            new(0x28, "Enter", "Enter"), new(0x29, "Esc", "Escape"), new(0x2A, "Backspace", "Backspace"),
            new(0x2B, "Tab", "Tab"), new(0x2C, "Space", "Space"), new(0x2D, "-", "Minus"), new(0x2E, "=", "Equal"),
            new(0x2F, "[", "BracketLeft"), new(0x30, "]", "BracketRight"), new(0x31, "\\", "Backslash"),
            new(0x33, ";", "Semicolon"), new(0x34, "'", "Quote"), new(0x35, "`", "Backquote"), new(0x36, ",", "Comma"),
            new(0x37, ".", "Period"), new(0x38, "/", "Slash"), new(0x39, "Caps Lock", "CapsLock"),
        ]);
        for (int i = 0; i < 12; i++) k.Add(new(0x3A + i, $"F{i + 1}", $"F{i + 1}"));
        k.AddRange([
            new(0x46, "Print Screen", "PrintScreen"), new(0x47, "Scroll Lock", "ScrollLock"), new(0x48, "Pause", "Pause"),
            new(0x49, "Insert", "Insert"), new(0x4A, "Home", "Home"), new(0x4B, "Page Up", "PageUp"),
            new(0x4C, "Delete", "Delete"), new(0x4D, "End", "End"), new(0x4E, "Page Down", "PageDown"),
            new(0x4F, "Right", "ArrowRight"), new(0x50, "Left", "ArrowLeft"), new(0x51, "Down", "ArrowDown"),
            new(0x52, "Up", "ArrowUp"), new(0x53, "Num Lock", "NumLock"), new(0x54, "Num /", "NumpadDivide"),
            new(0x55, "Num *", "NumpadMultiply"), new(0x56, "Num -", "NumpadSubtract"), new(0x57, "Num +", "NumpadAdd"),
            new(0x58, "Num Enter", "NumpadEnter"),
        ]);
        for (int i = 0; i < 9; i++) k.Add(new(0x59 + i, $"Num {i + 1}", $"Numpad{i + 1}"));
        k.AddRange([
            new(0x62, "Num 0", "Numpad0"), new(0x63, "Num .", "NumpadDecimal"), new(0x64, "ISO \\", "IntlBackslash"),
            new(0x65, "Menu", "ContextMenu"),
        ]);
        for (int i = 0; i < 12; i++) k.Add(new(0x68 + i, $"F{i + 13}", $"F{i + 13}"));
        k.AddRange([
            new(0xE0, "Left Ctrl", "ControlLeft"), new(0xE1, "Left Shift", "ShiftLeft"), new(0xE2, "Left Alt", "AltLeft"),
            new(0xE3, "Left Win", "MetaLeft"), new(0xE4, "Right Ctrl", "ControlRight"), new(0xE5, "Right Shift", "ShiftRight"),
            new(0xE6, "Right Alt", "AltRight"), new(0xE7, "Right Win", "MetaRight"),
        ]);
        return k;
    }

    static Dictionary<char, (int, bool)> BuildUs()
    {
        var us = new Dictionary<char, (int, bool)> { [' '] = (0x2C, false), ['\n'] = (0x28, false), ['\t'] = (0x2B, false) };
        for (int i = 0; i < 10; i++)
        {
            us["1234567890"[i]] = (0x1E + i, false);
            us["!@#$%^&*()"[i]] = (0x1E + i, true);
        }
        foreach (var (u, plain, shifted) in new[] {
                     (0x2D, '-', '_'), (0x2E, '=', '+'), (0x2F, '[', '{'), (0x30, ']', '}'), (0x31, '\\', '|'),
                     (0x33, ';', ':'), (0x34, '\'', '"'), (0x35, '`', '~'), (0x36, ',', '<'), (0x37, '.', '>'), (0x38, '/', '?') })
        {
            us[plain] = (u, false);
            us[shifted] = (u, true);
        }
        for (int i = 0; i < 26; i++)
        {
            us[(char)('a' + i)] = (0x04 + i, false);
            us[(char)('A' + i)] = (0x04 + i, true);
        }
        return us;
    }

    /// <summary>Key events that type <paramref name="text"/> with a US keyboard layout. Throws on other characters.</summary>
    public static JsonArray TextEvents(string text, double delayMs = 10)
    {
        var events = new JsonArray();
        foreach (char ch in text.Replace("\r\n", "\n"))
        {
            if (!Us.TryGetValue(ch, out var k))
                throw new ArgumentException($"can't type '{ch}' (only US-keyboard characters)");
            if (k.Shift) events.Add(new JsonArray("down", Shift, 0));
            events.Add(new JsonArray("down", k.Usage, delayMs));
            events.Add(new JsonArray("up", k.Usage, delayMs));
            if (k.Shift) events.Add(new JsonArray("up", Shift, 0));
        }
        return events;
    }

    public static JsonArray KeysJson() => [.. Keys.Select(k => (JsonNode)new JsonArray(k.Usage, k.Name, k.BrowserCode))];
    public static JsonArray MediaJson() => [.. Media.Select(m => (JsonNode)new JsonArray(m.Code, m.Name))];
}
