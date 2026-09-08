using System.Text;

namespace SteamCloudTamper.Core;

/// <summary>
/// Barcode lane: parked save files carry a self-identifying trailer so any fresh install
/// can tell "which game lives in which bucket" in milliseconds (tail-4KB scan only).
///
/// Payload format (the ONLY game identifier - the storage appid is implied by the bucket):
///   &lt;originalGameAppId&gt;|&lt;steamUserid3&gt;|&lt;DDMMYYYY&gt;      e.g. 588650|1201110076|09082026
///
/// The payload IS the barcode data; <see cref="RenderBarcode"/> renders its deterministic
/// pictogram (visual barcode). The trailer also carries a CRC32 so corruption is detected.
/// Unparking strips the trailer -> the original save comes back byte-identical.
/// </summary>
public static class Barcode
{
    public const string Magic = "SCTB1";
    public const int TailWindowBytes = 4096;

    public const char Sep = '|';

    public const int TrailerOverheadBytes = 8 + 4 + 5; // len fields + crc + magic "SCTB1"

    public static byte[] PackTrailer(string gameAppId, string userId3, DateOnly date, string? originalName = null)
    {
        var payload = $"{gameAppId}{Sep}{userId3}{Sep}{date:ddMMyyyy}";
        // OPTIONAL 4th field: the original file name. Only present when it can be
        // embedded unambiguously (no separator inside); lets a fresh-install rebuild
        // recover the true name even for stealth/hashed stored names. Older files
        // without it keep decoding via backward-compatible 2/3-part parsing.
        if (!string.IsNullOrEmpty(originalName)
            && !originalName.Contains(Sep)
            && originalName.Length <= 1024)
            payload += $"{Sep}{originalName}";
        return PackTrailer(payload);
    }

    public static byte[] PackTrailer(string payload)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var crc = Crc32(payloadBytes);
        using var ms = new MemoryStream(payloadBytes.Length + TrailerOverheadBytes);
        ms.Write(Encoding.ASCII.GetBytes(Magic));
        WriteU32(ms, (uint)(payloadBytes.Length + 17)); // full trailer: magic5+len4+paylen4+payload+crc4
        WriteU32(ms, (uint)payloadBytes.Length);
        ms.Write(payloadBytes);
        WriteU32(ms, crc);
        return ms.ToArray();
    }

    public static int TrailerLength(ReadOnlySpan<byte> trailer)
        => trailer.Length >= Magic.Length + 4
           && Encoding.ASCII.GetString(trailer[..Magic.Length]) == Magic
               ? (int)BitConverter.ToUInt32(trailer[Magic.Length..]) : 0;

    public static bool TryDecode(ReadOnlySpan<byte> trailer, out string payload)
    {
        payload = "";
        if (trailer.Length < Magic.Length + 8) return false;
        if (Encoding.ASCII.GetString(trailer[..Magic.Length]) != Magic) return false;

        var total = BitConverter.ToUInt32(trailer[Magic.Length..]);
        if (total > trailer.Length) return false;

        var payloadLen = (int)BitConverter.ToUInt32(trailer[(Magic.Length + 4)..]);
        if (payloadLen < 1 || payloadLen + Magic.Length + 8 + 4 > trailer.Length) return false;

        var payloadStart = Magic.Length + 8;
        var payloadSpan = trailer.Slice(payloadStart, payloadLen);
        var crc = BitConverter.ToUInt32(trailer.Slice(payloadStart + payloadLen, 4));
        if (Crc32(payloadSpan) != crc) return false;

        try { payload = Encoding.UTF8.GetString(payloadSpan); } catch { return false; }
        var parts = payload.Split(Sep);
        // 2 parts: appid|uid   3 parts: appid|uid|ddMMyyyy   4 parts: + originalName
        return parts.Length is >= 2 and <= 4
               && uint.TryParse(parts[0], out _)
               && (parts.Length < 3 || parts[2].Length == 8);
    }

    public static (uint GameAppId, string? UserId3, DateOnly? TaggedOn, string? OriginalName) Parse(string payload)
    {
        var parts = payload.Split(Sep);
        if (parts.Length < 2 || !uint.TryParse(parts[0], out var app)) return (0, null, null, null);
        var date = parts.Length >= 3 && DateTime.TryParseExact(parts[2], "ddMMyyyy", null, System.Globalization.DateTimeStyles.None, out var d)
            ? (DateOnly?)DateOnly.FromDateTime(d) : null;
        // name may itself contain the separator; everything after the (appid,uid,date)
        // prefix counts as the name so embedded '|' never shifts field positions
        var name = parts.Length > 3 ? string.Join(Sep, parts[3..]) : null;
        return (app, parts[1], date, name);
    }

    /// <summary>Finds a valid trailer inside <paramref name="tail"/> (the last bytes of a file).</summary>
    public static bool TryDecodeTail(ReadOnlySpan<byte> tail, out string payload, out int trailerByteLen)
    {
        trailerByteLen = 0;
        // trailer is the LAST chunk: find the magic "SCTB1" scanning backwards in the window
        var magic = Encoding.ASCII.GetBytes(Magic);
        for (var i = tail.Length - magic.Length; i >= 0; i--)
        {
            var match = true;
            for (var m = 0; m < magic.Length; m++)
            {
                if (tail[i + m] != magic[m]) { match = false; break; }
            }
            if (!match) continue;

            var candidate = tail[i..];
            var len = TrailerLength(candidate);
            if (len > 0 && len <= candidate.Length && TryDecode(candidate[..len], out payload))
            {
                trailerByteLen = len;
                return true;
            }
            // total wrong -> keep scanning
        }
        payload = "";
        return false;
    }

    public static byte[] StripTrailer(byte[] fileData, int trailerLen)
        => fileData.AsSpan(0, fileData.Length - trailerLen).ToArray();

    /// <summary>Deterministic visual barcode pictogram of the payload (Code-39-ish guards + 8-bit byte modules).</summary>
    public static List<string> RenderBarcode(string payload, int height = 21, char dark = '\u2588', char light = ' ')
    {
        var bits = new List<bool>();
        foreach (var c in Encoding.ASCII.GetBytes(payload))
        {
            for (var b = 7; b >= 0; b--) bits.Add(((c >> b) & 1) == 1);
            bits.Add(true); // separator
        }
        // guards: start (1 0 1), payload, checksum-mod, stop (1 0 1 0)
        var line = new StringBuilder();
        void Guard() { line.Append(dark); line.Append(light); line.Append(dark); }
        void Emit(bool bit) => line.Append(bit ? dark : light);

        var lines = new List<string>(height);

        for (var row = 0; row < height; row++)
        {
            line.Clear();
            Guard();
            var from = row * (bits.Count / height);
            var until = row == height - 1 ? bits.Count : from + bits.Count / height;
            var taken = 0;
            for (var i = from; i < until && i < bits.Count; i++)
            {
                Emit(bits[i]);
                taken++;
            }
            // checksum filler to keep width fixed
            var checksum = (payload.Length + row) % 16;
            for (var i = taken; i < bits.Count / height; i++) Emit(((checksum >> (i % 4)) & 1) == 1);
            Guard();
            lines.Add(line.ToString());
        }
        return lines;
    }

    private static void WriteU32(Stream s, uint v)
    {
        s.WriteByte((byte)v);
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 24));
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            uint crc = 0xFFFFFFFF;
            foreach (var b in data)
            {
                crc ^= b;
                for (var i = 0; i < 8; i++)
                    crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1)));
            }
            return ~crc;
        }
    }
}