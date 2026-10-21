using System.Text.Json;
using System.Text.RegularExpressions;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;

namespace DarkCipher.AgentControl.Integrations.HuggingFace;

/// <summary>
/// Hugging Face Hub via its official REST API (https://huggingface.co/api/...). Metadata only: nothing from a repository
/// is executed (no scripts, Python, remote code, native libraries or installers). Everything returned is untrusted and
/// validated. A token is attached only if the user stored one (credential reference), and only to huggingface.co.
/// </summary>
public sealed partial class HuggingFaceSource : IModelSource
{
    public static readonly Uri ApiBase = new("https://huggingface.co");
    private readonly INetworkClient _net;
    private readonly Func<string?> _tokenCredentialId;

    public HuggingFaceSource(INetworkClient net, Func<string?> tokenCredentialId)
    {
        _net = net;
        _tokenCredentialId = tokenCredentialId;
    }

    public string Id => "huggingface";
    public string DisplayName => "Hugging Face";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,95}/[A-Za-z0-9][A-Za-z0-9._\-]{0,95}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoIdPattern();

    public static bool IsValidRepoId(string? id) => id is not null && !id.Contains("..", StringComparison.Ordinal) && RepoIdPattern().IsMatch(id);

    public async Task<IReadOnlyList<ModelSourceEntry>> SearchAsync(ModelSearchQuery query, CancellationToken ct)
    {
        var text = (query.Text ?? "").Trim();
        if (text.Length > 100) text = text[..100];
        var qs = new List<string> { "sort=downloads", "direction=-1", $"limit={Math.Clamp(query.Limit, 1, 100)}" };
        if (text.Length > 0) qs.Add("search=" + Uri.EscapeDataString(text));
        if (query.GgufOnly) qs.Add("filter=gguf");
        var uri = new Uri(ApiBase, "/api/models?" + string.Join('&', qs));
        var r = await _net.SendAsync(new NetworkRequest(HttpMethod.Get, uri, NetworkPurpose.ModelCatalog)
        { BearerCredentialId = _tokenCredentialId(), MaxResponseBytes = 4 * 1024 * 1024, Timeout = TimeSpan.FromSeconds(20) }, ct).ConfigureAwait(false);
        if (!r.IsSuccess) throw new HttpRequestException($"Hugging Face returned HTTP {r.StatusCode}.");

        var list = JsonSerializer.Deserialize<List<ListItem>>(r.Body, Opts) ?? [];
        var entries = list.Where(i => IsValidRepoId(i.Id)).Select(ToEntry);
        if (query.CodingOnly) entries = entries.Where(e => LooksLikeCoding(e));
        return entries.ToList();
    }

    public async Task<ModelSourceDetails?> GetDetailsAsync(string id, CancellationToken ct)
    {
        if (!IsValidRepoId(id)) throw new ArgumentException("That is not a valid Hugging Face repository id (owner/name).");
        var uri = new Uri(ApiBase, $"/api/models/{id}?blobs=true");
        var r = await _net.SendAsync(new NetworkRequest(HttpMethod.Get, uri, NetworkPurpose.ModelCatalog)
        { BearerCredentialId = _tokenCredentialId(), MaxResponseBytes = 8 * 1024 * 1024, Timeout = TimeSpan.FromSeconds(20) }, ct).ConfigureAwait(false);
        if (r.StatusCode == 404) return null;
        if (r.StatusCode is 401 or 403) throw new UnauthorizedAccessException("This repository is gated or private. Accept its terms on huggingface.co and store a token to read it.");
        if (!r.IsSuccess) throw new HttpRequestException($"Hugging Face returned HTTP {r.StatusCode}.");

        var d = JsonSerializer.Deserialize<DetailsItem>(r.Body, Opts) ?? throw new JsonException("Empty model details.");
        var entry = ToEntry(new ListItem(d.Id ?? id, d.Author, d.Downloads, d.Likes, d.Tags, d.Pipeline_Tag, d.Private, d.Gated));
        var license = d.CardData?.License ?? entry.LicenseName;
        entry = entry with { LicenseName = license, License = LicenseClassifier.Classify(license) };
        var files = (d.Siblings ?? [])
            .Where(s => s.Rfilename is not null && s.Rfilename.Length < 300 && !s.Rfilename.Contains("..", StringComparison.Ordinal))
            .Select(s => new ModelFile(s.Rfilename!, s.Lfs?.Size ?? s.Size, GgufQuant.FromFileName(s.Rfilename!), s.Lfs?.Sha256))
            .ToList();
        var attribution = $"{entry.Id} by {entry.Author ?? entry.Id.Split('/')[0]} on Hugging Face — licence: {license ?? "not stated"}.";
        return new ModelSourceDetails(entry, files, null, attribution);
    }

    private static ModelSourceEntry ToEntry(ListItem i)
    {
        var tags = (i.Tags ?? []).Where(t => t.Length < 100).Take(60).ToList();
        var license = tags.FirstOrDefault(t => t.StartsWith("license:", StringComparison.Ordinal))?[8..];
        var gated = i.Gated is JsonElement g && (g.ValueKind == JsonValueKind.String || g.ValueKind == JsonValueKind.True);
        return new ModelSourceEntry(i.Id!, i.Id!.Split('/')[1], i.Author ?? i.Id.Split('/')[0], LicenseClassifier.Classify(license), license,
            gated, i.Private ?? false, i.Downloads, tags, $"https://huggingface.co/{i.Id}", i.Pipeline_Tag);
    }

    private static bool LooksLikeCoding(ModelSourceEntry e) =>
        e.Id.Contains("coder", StringComparison.OrdinalIgnoreCase) || e.Id.Contains("code", StringComparison.OrdinalIgnoreCase)
        || e.Id.Contains("devstral", StringComparison.OrdinalIgnoreCase) || e.Tags.Any(t => t.Contains("code", StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true, MaxDepth = 32 };

    private sealed record ListItem(string? Id, string? Author, long? Downloads, long? Likes, List<string>? Tags, string? Pipeline_Tag, bool? Private, JsonElement? Gated);
    private sealed record DetailsItem(string? Id, string? Author, long? Downloads, long? Likes, List<string>? Tags, string? Pipeline_Tag, bool? Private,
        JsonElement? Gated, CardDataItem? CardData, List<SiblingItem>? Siblings);
    private sealed record CardDataItem([property: System.Text.Json.Serialization.JsonPropertyName("license")] JsonElement? LicenseRaw)
    {
        // The card's licence may be a string or a list; anything else is treated as not stated.
        public string? License => LicenseRaw switch
        {
            { ValueKind: JsonValueKind.String } s => s.GetString(),
            { ValueKind: JsonValueKind.Array } a => a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).FirstOrDefault(),
            _ => null,
        };
    }
    private sealed record SiblingItem(string? Rfilename, long? Size, LfsItem? Lfs);
    private sealed record LfsItem(string? Sha256, long? Size);
}

/// <summary>GGUF quantisation names parsed from file names, ordered by approximate quality.</summary>
public static partial class GgufQuant
{
    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])(IQ[1-4]_(?:XXS|XS|S|M|NL)|Q[2-8]_K_(?:S|M|L|XL)|Q[2-8]_K|Q[4-8]_[01]|MXFP4|BF16|F16|F32)(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex QuantPattern();

    [GeneratedRegex(@"(?i)-0*\d+-of-0*\d+\.gguf$", RegexOptions.CultureInvariant)]
    private static partial Regex SplitPattern();

    public static string? FromFileName(string fileName)
    {
        if (!fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return null;
        var m = QuantPattern().Match(Path.GetFileName(fileName));
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    public static bool IsSplitFile(string fileName) => SplitPattern().IsMatch(fileName);

    /// <summary>Preference order when several fit: balanced quality/size first.</summary>
    public static readonly string[] Preference = ["Q4_K_M", "Q5_K_M", "Q4_K_S", "Q6_K", "Q5_K_S", "Q8_0", "IQ4_XS", "Q4_0", "Q3_K_M", "IQ3_M", "Q3_K_L", "MXFP4", "Q2_K"];
}
