namespace GerGit.Core;

/// <summary>Message handling: subject/description fields ↔ commit message.</summary>
public static class CommitMessage
{
    public static string Build(string subject, string description) =>
        description.Length == 0 ? subject.Trim('\r', '\n') : subject + "\n\n" + description;

    public static (string Subject, string Description) Split(string message)
    {
        int lf = message.IndexOf("\n\n", StringComparison.Ordinal), crlf = message.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (lf < 0 && crlf < 0) return (message.Trim('\r', '\n'), "");
        return lf >= 0 && (crlf < 0 || lf < crlf) ? (message[..lf], message[(lf + 2)..]) : (message[..crlf], message[(crlf + 4)..]);
    }
}
