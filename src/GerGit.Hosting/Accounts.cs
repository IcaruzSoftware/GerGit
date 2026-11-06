using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GerGit.Hosting;

/// <summary>Remote / account types (Supported account types, "Display names of remote types").
/// Accounts exist only for GitHub, GitLab and GitLabServer; the others are host-name-only kinds for
/// icons and web URLs. Not built yet: Bitbucket / Bitbucket Server / Gitea / GitHub Enterprise Server accounts.</summary>
public enum HostType { Custom, GitHub, GitLab, GitLabServer, Bitbucket, Azure, VisualStudio }

public enum AuthType { AccessToken, OAuth }

public static class HostTypes
{
    /// <summary>Display names of remote types; null for custom hosts.</summary>
    public static string? DisplayName(HostType type) => type switch
    {
        HostType.GitHub => "GitHub",
        HostType.GitLab => "GitLab",
        HostType.GitLabServer => "GitLab Server",
        HostType.Bitbucket => "Bitbucket",
        HostType.Azure => "Azure",
        HostType.VisualStudio => "Visual Studio",
        _ => null,
    };

    /// <summary>Matching a remote URL to an account: built-in host-name rule.</summary>
    public static HostType FromHost(string host)
    {
        host = host.ToLowerInvariant();
        if (host.Contains("github.com")) return HostType.GitHub;
        if (host.Contains("gitlab.com")) return HostType.GitLab;
        if (host.Contains("bitbucket.org")) return HostType.Bitbucket;
        if (host.Contains("dev.azure.com")) return HostType.Azure;
        if (host.Contains("visualstudio.com")) return HostType.VisualStudio;
        return HostType.Custom;
    }
}

