using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Hardware;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Sdk;

namespace DarkCipher.AgentControl.Extensions;

/// <summary>
/// Extension lifecycle: discovery → validation (manifest, SDK compatibility, integrity) → install (review, permissions,
/// atomic commit) → enable/disable → load (isolated failures, timeout, crash-loop auto-disable) → remove.
/// <para>Isolation, stated honestly: declarative (data-only) extensions run no code — Enforced. Code extensions load
/// in-process in a collectible AssemblyLoadContext; the broker enforces permissions on every API call, but .NET cannot
/// sandbox in-process code, so a malicious assembly could call OS APIs directly — Advisory only. Code extensions
/// therefore require Development Mode and are labelled "Not isolated". Out-of-process isolation is Planned.</para>
/// Failures never block start-up: every load is wrapped and recorded on the extension's record.
/// </summary>
public sealed class ExtensionHost
{
    private const string RegistryName = "extensions";
    private const int CrashLoopThreshold = 3;
    private const long MaxPackageBytes = 50L * 1024 * 1024;
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(10);

    private readonly IAppPaths _paths;
    private readonly IDocumentStore _store;
    private readonly IPackageVerifier _verifier;
    private readonly ISecurityPolicyService _policy;
    private readonly IAuditLogger _audit;
    private readonly IAppLogger _log;
    private readonly IExtensionHostServices _services;
    private readonly IProcessExecutor _processes;
    private readonly IEnumerable<IModelRuntime> _runtimes;
    private readonly IHardwareDiscoveryProvider _hardware;
    private readonly ICredentialStore _credentials;
    private readonly object _gate = new();
    private readonly Dictionary<string, ExtensionRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssemblyLoadContext> _contexts = new(StringComparer.Ordinal);

    public ExtensionHost(IAppPaths paths, IDocumentStore store, IPackageVerifier verifier, ISecurityPolicyService policy, IAuditLogger audit, IAppLogger log,
        IExtensionHostServices services, IProcessExecutor processes, IEnumerable<IModelRuntime> runtimes, IHardwareDiscoveryProvider hardware, ICredentialStore credentials)
    {
        _paths = paths;
        _store = store;
        _verifier = verifier;
        _policy = policy;
        _audit = audit;
        _log = log;
        _services = services;
        _processes = processes;
        _runtimes = runtimes;
        _hardware = hardware;
        _credentials = credentials;
        foreach (var b in BuiltIns()) _records[b.Id] = b;
    }

    public ExtensionContributionRegistry Contributions { get; } = new();

    public event EventHandler? Changed;

    public IReadOnlyList<ExtensionRecord> Extensions { get { lock (_gate) return _records.Values.OrderBy(r => r.Trust).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(); } }

    private static IEnumerable<ExtensionRecord> BuiltIns()
    {
        ExtensionRecord B(string id, string name, string desc, params ExtensionType[] types) => new()
        {
            Id = id, Name = name, Publisher = "DarkCipher Systems", Version = "0.1.0", Description = desc, Trust = TrustLevel.BuiltIn,
            State = LifecycleState.Ready, Enabled = true, Types = types, SignatureStatus = "Part of the application", Isolation = "Built in (same trust as the app)",
        };
        yield return B("darkcipher.runtime.ollama", "Ollama runtime", "Ollama REST API: models, pulls, tests.", ExtensionType.Runtime, ExtensionType.Gateway);
        yield return B("darkcipher.source.huggingface", "Hugging Face model source", "Official Hub API: search, details, GGUF import planning.", ExtensionType.ModelSource);
        yield return B("darkcipher.agent.claude-code", "Claude Code agent", "Anthropic Claude Code: native and experimental local-gateway modes.", ExtensionType.AgentProvider);
        yield return B("darkcipher.agent.codex", "Codex CLI agent", "OpenAI Codex: local Ollama provider (isolated profile) and native.", ExtensionType.AgentProvider);
        yield return B("darkcipher.hardware.windows", "Windows hardware detection", "WMI + driver registry discovery.", ExtensionType.HardwareDetector);
    }

    // ---------------- Start-up ----------------

