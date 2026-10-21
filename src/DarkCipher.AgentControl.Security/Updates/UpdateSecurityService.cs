using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Security.Updates;

public sealed record UpdateManifest(string Product, string Version, string Url, string Sha256, DateTimeOffset PublishedAt, string Signature);

public sealed record UpdateVerification(bool Accepted, string Message, UpdateManifest? Manifest);

/// <summary>
/// Verifies update metadata before anything is downloaded: product name, HTTPS URL on an allow-listed host, strictly
/// newer version (downgrade protection), a well-formed SHA-256 for the package, and an ECDSA P-256 / SHA-256 signature
/// over the canonical manifest fields made with the release key. With no trusted public key configured, updates are
/// refused (fail safe). No release key exists yet, so the shipped build has updates switched off.
/// </summary>
public sealed class UpdateSecurityService
{
    public const string ProductName = "DarkCipher AgentControl";
    private static readonly string[] AllowedHosts = ["darkciphersystems.com", "www.darkciphersystems.com"];
    private readonly string? _trustedPublicKeyPem;
    private readonly IAuditLogger _audit;

    public UpdateSecurityService(IAuditLogger audit, string? trustedPublicKeyPem = null)
    {
        _audit = audit;
        _trustedPublicKeyPem = string.IsNullOrWhiteSpace(trustedPublicKeyPem) ? null : trustedPublicKeyPem;
    }

    public bool UpdatesEnabled => _trustedPublicKeyPem is not null;

    public static string CanonicalPayload(UpdateManifest m) =>
        string.Join('\n', m.Product, m.Version, m.Url, m.Sha256.ToLowerInvariant(), m.PublishedAt.ToUniversalTime().ToString("O"));

    public UpdateVerification Verify(string manifestJson, Version currentVersion)
    {
        UpdateVerification Result(bool ok, string msg, UpdateManifest? m)
        {
            _audit.Record(new AuditEvent("UpdateSignatureCheck", "UpdateSecurityService", ok ? AuditResult.Success : AuditResult.Blocked,
                m?.Version, ok ? SecurityClassification.Medium : SecurityClassification.High, Detail: msg));
            return new UpdateVerification(ok, msg, m);
        }

        if (_trustedPublicKeyPem is null) return Result(false, "Updates are disabled: no release signing key is configured.", null);
        if (manifestJson.Length > 64 * 1024) return Result(false, "The update manifest is too large.", null);

        UpdateManifest? m;
        try { m = JsonSerializer.Deserialize<UpdateManifest>(manifestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return Result(false, "The update manifest is not valid JSON.", null); }
        if (m is null) return Result(false, "The update manifest is empty.", null);

        if (m.Product != ProductName) return Result(false, "The manifest is for a different product.", m);
        if (!Version.TryParse(m.Version, out var offered)) return Result(false, "The manifest version is not valid.", m);
        if (offered <= currentVersion) return Result(false, $"Downgrade or same version refused ({m.Version} ≤ {currentVersion}).", m);
        if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
            return Result(false, "The package URL is not an HTTPS address on the official site.", m);
        if (m.Sha256.Length != 64 || !m.Sha256.All(Uri.IsHexDigit)) return Result(false, "The package hash is missing or malformed.", m);

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(_trustedPublicKeyPem);
            var ok = ecdsa.VerifyData(Encoding.UTF8.GetBytes(CanonicalPayload(m)), Convert.FromBase64String(m.Signature), HashAlgorithmName.SHA256);
            return ok ? Result(true, $"Update {m.Version} verified.", m) : Result(false, "The update signature is NOT valid.", m);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return Result(false, "The update signature could not be checked: " + ex.Message, m);
        }
    }
}
