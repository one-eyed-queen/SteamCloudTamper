using System.Globalization;
using System.Text;

namespace SteamCloudTamper.Core;

/// <summary>
/// A hand-rolled, dependency-free TOML subset reader/writer for SCT's routing
/// config (sections, key=value, basic/literal strings, integers, booleans,
/// arrays, inline tables, comments). Not a full TOML 1.0 parser — by design.
/// Values normalize into .NET primitives: string, long, bool, List&lt;object?&gt;,
/// or Dictionary&lt;string, object?&gt; for inline tables. Dotted keys ("a.b=1")
/// collapse into nested Dicts so the model layer can read them naturally.
/// </summary>
public static class Toml
{
    public sealed class TomlException : Exception
    {
        public TomlException(string message, int line) : base($"toml:{line}: {message}") { }
    }

    /// <summary>Parses TOML text into a nested Dictionary&lt;string, object?&gt;.</summary>
    public static Dictionary<string, object?> Parse(string text)
    {
        var root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string? currentSection = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = StripComment(lines[i]).Trim();
            if (raw.Length == 0) continue;

            if (raw.StartsWith("[[", StringComparison.Ordinal))
            {
                // array-of-tables is unsupported in this subset - skip
                continue;
            }

            if (raw[0] == '[')
            {
                var section = ParseSectionHeader(raw, i);
                currentSection = section;
                // EnsureTable nests dotted headers ("routing.options" -> routing . options),
                // matching the writer's structure and the model readers.
                EnsureTable(root, section);
                continue;
            }

            var eq = raw.IndexOf('=');
            if (eq <= 0) throw new TomlException($"expected 'key = value', got: {raw}", i + 1);

            var key = raw[..eq].Trim().Trim('"').Trim();
            var valText = raw[(eq + 1)..].Trim();
            var value = ParseValue(valText, i);

            var target = currentSection is null
                ? root
                : (Dictionary<string, object?>)EnsureTable(root, currentSection);

            SetKeyPath(target, key, value, i);
        }

