// HID protocol for the Redragon K580 (Sonix SN32F248B, EVision firmware, 0C45:5004).
//
// All packets are 64 bytes on the vendor collection (usage page 0xFF1C, usage 0x92):
//   [0]=0x04 report id  [1..2]=checksum of bytes 3..63  [3]=command  [4]=length
//   [5..6]=offset (LE)  [7]=status (0xFF = rejected)    [8..63]=data (max 56 bytes)
//
// Stock commands write straight to flash (see NOTES.md), so callers should only send
// real changes. Command 0x20 exists only on the direct-mode patched firmware.
using System.Diagnostics;
using HidSharp;

namespace K580Rgb.Core.Device;

public class DeviceException(string message, Exception? inner = null) : IOException(message, inner);

public sealed class K580 : IDisposable
{
    public const int Vid = 0x0C45, Pid = 0x5004;
    public const uint UsagePage = 0xFF1C, Usage = 0x92;

    public const byte CmdReadHeader = 0x03;    // flash 0xA000 + offset
    public const byte CmdReadParams = 0x05;    // flash 0xA080 + offset
    public const byte CmdWriteParams = 0x06;
    public const byte CmdReadKeymap = 0x07;    // flash 0xA100 + offset (one 126 x 3-byte keymap per profile)
    public const byte CmdWriteKeymap = 0x08;
    public const byte CmdReadMacros = 0x09;    // flash 0xAC00 + offset
    public const byte CmdWriteMacros = 0x0A;
    public const byte CmdReadRom = 0x0F;       // flash 0x90D8 + offset: firmware tables, starting with the factory keymap
    public const byte CmdReadCustom = 0x10;    // flash 0xA600 + offset
    public const byte CmdWriteCustom = 0x11;
    public const byte CmdDirect = 0x20;        // patched firmware: RAM-only LED planes

    public const int MaxData = 56;
    public const int Profiles = 3;
    public const int ProfileParamsSize = 0x2A;
    public const int ProfileCustomSize = 0x200;
    public const int KeymapSize = 0x17A;       // 126 x 3 bytes
    public const int HeaderProfile = 0x0A;     // active profile index in the header
    public const int NumLeds = 126;            // 6 rows x 21 columns, index = row * 21 + col
    public const int CustomSize = 3 * NumLeds;
    public const byte StatusError = 0xFF;
    static readonly TimeSpan DirectKeepalive = TimeSpan.FromSeconds(1);   // patched firmware falls back to stock effects after ~2.5 s
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(500);   // per attempt
    const int ReplyAttempts = 3;
    const int ReadPollMs = 100;

    // Side light strips: 14 LEDs in two groups of 7 (firmware table 0x8335). Their green and
    // blue are wired swapped relative to the keys (firmware writes G into the B plane for them).
    public static readonly int[] SideGroupA = [13, 112, 114, 115, 116, 117, 118];   // left strip, firmware order (back to front)
    public static readonly int[] SideGroupB = [17, 18, 19, 20, 79, 100, 98];        // right strip, firmware order (back to front)
    public static readonly int[] SideLeds = [.. SideGroupA, .. SideGroupB];
    public const int SideOffset = 0x14;        // profile bytes: mode, speed, color index
    public static readonly string[] SideModes = ["Off", "Rainbow flow", "Color cycle", "Breathing", "Static", "Follow keys"];
    public static readonly string[] SideColors = ["Red", "Orange", "Yellow", "Green", "Cyan", "Blue", "Purple", "White"];

    // Indicator LEDs (not keys). Stock present() lights them from the host's LED report:
    // 77: R=Scroll, G=Caps, B=Num   78: R=any lock on, G=Kana, B=Compose
    public const int LedIndicatorA = 77, LedIndicatorB = 78;

    readonly Lock _lock = new();
    HidStream? _stream;
    int _reportLength = 64, _replyLength = 64;
    byte[]? _directLast;                       // last streamed planes
    long _directSentAt;

    /// <summary>Packets that caused a flash write this session.</summary>
    public int FlashWrites { get; private set; }

    // -- connection ----------------------------------------------------------

