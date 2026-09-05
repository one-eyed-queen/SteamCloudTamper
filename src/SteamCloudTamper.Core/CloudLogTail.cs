using System.Text;

namespace SteamCloudTamper.Core;

/// <summary>
/// Incremental tail reader for the Steam client's cloud_log.txt with byte-exact
/// watermark tracking. Every call returns only the appid-matching lines that
/// became fully available since the last watermark, and advances the watermark
/// by the exact number of bytes consumed - so non-matching lines and the
/// \r\n terminator never drift the offset and no line is ever re-read or skipped.
/// A trailing partial line (no newline yet) is left un-consumed for the next poll.
/// </summary>
public static class CloudLogTail
{
    public static List<string> ReadMatchingLines(string logPath, ref long watermark, uint appId)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return list;
        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            if (length <= watermark) return list;

            fs.Seek(watermark, SeekOrigin.Begin);
            var count = length - watermark;
            var bytes = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = fs.Read(bytes, read, (int)(count - read));
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) return list;

            var text = Encoding.UTF8.GetString(bytes, 0, read);
            var hadFinalNewline = text.EndsWith('\n');
            var pieces = text.Split('\n');
            var fullLines = hadFinalNewline ? pieces.Length : pieces.Length - 1;

            var consumed = 0L;
            for (var i = 0; i < fullLines; i++)
            {
                var piece = pieces[i].TrimEnd('\r');
                consumed += Encoding.UTF8.GetByteCount(pieces[i]) + 1;
                if (piece.Contains($"[AppID {appId}]", StringComparison.OrdinalIgnoreCase)
                    || piece.Contains($"[appid {appId}]", StringComparison.OrdinalIgnoreCase))
                    list.Add(piece);
            }
            watermark += consumed;
        }
        catch (IOException)
        {
            // cloud_log can be rotated/locked mid-read by the running client - skip the poll
        }
        return list;
    }
}