/// <summary>Account record. Identity = (ServerUrl, Username). CaFile: GitLab Server only.
/// EnableNotifications is always written; NotificationsUpdatedAt = time of the last successful notification
/// fetch (null = never). Limitation: no e-mail field (Bitbucket only).</summary>
public sealed record Account(
    HostType Type,
    AuthType AuthType,
    string ServerUrl,
    string Username,
    string? AvatarUrl = null,
    string? CaFile = null,
    bool EnableNotifications = false,
    DateTimeOffset? NotificationsUpdatedAt = null)
{
    public const string GitHubServer = "https://github.com";
    public const string GitLabServer = "https://gitlab.com";

    /// <summary>Host name of the server URL without port (lower-case), used for remote matching.</summary>
    [JsonIgnore]
    public string Host => Uri.TryCreate(ServerUrl, UriKind.Absolute, out var u) ? u.Host : "";

    [JsonIgnore]
    public SecretKind SecretKind => AuthType == AuthType.OAuth ? SecretKind.OAuthToken : SecretKind.AccessToken;

    public bool SameIdentity(Account other) =>
        string.Equals(ServerUrl, other.ServerUrl, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Username, other.Username, StringComparison.OrdinalIgnoreCase);

    /// <summary>A typed server URL as stored: trimmed, no trailing `/`, lower-case.</summary>
    public static string NormalizeServerUrl(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

    /// <summary>GitLab Server with `https://gitlab.com` becomes type GitLab.</summary>
    public static HostType GitLabTypeFor(string normalizedServerUrl) =>
        normalizedServerUrl == GitLabServer ? HostType.GitLab : HostType.GitLabServer;
}

/// <summary>`accounts.json`: list order = file order, identity replace, written atomically
/// on every change. Holds no secrets (those live in <see cref="ISecretStore"/>).</summary>
public sealed class AccountStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly object _lock = new();
    Account[] _accounts;

    public AccountStore(string filePath)
    {
        FilePath = filePath;
        _accounts = Load(filePath);
    }

    public string FilePath { get; }

    /// <summary>Same folder as the app's settings: <see cref="GerGit.Core.ConfigFolder.Location"/>.</summary>
    public static string DefaultFilePath => Path.Combine(GerGit.Core.ConfigFolder.Location, "accounts.json");

    /// <summary>Raised after every change (already saved). May come from any thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<Account> Accounts
    {
        get { lock (_lock) return _accounts; }
    }

    /// <summary>Adds the account at the end; an account with the same identity is removed first.</summary>
    public void Add(Account account) =>
        Update(list => [.. list.Where(a => !a.SameIdentity(account)), account]);

    /// <summary>Removes the account with the same identity (nothing when absent).</summary>
    public void Remove(Account account) => Update(list => [.. list.Where(a => !a.SameIdentity(account))]);

    /// <summary>Replaces the account with the same identity by <paramref name="change"/>(current entry), in place
    /// (nothing when absent). <paramref name="raiseChanged"/> false: saved without <see cref="Changed"/> (bookkeeping
    /// such as the notification time, which must not refresh the repository tabs).</summary>
    public void Change(Account account, Func<Account, Account> change, bool raiseChanged = true) =>
        Update(list => [.. list.Select(a => a.SameIdentity(account) ? change(a) : a)], raiseChanged);

    void Update(Func<Account[], Account[]> change, bool raiseChanged = true)
    {
        lock (_lock)
        {
            _accounts = change(_accounts);
            Save();
        }
        if (raiseChanged) Changed?.Invoke();
    }

    static Account[] Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var list = new List<Account>();
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonArray
                       ?? throw new JsonException("the root is not an array"); // kept as .bak like broken JSON
            foreach (var node in root)
            {
                try
                {
                    if (node.Deserialize<Account>(Json) is { Type: HostType.GitHub or HostType.GitLab or HostType.GitLabServer,
                        ServerUrl.Length: > 0, Username.Length: > 0 } a) list.Add(a);
                }
                catch (JsonException e) { Trace.WriteLine($"Account entry skipped: {e.Message}"); }
            }
            return [.. list];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine($"Cannot read {path}: {e.Message}"); // a broken file starts empty; keep a copy for the user
            if (e is JsonException)
            {
                try { File.Copy(path, path + ".bak", overwrite: true); }
                catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
            }
            return [];
        }
    }

    // Temp file in the same folder, then rename (atomic on one file system).
    void Save()
    {
        try
        {
            GerGit.Core.ConfigFolder.CreateFolderOf(FilePath); // 0700 when it is the settings folder
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_accounts, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Cannot write {FilePath}: {e.Message}"); // the list stays in memory for this session
        }
    }
}

/// <summary>HTTPS git operations with account credentials: the account for a git credential `get`.</summary>
public static class Credentials
{
    /// <summary>Git's `host=` is `host[:port]`; host and port (default port of the protocol when absent) are compared with
    /// the account's server URL, and the scheme must equal the protocol. With a
    /// username only an account with that username (case-insensitive) matches; else the first account in list order.</summary>
    public static Account? FindAccount(IEnumerable<Account> accounts, string protocol, string host, string? username)
    {
        if (!Uri.TryCreate($"{protocol}://{host}/", UriKind.Absolute, out var asked)) return null;
        return accounts.FirstOrDefault(a =>
            Uri.TryCreate(a.ServerUrl, UriKind.Absolute, out var server) &&
            server.Scheme == asked.Scheme && server.Host == asked.Host && server.Port == asked.Port &&
            (string.IsNullOrEmpty(username) || string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Username (always the account's) and token for a credential `get`; null = not found (git goes on with
    /// its next helper / the prompt). A keyring failure counts as not found.</summary>
    public static (string Username, string Token)? Lookup(
        IEnumerable<Account> accounts, ISecretStore secrets, string protocol, string host, string? username)
    {
        if (FindAccount(accounts, protocol, host, username) is not { } account) return null;
        try
        {
            return secrets.Lookup(account.ServerUrl, account.Username, account.SecretKind) is { } token
                ? (account.Username, token) : null;
        }
        catch (SecretStoreException) { return null; } // logged by the store
    }
}
