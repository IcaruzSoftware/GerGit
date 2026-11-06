namespace GerGit.Core;

/// <summary>Reference-name rules of the reference text box.</summary>
public static class RefName
{
    /// <summary>
    /// The 11 rules plus a leading "-", first failing wins. Valid = submit allowed; Message = the footer warning (null when valid, and also
    /// for an empty name, which only disables Submit).
    /// Not built yet: the Git Flow variant of the rules.
    /// </summary>
    public static (bool Valid, string? Message) Validate(string name)
    {
        if (name.Length == 0) return (false, null);
        string? message =
            name.EndsWith('.') ? "Name cannot end with '.'"
            : name.Contains("..") ? "Name cannot contain '..'"
            : name.IndexOfAny(['~', '^', ':']) >= 0 ? "Name cannot contain ASCII control characters '~', '^' or ':'"
            : name.IndexOfAny(['?', '*', '[']) >= 0 ? "Name cannot contain '?', '*' or '['"
            : name.Contains("@{") ? "Name cannot contain '@{'"
            : name.Contains('\\') ? "Name cannot contain '\\'"
            : name == "@" ? "Name cannot be the single '@' character"
            : name.StartsWith('/') ? "Name cannot begin with '/'"
            : name.StartsWith('-') ? "Name cannot begin with '-'" // git would read it as an option
            : name.Contains("/.") ? "Name cannot contain '/.'"
            : name.Contains('/') && name.EndsWith(".lock", StringComparison.Ordinal) ? "Name cannot contain '/' and end with '.lock'"
            : name.Contains("//") ? "Name cannot contain multiple consecutive '/'"
            : null;
        return (message is null, message);
    }

    // Multi-character sequences first, so "a..b" becomes "a-b" and not "a--b".
    static readonly string[] PasteReplaced =
        ["\r\n", "\n", "\r", " ", "..", "//", "@{", "\\", "/.", ".lock", "~", "^", ":", "?", "*", "["];

    /// <summary>Pasted text with every forbidden sequence replaced by <paramref name="replacement"/>; a lone "@" too.</summary>
    public static string CleanPaste(string text, string replacement)
    {
        if (text == "@") return replacement;
        foreach (var s in PasteReplaced) text = text.Replace(s, replacement, StringComparison.Ordinal);
        return text;
    }
}
