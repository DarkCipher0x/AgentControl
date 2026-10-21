namespace DarkCipher.AgentControl.Domain.Models;

[Flags]
public enum ModelCapabilities
{
    None = 0,
    Completion = 1,
    Tools = 2,
    Vision = 4,
    Embedding = 8,
    Thinking = 16,
    Insert = 32,
    StructuredOutput = 64,
}

public enum LicenseClass { Unknown, OpenSource, OpenWeight, SourceAvailable, Restricted, ResearchOnly, CommercialRestricted }

public enum ModelRole { PrimaryCoding, FastUtility, Planning, CodeReview, Embedding, Vision, Fallback }

/// <summary>Where a model's metadata came from. Never trusted blindly.</summary>
public enum ModelOrigin { InstalledRuntime, CuratedCatalog, OllamaLibrary, HuggingFace, Extension, UserDefined }

public enum ModelReadiness { Unknown, Ready, Limited, Incompatible, CompatibilityUnverified, NotInstalled }

/// <summary>Runtime-neutral description of a model.</summary>
public sealed record ModelDescriptor
{
    public required string Name { get; init; }
    public string? Family { get; init; }
    public string? ParameterSize { get; init; }
    public string? Quantization { get; init; }
    public long? SizeBytes { get; init; }
    public int? ContextLength { get; init; }
    public ModelCapabilities Capabilities { get; init; }
    public LicenseClass License { get; init; }
    public string? LicenseName { get; init; }
    public ModelOrigin Origin { get; init; }
    public string? SourceUrl { get; init; }
    public string? Digest { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public ModelReadiness Readiness { get; init; } = ModelReadiness.Unknown;

    public bool Supports(ModelCapabilities c) => (Capabilities & c) == c;
}

/// <summary>A model currently loaded in a runtime.</summary>
public sealed record RunningModel(string Name, long SizeBytes, long SizeInVramBytes, int? ContextLength, DateTimeOffset? ExpiresAt)
{
    public double GpuFraction => SizeBytes <= 0 ? 0 : Math.Clamp((double)SizeInVramBytes / SizeBytes, 0, 1);
}

public static class LicenseClassifier
{
    /// <summary>Map an SPDX-style licence id to a class. Unknown ids stay Unknown and are never guessed upward.</summary>
    public static LicenseClass Classify(string? license)
    {
        if (string.IsNullOrWhiteSpace(license)) return LicenseClass.Unknown;
        var l = license.Trim().ToLowerInvariant();
        return l switch
        {
            "apache-2.0" or "mit" or "bsd-3-clause" or "bsd-2-clause" or "mpl-2.0" or "isc" => LicenseClass.OpenSource,
            "openrail" or "openrail++" or "bigscience-openrail-m" or "creativeml-openrail-m" => LicenseClass.OpenWeight,
            "cc-by-4.0" or "cc-by-sa-4.0" => LicenseClass.OpenWeight,
            "gemma" or "llama2" or "llama3" or "llama3.1" or "llama3.2" or "llama3.3" or "llama4" => LicenseClass.CommercialRestricted,
            "cc-by-nc-4.0" or "cc-by-nc-sa-4.0" or "cc-by-nc-nd-4.0" => LicenseClass.ResearchOnly,
            _ when l.Contains("research", StringComparison.Ordinal) => LicenseClass.ResearchOnly,
            _ => LicenseClass.Unknown,
        };
    }
}
