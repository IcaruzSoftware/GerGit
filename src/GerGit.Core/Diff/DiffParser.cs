using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GerGit.Core.Diff;

/// <summary>Parsing the diff text (prose). Only the first `diff --git` section is used.</summary>
public static partial class DiffParser
{
    public const string SrcPrefix = "gergitsrc/";
    public const string DstPrefix = "gergitdst/";
    public const int MinifiedLineLength = 2048;

    public static FileDiff Parse(string text)
    {
        var lines = text.Split('\n');
        int count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

        int i = 0;
        while (i < count && !lines[i].StartsWith("diff --git ", StringComparison.Ordinal)) i++;
        if (i == count) return new FileDiff(null, null, null, null, null, null, false, false, false, 0, []);

        var (oldPath, newPath) = ParseDiffGitLine(lines[i++]);
        int? oldMode = null, newMode = null;
        string? oldBlob = null, newBlob = null;
        bool binary = false;

        // Header lines up to the first hunk.
        for (; i < count && !lines[i].StartsWith("@@", StringComparison.Ordinal); i++)
        {
            var l = lines[i];
            if (l.StartsWith("diff --git ", StringComparison.Ordinal)) { count = i; break; }
            if (l.StartsWith("old mode ", StringComparison.Ordinal)) oldMode = ParseMode(l[9..]);
            else if (l.StartsWith("new mode ", StringComparison.Ordinal)) newMode = ParseMode(l[9..]);
            else if (l.StartsWith("deleted file mode ", StringComparison.Ordinal)) { oldMode = ParseMode(l[18..]); newPath = null; }
            else if (l.StartsWith("new file mode ", StringComparison.Ordinal)) { newMode = ParseMode(l[14..]); oldPath = null; }
            else if (l.StartsWith("index ", StringComparison.Ordinal))
            {
                var parts = l[6..].Split(' ');
                var shas = parts[0].Split("..");
                if (shas.Length == 2) (oldBlob, newBlob) = (shas[0], shas[1]);
                if (parts.Length > 1) oldMode = newMode = ParseMode(parts[1]);
            }
            else if (l.StartsWith("rename from ", StringComparison.Ordinal)) oldPath = Unquote(l[12..]);
            else if (l.StartsWith("rename to ", StringComparison.Ordinal)) newPath = Unquote(l[10..]);
            else if (l.StartsWith("copy from ", StringComparison.Ordinal)) oldPath = Unquote(l[10..]);
            else if (l.StartsWith("copy to ", StringComparison.Ordinal)) newPath = Unquote(l[8..]);
            else if (l.StartsWith("Binary files ", StringComparison.Ordinal) && l.EndsWith(" differ", StringComparison.Ordinal)) binary = true;
        }

        var hunks = new List<DiffHunk>();
        int totalLength = 0;
        bool minified = false;
        while (i < count)
        {
            var l = lines[i];
            if (l.StartsWith("diff --git ", StringComparison.Ordinal)) break; // only the first section
            var m = HunkHeaderRegex().Match(l);
            if (!m.Success) { i++; continue; }
            int oldStart = int.Parse(m.Groups[1].Value), newStart = int.Parse(m.Groups[3].Value);
            int oldLen = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
            int newLen = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;
            string? context = m.Groups[5].Value.Length > 0 ? m.Groups[5].Value : null;

            var body = new List<DiffLine>();
            int o = oldStart, n = newStart;
            for (i++; i < count; i++)
            {
                l = lines[i];
                if (l.StartsWith("@@", StringComparison.Ordinal) || l.StartsWith("diff --git ", StringComparison.Ordinal)) break;
                char marker = l.Length > 0 ? l[0] : ' '; // an empty line is a context line under diff.suppressBlankEmpty
                var t = l.Length > 0 ? l[1..] : "";
                switch (marker)
                {
                    case ' ': body.Add(new DiffLine(DiffLineKind.Context, t, o++, n++)); break;
                    case '-': body.Add(new DiffLine(DiffLineKind.Removed, t, o++, null)); break;
                    case '+': body.Add(new DiffLine(DiffLineKind.Added, t, null, n++)); break;
                    case '\\':
                        if (body.Count > 0 && t.TrimStart().StartsWith("No newline at end of file", StringComparison.Ordinal))
                            body[^1] = body[^1] with { NoNewlineAtEnd = true };
                        continue;
                    default: continue;
                }
                totalLength += t.Length;
                minified |= t.Length >= MinifiedLineLength;
            }
            hunks.Add(new DiffHunk(oldStart, oldLen, newStart, newLen, context, body));
        }

        bool submodule = oldMode == 160000 || newMode == 160000;
        return new FileDiff(oldPath, newPath, oldMode, newMode, oldBlob, newBlob, binary, submodule, minified, totalLength, hunks);
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$")]
    private static partial Regex HunkHeaderRegex();

    // Modes are kept as their octal digits read as a decimal number (100644), which is what the UI prints.
    static int? ParseMode(string s) => int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>`diff --git gergitsrc/a gergitdst/b`; each side may be C-quoted. The unique prefixes make the split safe.</summary>
    static (string?, string?) ParseDiffGitLine(string line)
    {
        var rest = line["diff --git ".Length..];
        string a, b;
        if (rest.StartsWith('"'))
        {
            int end = ClosingQuote(rest);
            a = Unquote(rest[..(end + 1)]);
            b = Unquote(rest[(end + 2)..]);
        }
        else
        {
            int sep = rest.IndexOf(" " + DstPrefix, StringComparison.Ordinal);
            if (sep < 0) sep = rest.IndexOf(" \"" + DstPrefix, StringComparison.Ordinal);
            if (sep < 0) return (null, null);
            a = rest[..sep];
            b = Unquote(rest[(sep + 1)..]);
        }
        return (StripPrefix(a, SrcPrefix), StripPrefix(b, DstPrefix));
    }

    static string StripPrefix(string s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal) ? s[prefix.Length..] : s;

    static int ClosingQuote(string s)
    {
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] == '\\') i++;
            else if (s[i] == '"') return i;
        }
        return s.Length - 1;
    }

    /// <summary>Undo git's C-quoting ("a\tb", octal UTF-8 bytes like \303\244). Unquoted input is returned as is.</summary>
    internal static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return s;
        var bytes = new List<byte>();
        for (int i = 1; i < s.Length - 1; i++)
        {
            if (s[i] != '\\')
            {
                int end = s.IndexOf('\\', i);
                if (end < 0) end = s.Length - 1;
                bytes.AddRange(Encoding.UTF8.GetBytes(s[i..end]));
                i = end - 1;
                continue;
            }
            char c = s[++i];
            if (c is >= '0' and <= '7' && i + 2 < s.Length - 1)
            {
                bytes.Add(Convert.ToByte(s.Substring(i, 3), 8));
                i += 2;
                continue;
            }
            bytes.Add(c switch
            {
                'a' => 7, 'b' => 8, 't' => 9, 'n' => 10, 'v' => 11, 'f' => 12, 'r' => 13,
                _ => (byte)c, // \" and \\
            });
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
