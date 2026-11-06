using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GerGit.Core;

/// <summary>Where a rule is stored: Shared = `&lt;working tree&gt;/.issuetracker` (can be committed),
/// Local = `&lt;common git dir&gt;/issuetracker`.</summary>
public enum IssueRuleLevel { Shared, Local }

/// <summary>An issue tracker rule: name, level, pattern, URL template.</summary>
public sealed record IssueRule(string Name, IssueRuleLevel Level, string Regex, string Url);

/// <summary>A link inside a text: the characters [Start, Start + Length) open <see cref="Url"/> (always http or https).</summary>
public readonly record struct TextLink(int Start, int Length, string Url);

/// <summary>A text with its links (offsets into <see cref="Text"/>).</summary>
public sealed record LinkedText(string Text, IReadOnlyList<TextLink> Links);

/// <summary>The rules of one file and whether the whole file was read (no parse error, not cut at the size limit).</summary>
public sealed record IssueRuleFile(List<IssueRule> Rules, bool Complete);

/// <summary>
/// Issue tracker rule files and link matching. The files come from the repository, so nothing in
/// them is trusted:
/// - files are read only when they are regular files (no link, FIFO or device), at most 64 KB; values longer than
///   <see cref="MaxValueLength"/> drop their rule;
/// - patterns run with the linear NonBacktracking engine when it supports them, else with a match timeout; invalid
///   ones are skipped and logged;
/// - per text: the first <see cref="TextLimit"/> characters, one time budget for all rules, at most
///   <see cref="MaxLinks"/> links, no URL longer than <see cref="MaxUrlLength"/>;
/// - only http(s) URLs without user info become links, and a rule never links text that is itself a plain URL.
/// Design choice: the local file is `&lt;common git dir&gt;/issuetracker`, shared by all worktrees.
/// </summary>
public static partial class IssueTracker
{
    /// <summary>Per pattern and text for patterns NonBacktracking cannot run (backreferences, lookarounds, …).</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>All rules of one text together; matching stops (logged) when it is used up.</summary>
    public static readonly TimeSpan TextBudget = TimeSpan.FromMilliseconds(250);

    public const int MaxLinks = 500, MaxUrlLength = 2048, MaxValueLength = 2048, TextLimit = 64 * 1024;

    internal const int FileLimit = 64 * 1024;

    static readonly string[] HeaderLines = ["# Integration with Issue Tracker", "#", "# (note that '\\' need to be escaped)."];

    public static string FileOf(RepoInfo info, IssueRuleLevel level) => level == IssueRuleLevel.Shared
        ? Path.Combine(info.WorkTree, ".issuetracker")
        : Path.Combine(info.CommonDir, "issuetracker");

    /// <summary>The rules of both files, shared first.</summary>
    public static List<IssueRule> Read(RepoInfo info) =>
        [.. ReadFile(info, IssueRuleLevel.Shared).Rules, .. ReadFile(info, IssueRuleLevel.Local).Rules];