    /// <summary>Loads installed extensions according to policy. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        List<InstalledExtension> snapshot;
        lock (_gate) snapshot = [.. Registry().Items]; // never enumerate the live document (loads update it concurrently)
        foreach (var installed in snapshot)
        {
            try { await LoadOneAsync(installed, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                SetRecord(installed.Id, r => r with { State = LifecycleState.Failed, StateReason = "Unexpected host error: " + ex.Message });
                _log.Error("Extensions", $"Extension {installed.Id} failed to load.", ex);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadOneAsync(InstalledExtension inst, CancellationToken ct)
    {
        lock (_gate)
        {
            // One load per installed version: a second start (e.g. install + app start racing) is a no-op.
            if (_records.TryGetValue(inst.Id, out var existing) && existing.State is LifecycleState.Ready or LifecycleState.Loading
                && string.Equals(existing.InstallPath, inst.Path, StringComparison.OrdinalIgnoreCase))
                return;
            if (existing is not null) _records[inst.Id] = existing with { State = LifecycleState.Loading, InstallPath = inst.Path };
        }
        var policy = _policy.Current;
        var manifestPath = Path.Combine(inst.Path, SdkInfo.ManifestFileName);
        var (manifest, parseErrors) = File.Exists(manifestPath) ? ManifestValidator.Parse(File.ReadAllText(manifestPath)) : (null, ["Manifest missing."]);
        var baseRecord = new ExtensionRecord
        {
            Id = inst.Id, Name = manifest?.Name ?? inst.Id, Publisher = manifest?.Publisher ?? "?", Version = inst.Version, Description = manifest?.Description ?? "",
            Trust = inst.Trust, Enabled = inst.Enabled, IsDeclarative = manifest?.IsDeclarative ?? true, Types = manifest?.Types ?? [],
            RequestedPermissions = manifest?.Permissions ?? [], GrantedPermissions = inst.Granted, NetworkEndpoints = manifest?.NetworkEndpoints ?? [],
            License = manifest?.License, Source = manifest?.Source, InstallPath = inst.Path, SignatureStatus = inst.SignatureStatus, FailureCount = inst.FailureCount,
            Isolation = manifest?.IsDeclarative != false ? "Data only — runs no code (Enforced)" : "In-process code — NOT isolated (Advisory permissions only)",
        };

        if (manifest is null) { Put(baseRecord with { State = LifecycleState.Failed, StateReason = string.Join(" ", parseErrors) }); return; }
        var errors = ManifestValidator.Validate(manifest, _services.AppVersion, builtIn: IsBundled(inst.Id, inst.Trust));
        if (manifest.Id != inst.Id || manifest.Version != inst.Version) errors.Add("The installed manifest does not match the registry (id/version changed).");
        if (errors.Count > 0)
        {
            var incompatible = errors.Any(e => e.Contains("compatible", StringComparison.Ordinal) || e.Contains("Requires app version", StringComparison.Ordinal));
            Put(baseRecord with { State = incompatible ? LifecycleState.Incompatible : LifecycleState.Failed, Trust = incompatible ? TrustLevel.Incompatible : baseRecord.Trust, StateReason = string.Join(" ", errors) });
            return;
        }
        var integrity = PackageIntegrity.Verify(inst.Path, _verifier, allowDlls: !manifest.IsDeclarative);
        if (integrity.Count > 0)
        {
            Put(baseRecord with { State = LifecycleState.Blocked, Trust = TrustLevel.Blocked, StateReason = "Files changed after installation: " + string.Join(" ", integrity.Take(3)) });
            _audit.Record(new AuditEvent("ExtensionTamperDetected", "ExtensionHost", AuditResult.Blocked, inst.Id, SecurityClassification.High, Detail: string.Join(" ", integrity.Take(5))));
            return;
        }

        if (policy.SafeMode) { Put(baseRecord with { State = LifecycleState.Disabled, StateReason = "Safe Mode: third-party extensions are not loaded." }); return; }
        if (policy.DisableThirdPartyExtensions && !IsBundled(inst.Id, inst.Trust)) { Put(baseRecord with { State = LifecycleState.Disabled, StateReason = "Third-party extensions are disabled in Security settings." }); return; }
        if (!inst.Enabled) { Put(baseRecord with { State = LifecycleState.Disabled, StateReason = inst.FailureCount >= CrashLoopThreshold ? "Disabled automatically after repeated failures." : "Disabled by you." }); return; }
        if (!manifest.IsDeclarative && !policy.DevelopmentMode)
        { Put(baseRecord with { State = LifecycleState.Disabled, StateReason = "Code extensions run in-process without isolation and are only loaded in Development Mode." }); return; }

        var missing = manifest.Dependencies.Where(d => !Registry().Items.Any(i => i.Id == d.Id && i.Enabled && Version.Parse(i.Version) >= Version.Parse(d.MinVersion))).ToList();
        if (missing.Count > 0) { Put(baseRecord with { State = LifecycleState.Failed, StateReason = "Missing dependencies: " + string.Join(", ", missing.Select(d => $"{d.Id} ≥ {d.MinVersion}")) }); return; }

        Put(baseRecord with { State = LifecycleState.Loading, StateReason = "Loading" });
        var broker = new SdkCapabilityBroker(inst.Id, inst.Granted.ToHashSet(), _services, _policy, _audit, _log, _processes, _runtimes, _hardware, _credentials, _store, Contributions,
            (h, s) => SetRecord(inst.Id, r => r with { State = h == SdkHealth.Healthy ? LifecycleState.Ready : h == SdkHealth.Failed ? LifecycleState.Failed : LifecycleState.Degraded, StateReason = s }));

        try
        {
            if (manifest.IsDeclarative) LoadDeclarative(manifest, inst.Path, broker);
            else await LoadCodeAsync(manifest, inst, broker, ct).ConfigureAwait(false);
            Put(baseRecord with { State = LifecycleState.Ready, StateReason = "Loaded", FailureCount = 0 });
            if (inst.FailureCount != 0) UpdateRegistry(inst.Id, i => i with { FailureCount = 0 });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var failures = inst.FailureCount + 1;
            var disable = failures >= CrashLoopThreshold;
            UpdateRegistry(inst.Id, i => i with { FailureCount = failures, Enabled = !disable && i.Enabled });
            Put(baseRecord with
            {
                State = LifecycleState.Failed, FailureCount = failures, Enabled = !disable,
                StateReason = (disable ? $"Disabled after {failures} failed starts. " : "") + $"{ex.GetType().Name}: {ex.Message}",
            });
            _audit.Record(new AuditEvent("ExtensionLoadFailed", "ExtensionHost", AuditResult.Failure, inst.Id, disable ? SecurityClassification.Medium : SecurityClassification.Low, Detail: ex.Message));
        }
    }

    private static void LoadDeclarative(ExtensionManifest m, string root, SdkCapabilityBroker broker)
    {
        foreach (var rel in m.Contributes.StackTemplates)
        {
            var file = Path.Combine(root, rel.Replace('/', '\\'));
            if (new FileInfo(file).Length > 64 * 1024) throw new InvalidDataException($"{rel} is larger than 64 KB.");
            var templates = JsonSerializer.Deserialize<List<SdkStackTemplate>>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 8 })
                            ?? throw new InvalidDataException($"{rel} is empty.");
            if (templates.Count > 20) throw new InvalidDataException($"{rel} contains more than 20 templates.");
            foreach (var t in templates) broker.RegisterStackTemplate(t);
        }
        // Model catalogue contributions are validated here and surfaced read-only in Models (Experimental).
        foreach (var rel in m.Contributes.ModelCatalogs)
        {
            var file = Path.Combine(root, rel.Replace('/', '\\'));
            if (new FileInfo(file).Length > 256 * 1024) throw new InvalidDataException($"{rel} is larger than 256 KB.");
            using var _ = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { MaxDepth = 8 });
        }
    }