        return root;
    }

    private static string StripComment(string line)
    {
        bool inStr = false, inLit = false, escaped = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inStr)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inStr = false;
            }
            else if (inLit)
            {
                if (c == '\'') inLit = false;
            }
            else if (c == '"') inStr = true;
            else if (c == '\'') inLit = true;
            else if (c == '#') return line[..i];
        }
        return line;
    }

    private static string ParseSectionHeader(string raw, int line)
    {
        var inner = raw.Substring(1, raw.Length - (raw.EndsWith(']') ? 2 : 1)).Trim();
        if (inner.Length == 0) throw new TomlException("empty [section]", line + 1);
        // normalize dotted keys with quoted segments: [a.b."c"] -> a.b.c
        return string.Join('.', inner.Split('.').Select(seg => seg.Trim().Trim('"').Trim()));
    }

    private static object? ParseValue(string text, int line)
    {
        text = text.Trim();
        if (text.Length == 0) throw new TomlException("empty value", line + 1);

        // arrays
        if (text[0] == '[')
        {
            if (!text.EndsWith(']')) throw new TomlException("unterminated array", line + 1);
            var inner = text.Substring(1, text.Length - 2).Trim();
            var items = new List<object?>();
            if (inner.Length == 0) return items;
            foreach (var part in SplitTopLevel(inner, ','))
            {
                var p = part.Trim();
                items.Add(ParseValue(p, line));
            }
            return items;
        }

        // inline tables { k = v, ... }
        if (text[0] == '{')
        {
            if (!text.EndsWith('}')) throw new TomlException("unterminated inline table", line + 1);
            var inner = text.Substring(1, text.Length - 2).Trim();
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (inner.Length == 0) return dict;
            foreach (var part in SplitTopLevel(inner, ','))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) throw new TomlException("malformed inline table entry", line + 1);
                var k = part[..eq].Trim().Trim('"').Trim();
                dict[k] = ParseValue(part[(eq + 1)..], line);
            }
            return dict;
        }

        // strings
        if (text[0] == '"') return ParseBasicString(text, line);
        if (text[0] == '\'') return ParseLiteralString(text, line);

        // bools
        if (text is "true" or "false") return text == "true";

        // numbers (int subset; ignore float precision issues)
        if (IsInteger(text)) return long.Parse(text, CultureInfo.InvariantCulture);

        // bare-word passthrough (allows enum-ish tokens / paths without quotes)
        return text;
    }

    private static string ParseBasicString(string text, int line)
    {
        if (!text.EndsWith('"') || text.Length < 2) throw new TomlException("bad basic string", line + 1);
        var body = text.Substring(1, text.Length - 2);
        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c != '\\') { sb.Append(c); continue; }
            if (++i >= body.Length) throw new TomlException("bad escape", line + 1);
            switch (body[i])
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case '\'': sb.Append('\''); break;
                default:
                    if (body[i] == 'u' && i + 4 < body.Length && ushort.TryParse(body.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                    {
                        sb.Append((char)cp); i += 4;
                    }
                    else if (body[i] == 'U' && i + 8 < body.Length && uint.TryParse(body.Substring(i + 1, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp2))
                    {
                        sb.Append(char.ConvertFromUtf32((int)cp2)); i += 8;
                    }
                    else throw new TomlException($"unknown escape \\{body[i]}", line + 1);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string ParseLiteralString(string text, int line)
    {
        if (!text.EndsWith('\'') || text.Length < 2) throw new TomlException("bad literal string", line + 1);
        return text.Substring(1, text.Length - 2);
    }

    private static bool IsInteger(string t)
    {
        var s = t.TrimStart('+', '-');
        if (s.Length == 0) return false;
        foreach (var c in s)
            if (c is < '0' or > '9') return false;
        return true;
    }

    private static object EnsureTable(Dictionary<string, object?> root, string path)
    {
        var parts = path.Split('.');
        var cur = root;
        foreach (var part in parts)
        {
            if (!cur.TryGetValue(part, out var next) || next is not Dictionary<string, object?> d)
            {
                d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                cur[part] = d;
            }
            cur = d;
        }
        return cur;
    }

    private static void SetKeyPath(Dictionary<string, object?> table, string key, object? value, int line)
    {
        var parts = key.Split('.');
        var cur = table;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var p = parts[i].Trim().Trim('"').Trim();
            if (!cur.TryGetValue(p, out var next) || next is not Dictionary<string, object?> d)
            {
                d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                cur[p] = d;
            }
            cur = d;
        }
        var last = parts[^1].Trim().Trim('"').Trim();
        if (last.Length == 0) throw new TomlException("empty key", line + 1);
        cur[last] = value;
    }

    private static IEnumerable<string> SplitTopLevel(string text, char sep)
    {
        var start = 0;
        var depth = 0;
        bool inStr = false, inLit = false, escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inStr)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') inStr = false;
            }
            else if (inLit)
            {
                if (c == '\'') inLit = false;
            }
            else
            {
                if (c is '"') inStr = true;
                else if (c is '\'') inLit = true;
                else if (c is '[' or '{') depth++;
                else if (c is ']' or '}') depth--;
                else if (c == sep && depth == 0)
                {
                    yield return text[start..i];
                    start = i + 1;
                }
            }
        }
        yield return text[start..];
    }

    // ------------------------------------------------------------------
    // Writer
    // ------------------------------------------------------------------

    /// <summary>
    /// Serializes a nested Dict into TOML, emitting dotted [section] headers for
    /// sub-tables and bare key = value / arrays for leaf tables. Dicts that contain
    /// only sub-tables produce no header of their own (they are pure containers),
    /// which keeps the output human-editable.
    /// </summary>
    public static string Write(Dictionary<string, object?> root)
    {
        var sb = new StringBuilder();
        WriteSection(sb, null, root, topLevel: true);
        return sb.ToString();
    }

    private static void WriteSection(StringBuilder sb, string? path, Dictionary<string, object?> table, bool topLevel)
    {
        var leaves = table.Where(kv => kv.Value is not Dictionary<string, object?>).ToList();
        var subs = table.Where(kv => kv.Value is Dictionary<string, object?>).ToList();

        if (leaves.Count > 0)
        {
            if (!topLevel)
            {
                sb.Append('[').Append(path).AppendLine("]");
            }
            foreach (var (key, value) in leaves)
            {
                sb.Append(topLevel ? key : "  ").Append(" = ").AppendLine(WriteValue(value));
            }
            sb.AppendLine();
            topLevel = false;
        }

        foreach (var (key, value) in subs)
        {
            var subPath = path is null ? key : path + "." + key;
            WriteSection(sb, subPath, (Dictionary<string, object?>)value, false);
        }
    }

    private static string WriteValue(object? v) => v switch
    {
        null => "\"\"",
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"",
        List<object?> list => "[" + string.Join(", ", list.Select(WriteValue)) + "]",
        Dictionary<string, object?> d => "{ " + string.Join(", ", d.Select(kv => kv.Key + " = " + WriteValue(kv.Value))) + " }",
        _ => "\"" + v.ToString()?.Replace("\"", "\\\"") + "\"",
    };
}