    public static HidDevice? Find()
    {
        foreach (var d in DeviceList.Local.GetHidDevices(Vid, Pid))
        {
            try
            {
                // on Windows each top-level collection is its own device with one item
                foreach (var item in d.GetReportDescriptor().DeviceItems)
                    if (item.Usages.GetAllValues().Contains((UsagePage << 16) | Usage))
                        return d;
            }
            catch (Exception) { }              // collections we may not query (keyboard/mouse): skip
        }
        return null;
    }

    public bool Connected => _stream != null;

    public bool Open()
    {
        lock (_lock)
        {
            if (_stream != null)
                return true;
            var dev = Find();
            if (dev == null || !dev.TryOpen(out var stream))   // still re-enumerating, or held exclusively for a moment
                return false;
            stream.ReadTimeout = ReadPollMs;
            stream.WriteTimeout = 1000;
            _reportLength = Math.Max(64, dev.GetMaxOutputReportLength());
            _replyLength = Math.Max(64, dev.GetMaxInputReportLength());
            _stream = stream;
            _directLast = null;
            return true;
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            try { _stream?.Dispose(); }
            finally { _stream = null; }
        }
    }

    public void Dispose() => Close();

    // -- transport -----------------------------------------------------------

    public byte[] Command(byte cmd, int length = 0, int offset = 0, ReadOnlySpan<byte> data = default)
    {
        var pkt = new byte[_reportLength];
        pkt[0] = 0x04; pkt[3] = cmd; pkt[4] = (byte)length;
        pkt[5] = (byte)(offset & 0xFF); pkt[6] = (byte)(offset >> 8);
        data.CopyTo(pkt.AsSpan(8));
        int cs = 0;
        for (int i = 3; i < 64; i++)
            cs += pkt[i];
        pkt[1] = (byte)(cs & 0xFF); pkt[2] = (byte)((cs >> 8) & 0xFF);

        lock (_lock)
        {
            if (_stream == null && !Open())
                throw new DeviceException("keyboard not connected");
            var reply = new byte[_replyLength];
            try
            {
                for (int attempt = 0; attempt < ReplyAttempts; attempt++)   // every command is idempotent: resend if the reply got lost
                {
                    _stream!.Write(pkt);
                    long deadline = Stopwatch.GetTimestamp() + (long)(ReplyTimeout.TotalSeconds * Stopwatch.Frequency);
                    while (Stopwatch.GetTimestamp() < deadline)
                    {
                        int n;
                        try { n = _stream.Read(reply); }
                        catch (TimeoutException) { continue; }
                        // the reply echoes cmd and offset; anything else is a stale reply to another request
                        if (n >= 8 && reply[0] == 0x04 && reply[3] == cmd && reply[5] == pkt[5] && reply[6] == pkt[6])
                            return reply[..n];
                    }
                }
            }
            catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException)
            {
                Close();
                throw new DeviceException(e.Message, e);
            }
        }
        throw new DeviceException($"no reply to command 0x{cmd:x2}");
    }

    byte[] Read(byte cmd, int offset, int size)
    {
        var output = new byte[size];
        for (int pos = 0; pos < size;)
        {
            int n = Math.Min(MaxData, size - pos);
            Command(cmd, n, offset + pos).AsSpan(8, n).CopyTo(output.AsSpan(pos));
            pos += n;
        }
        return output;
    }

    /// <summary>Write in 56-byte packets, skipping packets identical to <paramref name="old"/>.</summary>
    void Write(byte cmd, int offset, ReadOnlySpan<byte> data, ReadOnlySpan<byte> old = default)
    {
        for (int pos = 0; pos < data.Length; pos += MaxData)
        {
            var chunk = data.Slice(pos, Math.Min(MaxData, data.Length - pos));
            if (old.Length >= pos + chunk.Length && old.Slice(pos, chunk.Length).SequenceEqual(chunk))
                continue;
            var r = Command(cmd, chunk.Length, offset + pos, chunk);
            FlashWrites++;
            if (r[7] == StatusError)
                throw new DeviceException($"keyboard rejected write 0x{cmd:x2} @0x{offset + pos:x}");
        }
    }

    // -- stock firmware --------------------------------------------------------

    public int ActiveProfile()
    {
        int p = Command(CmdReadHeader, 1, HeaderProfile)[8];
        return p < Profiles ? p : 0;
    }

    public Params ReadParams(int profile) => Params.FromBytes(Read(CmdReadParams, profile * ProfileParamsSize, 8));

    public void WriteParams(int profile, Params p, Params? old = null) =>
        Write(CmdWriteParams, profile * ProfileParamsSize, p.ToBytes(), old?.ToBytes());

    /// <summary>Side strips: (mode 0-5, speed, color index 0-7) at profile bytes 0x14-0x16.</summary>
    public byte[] ReadSide(int profile) => Read(CmdReadParams, profile * ProfileParamsSize + SideOffset, 3);

    public void WriteSide(int profile, byte[] side, byte[]? old = null) =>
        Write(CmdWriteParams, profile * ProfileParamsSize + SideOffset, side, old);

    /// <summary>Per-key colours of the Custom effect: 126 x (R, G, B).</summary>
    public Rgb[] ReadCustom(int profile)
    {
        var raw = Read(CmdReadCustom, profile * ProfileCustomSize, CustomSize);
        var colors = new Rgb[NumLeds];
        for (int i = 0; i < NumLeds; i++)
            colors[i] = new Rgb(raw[3 * i], raw[3 * i + 1], raw[3 * i + 2]);
        return colors;
    }

    public void WriteCustom(int profile, Rgb[] colors, Rgb[]? old = null) =>
        Write(CmdWriteCustom, profile * ProfileCustomSize, Flat(colors), old == null ? default : Flat(old));

    static byte[] Flat(Rgb[] colors)
    {
        var b = new byte[colors.Length * 3];
        for (int i = 0; i < colors.Length; i++)
            (b[3 * i], b[3 * i + 1], b[3 * i + 2]) = (colors[i].R, colors[i].G, colors[i].B);
        return b;
    }

    public byte[] ReadKeymap(int profile) => Read(CmdReadKeymap, profile * KeymapSize, KeymapSize);

    public void WriteKeymap(int profile, byte[] keymap, byte[]? old = null) =>
        Write(CmdWriteKeymap, profile * KeymapSize, keymap, old);

    /// <summary>The factory keymap the firmware restores on a reset (read-only table in firmware).</summary>
    public byte[] ReadDefaultKeymap() => Read(CmdReadRom, 0, KeymapSize);

    public byte[] ReadMacros(int size) => Read(CmdReadMacros, 0, size);

    public void WriteMacros(byte[] image, byte[]? old = null) => Write(CmdWriteMacros, 0, image, old);

    // -- patched firmware ------------------------------------------------------

    /// <summary>Stock firmware answers unknown commands with status 0xFF; length 0 = "direct off".</summary>
    public bool SupportsDirect() => Command(CmdDirect, 0, 0)[7] != StatusError;

    /// <summary>
    /// Stream one frame (126 x RGB) to RAM. Plane order in RAM is R, B, G.
    /// Only changed packets are sent, except that every packet goes out at least
    /// once per DirectKeepalive so the firmware's ~2.5 s fallback timer never fires.
    /// </summary>
    public void DirectFrame(ReadOnlySpan<Rgb> colors)
    {
        var planes = new byte[CustomSize];
        for (int i = 0; i < NumLeds; i++)
        {
            var c = colors[i];
            planes[i] = c.R;
            planes[NumLeds + i] = c.B;
            planes[2 * NumLeds + i] = c.G;
        }
        foreach (int i in SideLeds)            // side strips are wired R, B, G
            (planes[NumLeds + i], planes[2 * NumLeds + i]) = (colors[i].G, colors[i].B);

        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            bool full = _directLast == null || Stopwatch.GetElapsedTime(_directSentAt, now) >= DirectKeepalive;
            for (int pos = 0; pos < planes.Length; pos += MaxData)
            {
                var chunk = planes.AsSpan(pos, Math.Min(MaxData, planes.Length - pos));
                if (full || !_directLast!.AsSpan(pos, chunk.Length).SequenceEqual(chunk))
                    Command(CmdDirect, chunk.Length, pos, chunk);
            }
            if (full)
                _directSentAt = now;
            _directLast = planes;
        }
    }

    public void DirectOff()
    {
        lock (_lock)
        {
            _directLast = null;
            _directSentAt = 0;
            Command(CmdDirect, 0, 0);
        }
    }
}