    private async Task LoadCodeAsync(ExtensionManifest m, InstalledExtension inst, SdkCapabilityBroker broker, CancellationToken ct)
    {
        var parts = m.EntryPoint!.Split('!');
        var asmPath = Path.Combine(inst.Path, parts[0]);
        var alc = new ExtensionLoadContext(inst.Id, asmPath);
        lock (_gate) _contexts[inst.Id] = alc;
        var asm = alc.LoadFromAssemblyPath(asmPath);
        var sdkRef = asm.GetReferencedAssemblies().FirstOrDefault(a => a.Name == typeof(IExtension).Assembly.GetName().Name);
        if (sdkRef?.Version is { } v && !SdkInfo.IsCompatible(v)) throw new InvalidOperationException($"Built against SDK {v}, host SDK is {SdkInfo.Version}.");
        var type = asm.GetType(parts[1], throwOnError: true)!;
        if (!typeof(IExtension).IsAssignableFrom(type)) throw new InvalidOperationException($"{parts[1]} does not implement IExtension.");
        var ext = (IExtension)Activator.CreateInstance(type)!;
        var ctx = new Context(inst.Id, _services.AppVersion, inst.Granted.ToHashSet(), broker);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(InitTimeout);
        var init = Task.Run(() => ext.InitializeAsync(ctx, cts.Token), cts.Token);
        var done = await Task.WhenAny(init, Task.Delay(InitTimeout, ct)).ConfigureAwait(false);
        if (done != init) throw new TimeoutException($"InitializeAsync did not finish within {InitTimeout.TotalSeconds:0} s.");
        await init.ConfigureAwait(false);
    }

