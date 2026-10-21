using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Application.Settings;

/// <summary>User-facing settings that actually change behaviour. No placeholder options.</summary>
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Ollama endpoint. Loopback by default; anything else must pass the network policy.</summary>
    public string OllamaEndpoint { get; init; } = "http://127.0.0.1:11434";
    public bool OnboardingCompleted { get; init; }
    public string? DefaultStackId { get; init; }
    /// <summary>Credential reference (not the token) for Hugging Face, if the user stored one.</summary>
    public string? HuggingFaceCredentialId { get; init; }
    public string PreferredAgentId { get; init; } = "codex";
}

public sealed class AppSettingsService
{
    private const string StoreName = "app-settings";
    private readonly IDocumentStore _store;
    private readonly IAuditLogger _audit;
    private AppSettings _current;

    public AppSettingsService(IDocumentStore store, IAuditLogger audit)
    {
        _store = store;
        _audit = audit;
        _current = store.Load(StoreName, () => new AppSettings());
        if (!Uri.TryCreate(_current.OllamaEndpoint, UriKind.Absolute, out _)) _current = _current with { OllamaEndpoint = new AppSettings().OllamaEndpoint };
    }

    public AppSettings Current => _current;

    public event EventHandler<AppSettings>? Changed;

    public OperationResult Update(Func<AppSettings, AppSettings> change)
    {
        var next = change(_current);
        if (!Uri.TryCreate(next.OllamaEndpoint, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https") || !string.IsNullOrEmpty(u.UserInfo))
            return OperationResult.Fail("The Ollama endpoint must be an http(s) URL without credentials.");
        var endpointChanged = next.OllamaEndpoint != _current.OllamaEndpoint;
        _current = next;
        _store.Save(StoreName, next);
        if (endpointChanged)
            _audit.Record(new AuditEvent("RuntimeEndpointChanged", "AppSettingsService", AuditResult.Success, next.OllamaEndpoint, u.IsLoopback ? SecurityClassification.Low : SecurityClassification.High));
        Changed?.Invoke(this, next);
        return OperationResult.Ok("Saved. Restart the app for endpoint changes to apply everywhere.");
    }
}
