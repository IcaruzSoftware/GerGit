using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GerGit.Hosting;

/// <summary>Token storage kinds. Only access and OAuth tokens are stored.</summary>
public enum SecretKind { AccessToken, OAuthToken }

/// <summary>Token storage . Real: <see cref="LibSecretStore"/>; tests use an in-memory fake.</summary>
public interface ISecretStore
{
    /// <summary>Stores the secret; throws <see cref="SecretStoreException"/> (<see cref="SecretStoreException.SaveFailed"/>).</summary>
    void Store(string server, string user, SecretKind kind, string secret);

    /// <summary>The stored secret or null when there is none; throws <see cref="SecretStoreException"/>
    /// (<see cref="SecretStoreException.ReadFailed"/>) when the keyring cannot be used.</summary>
    string? Lookup(string server, string user, SecretKind kind);

    /// <summary>Deletes every kind for (server, user) (log out); failures are only logged.</summary>
    void Clear(string server, string user);
}

/// <summary>A keyring failure; the message is the friendly text the UI shows.</summary>
public sealed class SecretStoreException(string message, Exception? inner = null) : HostingException(message, inner)
{
    public const string SaveFailed = "Cannot save authentication";
    public const string ReadFailed = "Cannot read authorization from the system keyring";
}

public static class SecretStoreExtensions
{
    /// <summary>The account's token; a missing token or keyring failure throws <see cref="SecretStoreException"/>
    /// with <see cref="SecretStoreException.ReadFailed"/>.</summary>
    public static string TokenFor(this ISecretStore store, Account account) =>
        store.Lookup(account.ServerUrl, account.Username, account.SecretKind)
        ?? throw new SecretStoreException(SecretStoreException.ReadFailed);
}

/// <summary>Freedesktop Secret Service through libsecret (P/Invoke, the non-variadic `secret_password_*v_sync`
/// functions). Schema `gergit`, attributes application / server / user / kind.
/// No file fallback: without a Secret Service every call fails. Lookups are cached for the process lifetime.
/// The calls block (D-Bus, maybe an unlock prompt): call them off the UI thread.</summary>
public sealed class LibSecretStore : ISecretStore
{
    const string LibSecret = "libsecret-1.so.0", LibGLib = "libglib-2.0.so.0";

    readonly object _lock = new();
    readonly Dictionary<(string, string, SecretKind), string> _cache = [];

    public void Store(string server, string user, SecretKind kind, string secret)
    {
        lock (_lock)
        {
            try
            {
                WithAttributes(server, user, kind, attributes =>
                {
                    var stored = secret_password_storev_sync(Schema.Value, attributes, IntPtr.Zero,
                        $"GerGit token for {user}@{server}", secret, IntPtr.Zero, out var error);
                    ThrowIfError(error);
                    if (stored == 0) throw new InvalidOperationException("secret_password_storev_sync returned FALSE");
                    return 0;
                });
                _cache[(server, user, kind)] = secret;
            }
            catch (Exception e) when (e is not SecretStoreException)
            {
                Trace.WriteLine($"Keyring store failed for {user}@{server}: {e.Message}");
                throw new SecretStoreException(SecretStoreException.SaveFailed, e);
            }
        }
    }