    private sealed record Context(string ExtensionId, Version HostVersion, IReadOnlySet<ExtensionPermission> GrantedPermissions, ISdkCapabilityBroker Broker) : IExtensionContext
    {
        public Version HostSdkVersion => SdkInfo.Version;
    }

    /// <summary>Collectible context; the SDK assembly is always shared from the host so contracts match.</summary>
    private sealed class ExtensionLoadContext(string name, string mainAssembly) : AssemblyLoadContext("ext:" + name, isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == typeof(IExtension).Assembly.GetName().Name) return null; // use the host's SDK
            if (assemblyName.Name?.StartsWith("DarkCipher.AgentControl.", StringComparison.Ordinal) == true)
                throw new InvalidOperationException("Extensions may reference only the public SDK, not application internals.");
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }

    // ---------------- Install / review / remove ----------------

    /// <summary>Id prefix of the data-only extensions bundled with the app (Extensions → Gallery).</summary>
    public const string GalleryIdPrefix = "darkcipher.gallery.";

    /// <summary>A gallery extension: shipped inside the app, data only, installed through <see cref="ReviewBundled"/>.</summary>
    public static bool IsBundled(string id, TrustLevel trust) => trust == TrustLevel.Verified && id.StartsWith(GalleryIdPrefix, StringComparison.Ordinal);

    /// <summary>Stages and validates a package for the pre-install review. Nothing is installed yet.</summary>
    public InstallReview Review(string packagePath) => Review(packagePath, bundled: false);

    /// <summary>
    /// Review for a package the app packed from its own install folder (the Gallery). Same extraction, manifest,
    /// integrity and version checks as any package, plus: data only, and the id must use <see cref="GalleryIdPrefix"/>.
    /// Only then may it use the reserved DarkCipher Systems identity; it is installed as Verified (bundled).
    /// </summary>
    public InstallReview ReviewBundled(string packagePath) => Review(packagePath, bundled: true);

    private InstallReview Review(string packagePath, bool bundled)
    {
        var staging = Path.Combine(_paths.StagingDirectory, Guid.NewGuid().ToString("N"));
        var errors = new List<string>();
        var warnings = new List<string>();
        ExtensionManifest? manifest = null;

        InstallReview Done(TrustLevel trust, string sig) => new(manifest ?? new ExtensionManifest(), trust, sig, errors, warnings,
            (manifest?.Permissions ?? []).Select(p => { var (meaning, risk) = ManifestValidator.Describe(p); return (p, meaning, risk); }).ToList(),
            manifest?.IsDeclarative != false ? "Data only — runs no code (Enforced)" : "In-process code — NOT isolated; requires Development Mode",
            staging);

        if (!File.Exists(packagePath)) { errors.Add("Package not found."); return Done(TrustLevel.Unsigned, "n/a"); }
        if (!packagePath.EndsWith(SdkInfo.PackageExtension, StringComparison.OrdinalIgnoreCase)) errors.Add($"Extension packages use the {SdkInfo.PackageExtension} extension.");
        if (new FileInfo(packagePath).Length > MaxPackageBytes) errors.Add("Package is larger than 50 MB.");
        if (errors.Count > 0) return Done(TrustLevel.Unsigned, "n/a");

        var extraction = _verifier.ExtractZipSafely(packagePath, staging, new ExtractionLimits(MaxEntries: 500, MaxTotalBytes: 100L * 1024 * 1024));
        if (!extraction.Success) { errors.Add(extraction.Message); return Done(TrustLevel.Blocked, "n/a"); }

        var manifestPath = Path.Combine(staging, SdkInfo.ManifestFileName);
        if (!File.Exists(manifestPath)) { errors.Add($"The package has no {SdkInfo.ManifestFileName}."); return Done(TrustLevel.Unsigned, "n/a"); }
        var (m, parseErrors) = ManifestValidator.Parse(File.ReadAllText(manifestPath));
        manifest = m;
        errors.AddRange(parseErrors);
        if (manifest is null) return Done(TrustLevel.Unsigned, "n/a");

        errors.AddRange(ManifestValidator.Validate(manifest, _services.AppVersion, builtIn: bundled));
        if (bundled && !manifest.IsDeclarative) errors.Add("Bundled gallery extensions must be data only.");
        if (bundled && !manifest.Id.StartsWith(GalleryIdPrefix, StringComparison.Ordinal)) errors.Add($"Bundled gallery extensions must use the {GalleryIdPrefix} id prefix.");
        errors.AddRange(PackageIntegrity.Verify(staging, _verifier, allowDlls: !manifest.IsDeclarative));

        var existing = Registry().Items.FirstOrDefault(i => i.Id == manifest.Id);
        if (existing is not null && Version.TryParse(manifest.Version, out var nv) && Version.TryParse(existing.Version, out var ov))
        {
            if (nv < ov) errors.Add($"Downgrade refused: {existing.Version} is installed.");
            else if (nv == ov) errors.Add($"Version {existing.Version} is already installed.");
            else
            {
                var gained = manifest.Permissions.Except(existing.Granted).ToList();
                if (gained.Count > 0) warnings.Add("This update requests NEW permissions: " + string.Join(", ", gained) + ". They are not granted unless you approve them here.");
            }
        }
        if (_records.TryGetValue(manifest.Id, out var rec) && rec.Trust == TrustLevel.BuiltIn) errors.Add("That id belongs to a built-in extension.");

        var sig = bundled ? "Bundled with DarkCipher AgentControl (packed from its install folder; not separately signed)"
            : manifest.Signature is null ? "Unsigned" : "Signature present, but no trusted publisher keys are configured — treated as unsigned";
        var trust = bundled ? TrustLevel.Verified : manifest.Signature is null ? TrustLevel.Unsigned : TrustLevel.Community;
        if (!manifest.IsDeclarative) warnings.Add("This extension contains code. It will run inside the app without isolation and only loads in Development Mode.");
        if (manifest.MaxTestedVersion.Length > 0 && Version.Parse(manifest.MaxTestedVersion) < _services.AppVersion) warnings.Add($"Tested up to app version {manifest.MaxTestedVersion} only.");
        if (_policy.Current.DisableThirdPartyExtensions && !bundled) warnings.Add("Third-party extensions are currently disabled in Security; it will install but not load until you enable them.");
        return Done(trust, sig);
    }

    /// <summary>Commits a reviewed package with the permissions the user approved (a subset of those requested).</summary>
    public OperationResult Install(InstallReview review, IEnumerable<ExtensionPermission> approvedPermissions)
    {
        if (!review.CanInstall) return OperationResult.Fail("The package did not pass review.");
        var m = review.Manifest;
        var granted = approvedPermissions.Intersect(m.Permissions).Distinct().ToList();
        var target = Path.Combine(_paths.ExtensionsDirectory, m.Id, m.Version);
        try
        {
            if (Directory.Exists(target)) return OperationResult.Fail("That version is already present on disk.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(review.StagingPath, target); // same volume: atomic rename
        }
        catch (IOException ex) { return OperationResult.Fail("Install failed: " + ex.Message); }

        var previous = Registry().Items.FirstOrDefault(i => i.Id == m.Id);
        UpdateRegistry(m.Id, _ => new InstalledExtension(m.Id, m.Version, target, Enabled: true, granted, review.Trust, review.SignatureStatus, 0, DateTimeOffset.UtcNow), create: true);
        _audit.Record(new AuditEvent(previous is null ? "ExtensionInstalled" : "ExtensionUpdated", "ExtensionHost", AuditResult.Success, $"{m.Id} {m.Version}", SecurityClassification.High,
            Detail: $"trust {review.Trust}; granted {string.Join(",", granted)}; {review.Isolation}"));
        if (previous is not null && previous.Path != target) TryDelete(previous.Path);
        _ = StartOneAsync(m.Id);
        return OperationResult.Ok(previous is null ? "Installed." : "Updated.");
    }

    public void DiscardReview(InstallReview review) => TryDelete(review.StagingPath);

    public OperationResult SetEnabled(string id, bool enabled)
    {
        if (_records.TryGetValue(id, out var r) && r.Trust == TrustLevel.BuiltIn) return OperationResult.Fail("Built-in components cannot be disabled here.");
        if (!UpdateRegistry(id, i => i with { Enabled = enabled, FailureCount = enabled ? 0 : i.FailureCount })) return OperationResult.Fail("Not installed.");
        _audit.Record(new AuditEvent(enabled ? "ExtensionEnabled" : "ExtensionDisabled", "ExtensionHost", AuditResult.Success, id, SecurityClassification.Medium));
        if (!enabled) { Contributions.RemoveExtension(id); SetRecord(id, x => x with { Enabled = false, State = LifecycleState.Disabled, StateReason = "Disabled by you." }); }
        else _ = StartOneAsync(id);
        return OperationResult.Ok(enabled ? "Enabled." : "Disabled.");
    }

    public OperationResult Remove(string id)
    {
        var inst = Registry().Items.FirstOrDefault(i => i.Id == id);
        if (inst is null) return OperationResult.Fail("Not installed.");
        Contributions.RemoveExtension(id);
        lock (_gate)
        {
            if (_contexts.Remove(id, out var alc)) alc.Unload();
            _records[id] = _records.TryGetValue(id, out var r) ? r with { State = LifecycleState.Removed, Enabled = false, StateReason = "Removed." } : _records.GetValueOrDefault(id)!;
        }
        var doc = Registry();
        doc.Items.RemoveAll(i => i.Id == id);
        _store.Save(RegistryName, doc);
        TryDelete(Path.Combine(_paths.ExtensionsDirectory, id));
        _audit.Record(new AuditEvent("ExtensionRemoved", "ExtensionHost", AuditResult.Success, id, SecurityClassification.Medium));
        Changed?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("Removed.");
    }

    private async Task StartOneAsync(string id)
    {
        var inst = Registry().Items.FirstOrDefault(i => i.Id == id);
        if (inst is null) return;
        try { await LoadOneAsync(inst, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _log.Error("Extensions", $"Extension {id} failed.", ex); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- registry ----------------

    public sealed record InstalledExtension(string Id, string Version, string Path, bool Enabled, List<ExtensionPermission> Granted, TrustLevel Trust, string SignatureStatus, int FailureCount, DateTimeOffset InstalledAt);

    public sealed class RegistryDoc
    {
        public int SchemaVersion { get; set; } = 1;
        public List<InstalledExtension> Items { get; set; } = [];
    }

    private RegistryDoc Registry() => _store.Load(RegistryName, () => new RegistryDoc());

    private bool UpdateRegistry(string id, Func<InstalledExtension, InstalledExtension> f, bool create = false)
    {
        lock (_gate)
        {
            var doc = Registry();
            var i = doc.Items.FindIndex(x => x.Id == id);
            if (i < 0 && !create) return false;
            if (i < 0) doc.Items.Add(f(null!));
            else doc.Items[i] = f(doc.Items[i]);
            _store.Save(RegistryName, doc);
            return true;
        }
    }

    private void Put(ExtensionRecord r) { lock (_gate) _records[r.Id] = r; Changed?.Invoke(this, EventArgs.Empty); }

    private void SetRecord(string id, Func<ExtensionRecord, ExtensionRecord> f)
    {
        lock (_gate) { if (_records.TryGetValue(id, out var r)) _records[id] = f(r); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TryDelete(string dir)
    {
        try
        {
            // Only ever inside the app's own extension/staging directories.
            var full = Path.GetFullPath(dir);
            var okRoots = new[] { Path.GetFullPath(_paths.ExtensionsDirectory), Path.GetFullPath(_paths.StagingDirectory) };
            if (!okRoots.Any(root => full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return;
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Warn("Extensions", $"Could not delete {dir}: {ex.Message}"); }
    }
}
