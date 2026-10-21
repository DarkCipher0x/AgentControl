using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Security.Native;

namespace DarkCipher.AgentControl.Security.Credentials;

/// <summary>
/// Secrets live only in Windows Credential Manager (per-user, DPAPI-protected by the OS, local-machine persistence so
/// they never roam). The app stores and displays opaque <see cref="CredentialReference"/>s. Secret values can be read
/// back only by code inside the Security assembly (<see cref="Reveal"/> is internal): the process executor injects them
/// into a child environment and the network service into an outgoing header. Nothing else ever sees them.
/// </summary>
public sealed class SecureCredentialService : ICredentialStore
{
    private readonly string _prefix;
    private readonly IRedactor _redactor;
    private readonly IAuditLogger _audit;

    /// <param name="targetPrefix">Credential Manager target prefix. Tests use a distinct prefix and clean up.</param>
    public SecureCredentialService(IRedactor redactor, IAuditLogger audit, string targetPrefix = "DarkCipher.AgentControl")
    {
        if (string.IsNullOrWhiteSpace(targetPrefix) || targetPrefix.Contains('*')) throw new ArgumentException("Invalid prefix.");
        _prefix = targetPrefix + ":";
        _redactor = redactor;
        _audit = audit;
        // Register existing secrets with the redactor so they can never be logged verbatim.
        foreach (var r in List())
        {
            var v = Reveal(r.Id);
            if (v is not null) _redactor.RegisterSecret(v);
        }
    }

    private sealed record Meta(string DisplayName, string Kind, DateTimeOffset CreatedAt);

    public IReadOnlyList<CredentialReference> List()
    {
        var result = new List<CredentialReference>();
        if (!NativeMethods.CredEnumerateW(_prefix + "*", 0, out var count, out var ptr))
            return result; // ERROR_NOT_FOUND when empty
        try
        {
            for (var i = 0; i < count; i++)
            {
                var credPtr = Marshal.ReadIntPtr(ptr, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credPtr);
                var id = cred.TargetName[_prefix.Length..];
                var meta = ParseMeta(cred.Comment);
                result.Add(new CredentialReference(id, meta?.DisplayName ?? id, meta?.Kind ?? "secret", meta?.CreatedAt ?? DateTimeOffset.MinValue));
            }
        }
        finally { NativeMethods.CredFree(ptr); }
        return result.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public CredentialReference Save(string displayName, string kind, string secret)
    {
        // Credential Manager comments are limited to 256 characters; keep metadata well inside that.
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 80) throw new ArgumentException("Give the credential a short name (up to 80 characters).");
        if (kind is { Length: > 40 }) throw new ArgumentException("The credential kind is too long.");
        if (string.IsNullOrEmpty(secret)) throw new ArgumentException("The secret is empty.");
        if (secret.Length > 2048) throw new ArgumentException("The secret is too long.");
        if (secret.IndexOf('\0') >= 0) throw new ArgumentException("The secret contains a NUL character.");

        var id = Guid.NewGuid().ToString("N")[..16];
        var meta = new Meta(displayName.Trim(), string.IsNullOrWhiteSpace(kind) ? "secret" : kind.Trim(), DateTimeOffset.UtcNow);
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new NativeMethods.CREDENTIAL
            {
                Type = NativeMethods.CRED_TYPE_GENERIC,
                TargetName = _prefix + id,
                Comment = JsonSerializer.Serialize(meta),
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
                UserName = "DarkCipher AgentControl",
            };
            if (!NativeMethods.CredWriteW(ref cred, 0))
                throw new InvalidOperationException("Windows Credential Manager refused the credential: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        finally
        {
            // Zero the unmanaged copy and the managed buffer.
            for (var i = 0; i < blob.Length; i++) Marshal.WriteByte(blobPtr, i, 0);
            Marshal.FreeHGlobal(blobPtr);
            Array.Clear(blob);
        }

        _redactor.RegisterSecret(secret);
        _audit.Record(new AuditEvent("CredentialCreated", "SecureCredentialService", AuditResult.Success, $"{meta.DisplayName} ({meta.Kind})", SecurityClassification.Medium));
        return new CredentialReference(id, meta.DisplayName, meta.Kind, meta.CreatedAt);
    }

    public bool Remove(string id)
    {
        if (!IsValidId(id)) return false;
        var ok = NativeMethods.CredDeleteW(_prefix + id, NativeMethods.CRED_TYPE_GENERIC, 0);
        _audit.Record(new AuditEvent("CredentialRemoved", "SecureCredentialService", ok ? AuditResult.Success : AuditResult.Failure, id, SecurityClassification.Medium));
        return ok;
    }

    public bool Exists(string id)
    {
        if (!IsValidId(id)) return false;
        if (!NativeMethods.CredReadW(_prefix + id, NativeMethods.CRED_TYPE_GENERIC, 0, out var ptr)) return false;
        NativeMethods.CredFree(ptr);
        return true;
    }

    /// <summary>Returns the secret. Internal by design: only the Security assembly may resolve values.</summary>
    internal string? Reveal(string id)
    {
        if (!IsValidId(id)) return null;
        if (!NativeMethods.CredReadW(_prefix + id, NativeMethods.CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return "";
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally { NativeMethods.CredFree(ptr); }
    }

    private static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(char.IsAsciiLetterOrDigit);

    private static Meta? ParseMeta(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return null;
        try { return JsonSerializer.Deserialize<Meta>(comment); } catch (JsonException) { return null; }
    }
}