    public string? Lookup(string server, string user, SecretKind kind)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue((server, user, kind), out var cached)) return cached;
            try
            {
                var value = WithAttributes(server, user, kind, attributes =>
                {
                    var ptr = secret_password_lookupv_sync(Schema.Value, attributes, IntPtr.Zero, out var error);
                    ThrowIfError(error);
                    if (ptr == IntPtr.Zero) return null;
                    var text = Marshal.PtrToStringUTF8(ptr);
                    secret_password_free(ptr); // wipes the memory
                    return text;
                });
                if (value is not null) _cache[(server, user, kind)] = value;
                return value;
            }
            catch (Exception e) when (e is not SecretStoreException)
            {
                Trace.WriteLine($"Keyring lookup failed for {user}@{server}: {e.Message}");
                throw new SecretStoreException(SecretStoreException.ReadFailed, e);
            }
        }
    }

    public void Clear(string server, string user)
    {
        lock (_lock)
        {
            foreach (var kind in Enum.GetValues<SecretKind>()) _cache.Remove((server, user, kind));
            try
            {
                // Without the kind attribute the clear call matches (and deletes) every kind.
                WithAttributes(server, user, null, attributes =>
                {
                    secret_password_clearv_sync(Schema.Value, attributes, IntPtr.Zero, out var error);
                    ThrowIfError(error);
                    return 0;
                });
            }
            catch (Exception e) { Trace.WriteLine($"Keyring clear failed for {user}@{server}: {e.Message}"); }
        }
    }

    static string KindName(SecretKind kind) => kind == SecretKind.OAuthToken ? "oauth_token" : "access_token";

    static T WithAttributes<T>(string server, string user, SecretKind? kind, Func<IntPtr, T> call)
    {
        var table = g_hash_table_new(StrHash.Value, StrEqual.Value);
        var strings = new List<IntPtr>();
        try
        {
            void Add(string key, string value)
            {
                var k = Marshal.StringToCoTaskMemUTF8(key);
                strings.Add(k);
                var v = Marshal.StringToCoTaskMemUTF8(value);
                strings.Add(v);
                g_hash_table_insert(table, k, v);
            }
            Add("application", "gergit");
            Add("server", server);
            Add("user", user);
            if (kind is { } k) Add("kind", KindName(k));
            return call(table);
        }
        finally
        {
            g_hash_table_unref(table);
            foreach (var p in strings) Marshal.FreeCoTaskMem(p);
        }
    }

    static void ThrowIfError(IntPtr error)
    {
        if (error == IntPtr.Zero) return;
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)); // GError { GQuark domain; gint code; gchar *message; }
        g_error_free(error);
        throw new InvalidOperationException(message);
    }

    static readonly Lazy<IntPtr> StrHash = new(() => NativeLibrary.GetExport(NativeLibrary.Load(LibGLib), "g_str_hash"));
    static readonly Lazy<IntPtr> StrEqual = new(() => NativeLibrary.GetExport(NativeLibrary.Load(LibGLib), "g_str_equal"));

    // SecretSchema { const gchar *name; SecretSchemaFlags flags; SecretSchemaAttribute attributes[32]; gint reserved;
    // gpointer reserved1..7; } with SecretSchemaAttribute { const gchar *name; SecretSchemaAttributeType type; }.
    // Allocated once and kept for the process lifetime; the zeroed rest ends the attribute list.
    static readonly Lazy<IntPtr> Schema = new(() =>
    {
        const int size = 8 + 8 + 32 * 16 + 8 + 7 * 8;
        var schema = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, schema, size);
        Marshal.WriteIntPtr(schema, 0, Marshal.StringToCoTaskMemUTF8("gergit"));
        Marshal.WriteInt32(schema, 8, 0); // SECRET_SCHEMA_NONE
        string[] attributes = ["application", "server", "user", "kind"];
        for (int i = 0; i < attributes.Length; i++)
        {
            Marshal.WriteIntPtr(schema, 16 + i * 16, Marshal.StringToCoTaskMemUTF8(attributes[i]));
            Marshal.WriteInt32(schema, 16 + i * 16 + 8, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING
        }
        return schema;
    });

    [DllImport(LibSecret)]
    static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string label, [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    static extern void secret_password_free(IntPtr password);

    [DllImport(LibGLib)]
    static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc);

    [DllImport(LibGLib)]
    static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [DllImport(LibGLib)]
    static extern void g_hash_table_unref(IntPtr table);

    [DllImport(LibGLib)]
    static extern void g_error_free(IntPtr error);
}
