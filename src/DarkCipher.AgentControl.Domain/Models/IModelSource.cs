namespace DarkCipher.AgentControl.Domain.Models;

public sealed record ModelSearchQuery(string? Text, bool GgufOnly = false, bool CodingOnly = false, bool ToolsOnly = false, int Limit = 30);

public sealed record ModelFile(string Path, long? SizeBytes, string? Quantization, string? Sha256);

public sealed record ModelSourceEntry(
    string Id,
    string DisplayName,
    string? Author,
    LicenseClass License,
    string? LicenseName,
    bool Gated,
    bool Private,
    long? Downloads,
    IReadOnlyList<string> Tags,
    string SourceUrl,
    string? PipelineTag);

public sealed record ModelSourceDetails(ModelSourceEntry Entry, IReadOnlyList<ModelFile> Files, string? CardSummary, string? Attribution);

/// <summary>Port: a searchable catalogue of models (Hugging Face, extension catalogues, ...).</summary>
public interface IModelSource
{
    string Id { get; }
    string DisplayName { get; }
    Task<IReadOnlyList<ModelSourceEntry>> SearchAsync(ModelSearchQuery query, CancellationToken ct);
    Task<ModelSourceDetails?> GetDetailsAsync(string id, CancellationToken ct);
}