    public static IssueRuleFile ReadFile(RepoInfo info, IssueRuleLevel level)
    {
        var path = FileOf(info, level);
        // Limitation: the type is checked before the open (statx), so a file swapped for a FIFO in between could still
        // block; callers read off the UI thread (see RepositoryScan.IsRegularFile for the same limit).
        if (!RepositoryScan.IsRegularFile(path)) return new([], true);
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[FileLimit];
            int n = reader.ReadBlock(buffer, 0, buffer.Length);
            bool cut = reader.Peek() >= 0;
            if (cut) Trace.WriteLine($"{path}: larger than {FileLimit} characters, the rest is ignored");
            var rules = Parse(new string(buffer, 0, n), level, out var complete);
            return new(rules, complete && !cut);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Cannot read {path}: {e.Message}");
            return new([], false);
        }
    }

    public static List<IssueRule> Parse(string text, IssueRuleLevel level) => Parse(text, level, out _);

    /// <summary>Parsing: trimmed lines, `#` and empty lines skipped, `[issuetracker "&lt;name&gt;"]`
    /// sections with `regex = "…"` and `url = "…"` (`\\` = one backslash); other sections are skipped, incomplete rules
    /// and rules with a value longer than <see cref="MaxValueLength"/> dropped, and a line that is none of these stops
    /// the reading (the rules so far are kept, <paramref name="complete"/> = false).</summary>
    public static List<IssueRule> Parse(string text, IssueRuleLevel level, out bool complete)
    {
        var rules = new List<IssueRule>();
        string? name = null, regex = null, url = null;
        bool skipping = false, dropped = false;
        complete = true;
        void Flush()
        {
            if (name is { Length: > 0 } && regex is not null && url is not null)
            {
                if (name.Length > MaxValueLength || regex.Length > MaxValueLength || url.Length > MaxValueLength)
                {
                    Trace.WriteLine($"Issue tracker rules: a value of '{name[..Math.Min(name.Length, 80)]}' is longer than {MaxValueLength} characters, the rule is skipped");
                    dropped = true; // a rewrite would lose it: the file counts as not read completely
                }
                else rules.Add(new(name, level, regex, url));
            }
            (name, regex, url) = (null, null, null);
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (SectionRegex().Match(line) is { Success: true } section)
            {
                Flush();
                skipping = false;
                name = Unescape(section.Groups[1].Value);
                continue;
            }
            if (line.StartsWith('['))
            {
                Flush();
                skipping = true; // another section keyword
                continue;
            }
            if (EntryRegex().Match(line) is not { Success: true } entry)
            {
                Trace.WriteLine($"Issue tracker rules: cannot parse '{line[..Math.Min(line.Length, 80)]}', the rest of the file is ignored");
                complete = false;
                break;
            }
            if (skipping || name is null) continue;
            var value = Unescape(entry.Groups[2].Value);
            if (entry.Groups[1].Value == "regex") regex = value;
            else if (entry.Groups[1].Value == "url") url = value;
        }
        Flush();
        complete &= !dropped;
        return rules;
    }

    static string Unescape(string value) => value.Replace(@"\\", @"\");

    /// <summary>Writing: the header comment, a blank line, then one section per rule (backslashes doubled).
    /// Line breaks in a value become spaces, so a value can never start a new section.</summary>
    public static string Format(IEnumerable<IssueRule> rules)
    {
        static string Escape(string value) => value.Replace(@"\", @"\\").Replace('\r', ' ').Replace('\n', ' ');
        var sb = new StringBuilder();
        foreach (var h in HeaderLines) sb.Append(h).Append('\n');
        sb.Append('\n');
        foreach (var r in rules)
            sb.Append($"[issuetracker \"{Escape(r.Name)}\"]\n  regex = \"{Escape(r.Regex)}\"\n  url = \"{Escape(r.Url)}\"\n");
        return sb.ToString();
    }

    /// <summary>Rewrites the file of this level; a level without rules deletes its file. With
    /// <paramref name="keepBackup"/> (a file that was not read completely) the old local file is renamed to
    /// `&lt;file&gt;.bak` first, so nothing GerGit could not read is lost. The shared file gets no backup: it is tracked by
    /// git (recoverable there), and a `.bak` would land in the working tree as an untracked file.</summary>
    public static void Write(RepoInfo info, IssueRuleLevel level, IReadOnlyList<IssueRule> rules, bool keepBackup = false)
    {
        var path = FileOf(info, level);
        if (keepBackup && level == IssueRuleLevel.Local && File.Exists(path))
        {
            File.Move(path, path + ".bak", overwrite: true); // a rename: a link at ".bak" is replaced, never written
            Trace.WriteLine($"{path} was not read completely; the old file is kept as {path}.bak");
        }
        if (rules.Count == 0) File.Delete(path); // a link is removed itself, never its target
        else SafeFile.WriteAllText(path, Format(rules));
    }

    /// <summary>The pattern in multi-line mode: with the linear NonBacktracking engine when it supports the pattern, else
    /// with <paramref name="timeout"/> (default <see cref="MatchTimeout"/>). Null (logged) when it does not compile; an
    /// empty or too long pattern is invalid too.</summary>
    public static Regex? Compile(string pattern, bool log = true, TimeSpan? timeout = null)
    {
        if (pattern.Length == 0 || pattern.Length > MaxValueLength) return null;
        const RegexOptions options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        try
        {
            try { return new Regex(pattern, options | RegexOptions.NonBacktracking, timeout ?? MatchTimeout); }
            catch (NotSupportedException) { } // backreferences, lookarounds, atomic groups, too complex: backtracking
            return new Regex(pattern, options, timeout ?? MatchTimeout);
        }
        catch (ArgumentException e)
        {
            if (log) Trace.WriteLine($"Issue tracker rule: invalid pattern '{pattern[..Math.Min(pattern.Length, 200)]}': {e.Message}");
            return null;
        }
    }

    /// <summary>The checked form (<see cref="Uri.AbsoluteUri"/>) of an absolute http or https URL without user info, at
    /// most <see cref="MaxUrlLength"/> characters; null for every other text (file, javascript, mailto, …, and
    /// `https://github.com@evil.example/`, which shows one host and opens another).</summary>
    public static string? WebUrl(string? text) =>
        text is { Length: > 0 and <= MaxUrlLength } && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.UserInfo.Length == 0 && uri.Host.Length > 0
            ? uri.AbsoluteUri : null;

    public static bool IsWebUrl(string? text) => WebUrl(text) is not null;

    /// <summary>
    /// Matching: every match of every rule, the URL template with `$1`, `$2`, … replaced by the groups that
    /// matched (URL-escaped, "/" kept); a result that is no http(s) URL is dropped. Plain http(s) URLs are found first: a
    /// rule match that overlaps one is dropped (visible URL text always opens itself); with <paramref name="plainUrls"/>
    /// the URLs are links too. Overlaps: the earlier start wins, at the same start the longer match; a match starting
    /// inside an accepted one is dropped. Sorted by start; see the class summary for the limits.
    /// </summary>
    public static List<TextLink> Find(string text, IEnumerable<(Regex Regex, string Url)> rules, bool plainUrls, CancellationToken ct = default)
    {
        if (text.Length > TextLimit) text = text[..TextLimit];
        var found = new List<TextLink>();
        if (text.Length == 0) return found;
        var urls = FindUrls(text);
        var clock = Stopwatch.StartNew();
        foreach (var (regex, template) in rules)
        {
            if (found.Count >= MaxLinks) break; // the cap is for the whole text: the other rules do not run
            if (clock.Elapsed > TextBudget) return Resolve(Over(found), urls, plainUrls);
            try
            {
                for (var m = regex.Match(text); m.Success && found.Count < MaxLinks; m = m.NextMatch())
                {
                    ct.ThrowIfCancellationRequested();
                    if (clock.Elapsed > TextBudget) return Resolve(Over(found), urls, plainUrls);
                    if (m.Length == 0 || urls.Any(u => m.Index < u.Start + u.Length && u.Start < m.Index + m.Length)) continue;
                    if (Substitute(template, m) is not { } raw || WebUrl(raw) is not { } url) continue;
                    found.Add(new(m.Index, m.Length, url));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                Trace.WriteLine($"Issue tracker rule: pattern '{Short(regex)}' timed out after {regex.MatchTimeout.TotalMilliseconds} ms, skipped");
            }
        }
        return Resolve(found, urls, plainUrls);

        List<TextLink> Over(List<TextLink> f)
        {
            Trace.WriteLine($"Issue tracker rules: the {TextBudget.TotalMilliseconds} ms budget of a text is used up, the other matches are skipped");
            return f;
        }
    }

    static string Short(Regex regex) => regex.ToString() is var p && p.Length > 200 ? p[..200] + "…" : p;

    /// <summary>The template with the matched groups (URL-escaped, "/" kept); null as soon as the result gets longer than
    /// <see cref="MaxUrlLength"/> (so a template full of `$1` never builds a huge string).</summary>
    static string? Substitute(string template, Match m)
    {
        var sb = new StringBuilder();
        int pos = 0;
        for (var g = GroupRegex().Match(template); g.Success; g = g.NextMatch())
        {
            sb.Append(template, pos, g.Index - pos);
            pos = g.Index + g.Length;
            if (int.TryParse(g.Groups[1].ValueSpan, out var i) && i < m.Groups.Count && m.Groups[i].Success)
            {
                if (m.Groups[i].Length > MaxUrlLength) return null;
                sb.Append(Uri.EscapeDataString(m.Groups[i].Value).Replace("%2F", "/"));
            }
            else sb.Append(g.Value);
            if (sb.Length > MaxUrlLength) return null;
        }
        sb.Append(template, pos, template.Length - pos);
        return sb.Length > MaxUrlLength ? null : sb.ToString();
    }

    static List<TextLink> Resolve(List<TextLink> found, List<TextLink> urls, bool plainUrls)
    {
        if (plainUrls) found.AddRange(urls);
        var result = new List<TextLink>();
        int end = 0;
        foreach (var link in found.OrderBy(l => l.Start).ThenByDescending(l => l.Length))
        {
            if (link.Start < end) continue;
            result.Add(link);
            end = link.Start + link.Length;
            if (result.Count == MaxLinks) break;
        }
        return result;
    }

    /// <summary>Plain http(s) URLs (output views; also in commit messages), at most <see cref="MaxLinks"/>.
    /// Trailing punctuation and unbalanced closing brackets are not part of the URL; a candidate longer than
    /// <see cref="MaxUrlLength"/> is skipped. Linear in the text length.</summary>
    public static List<TextLink> FindUrls(string text)
    {
        var result = new List<TextLink>();
        for (var m = UrlRegex().Match(text); m.Success && result.Count < MaxLinks; m = m.NextMatch())
        {
            if (m.Length > MaxUrlLength) continue;
            var span = m.ValueSpan;
            int open = span.Count('('), close = span.Count(')'), openSquare = span.Count('['), closeSquare = span.Count(']');
            int end = span.Length;
            while (end > 0)
            {
                char last = span[end - 1];
                if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"') end--;
                else if (last == ')' && close > open) { close--; end--; }
                else if (last == ']' && closeSquare > openSquare) { closeSquare--; end--; }
                else break;
            }
            if (WebUrl(m.Value[..end]) is { } url) result.Add(new(m.Index, end, url));
        }
        return result;
    }

    [GeneratedRegex(@"^\[issuetracker\s+""(.*)""\]$")]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"^(\w+)\s*=\s*""(.*)""$")]
    private static partial Regex EntryRegex();

    [GeneratedRegex(@"\$(\d+)")]
    private static partial Regex GroupRegex();

    [GeneratedRegex(@"\bhttps?://[^\s<>""`]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}

/// <summary>Writes files that live in a repository without following planted links.</summary>
public static class SafeFile
{
    /// <summary>
    /// Atomic write: a new temp file in the same folder (created exclusively, so an existing file or link is never
    /// opened), then a rename over the target (a link at the target is replaced, its target is not written).
    /// Throws IOException when the folder is a symbolic link.
    /// </summary>
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (new DirectoryInfo(dir).LinkTarget is not null) throw new IOException($"'{dir}' is a symbolic link");
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var s = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                w.Write(content);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
    }
}
