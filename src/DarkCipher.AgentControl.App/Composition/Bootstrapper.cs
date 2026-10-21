using System.Reflection;
using DarkCipher.AgentControl.App.Appearance;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Appearance;
using DarkCipher.AgentControl.Application.Configuration;
using DarkCipher.AgentControl.Application.Models;
using DarkCipher.AgentControl.Application.Templates;
using DarkCipher.AgentControl.Application.Tools;
using DarkCipher.AgentControl.Application.Verification;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Tools;
using DarkCipher.AgentControl.Integrations.Tools;
using DarkCipher.AgentControl.App.ViewModels;
using DarkCipher.AgentControl.Application.Compatibility;
using DarkCipher.AgentControl.Application.Downloads;
using DarkCipher.AgentControl.Application.Hardware;
using DarkCipher.AgentControl.Application.Recommendations;
using DarkCipher.AgentControl.Application.Sessions;
using DarkCipher.AgentControl.Application.Settings;
using DarkCipher.AgentControl.Application.Stacks;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Hardware;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Stacks;
using DarkCipher.AgentControl.Extensions;
using DarkCipher.AgentControl.Infrastructure.Hardware;
using DarkCipher.AgentControl.Infrastructure.Observability;
using DarkCipher.AgentControl.Infrastructure.Persistence;
using DarkCipher.AgentControl.Integrations.Agents;
using DarkCipher.AgentControl.Integrations.HuggingFace;
using DarkCipher.AgentControl.Integrations.Ollama;
using DarkCipher.AgentControl.Security.Audit;
using DarkCipher.AgentControl.Security.Configuration;
using DarkCipher.AgentControl.Security.Credentials;
using DarkCipher.AgentControl.Security.Network;
using DarkCipher.AgentControl.Security.Packages;
using DarkCipher.AgentControl.Security.Paths;
using DarkCipher.AgentControl.Security.Permissions;
using DarkCipher.AgentControl.Security.Policy;
using DarkCipher.AgentControl.Security.Processes;
using DarkCipher.AgentControl.Security.Redaction;
using DarkCipher.AgentControl.Security.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace DarkCipher.AgentControl.App.Composition;

public sealed record BootOptions(bool SafeMode, string? DataDirectory, bool Headless, string CredentialPrefix = "DarkCipher.AgentControl");

/// <summary>The composition root: the only place that knows concrete types. Everything else depends on ports.</summary>
public static class Bootstrapper
{
    public static Version AppVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);

    public static ServiceProvider Build(BootOptions options)
    {
        var s = new ServiceCollection();
        s.AddSingleton(options);

        // ---- Persistence, observability, security core ----
        s.AddSingleton<IAppPaths>(_ => new AppPaths(options.DataDirectory));
        s.AddSingleton<Redactor>();
        s.AddSingleton<IRedactor>(sp => sp.GetRequiredService<Redactor>());
        s.AddSingleton<IAuditLogger>(sp => new SecurityAuditService(sp.GetRequiredService<IAppPaths>(), () => sp.GetRequiredService<IRedactor>()));
        s.AddSingleton(sp => new StructuredLogger(sp.GetRequiredService<IAppPaths>().LogDirectory, sp.GetRequiredService<IRedactor>()));
        s.AddSingleton<IAppLogger>(sp => sp.GetRequiredService<StructuredLogger>());
        s.AddSingleton<IDocumentStore>(sp => new JsonDocumentStore(sp.GetRequiredService<IAppPaths>().ConfigDirectory, sp.GetRequiredService<IAppLogger>()));
        s.AddSingleton(sp => new SecureCredentialService(sp.GetRequiredService<IRedactor>(), sp.GetRequiredService<IAuditLogger>(), options.CredentialPrefix));
        s.AddSingleton<ICredentialStore>(sp => sp.GetRequiredService<SecureCredentialService>());
        s.AddSingleton(sp => new SecurityPolicyService(sp.GetRequiredService<IDocumentStore>(), sp.GetRequiredService<IAuditLogger>(), options.SafeMode));
        s.AddSingleton<ISecurityPolicyService>(sp => sp.GetRequiredService<SecurityPolicyService>());
        s.AddSingleton<IProtectedPathService>(sp => new ProtectedPathService(sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<IDocumentStore>(), sp.GetRequiredService<IAuditLogger>()));
        s.AddSingleton<IPermissionService>(sp => new PermissionService(sp.GetRequiredService<IDocumentStore>(), sp.GetRequiredService<IAuditLogger>()));
        s.AddSingleton(sp => new SecureProcessExecutionService(sp.GetRequiredService<IProtectedPathService>(), sp.GetRequiredService<IRedactor>(),
            sp.GetRequiredService<IAuditLogger>(), sp.GetRequiredService<SecureCredentialService>(), sp.GetRequiredService<IAppLogger>()));
        s.AddSingleton<IProcessExecutor>(sp => sp.GetRequiredService<SecureProcessExecutionService>());
        s.AddSingleton(sp => new NetworkSecurityService(sp.GetRequiredService<ISecurityPolicyService>(), sp.GetRequiredService<IAuditLogger>(),
            sp.GetRequiredService<SecureCredentialService>(), sp.GetRequiredService<IAppLogger>()));
        s.AddSingleton<INetworkClient>(sp => sp.GetRequiredService<NetworkSecurityService>());
        s.AddSingleton<IConfigurationProtection>(sp => new ConfigurationProtectionService(sp.GetRequiredService<IProtectedPathService>(), sp.GetRequiredService<IAuditLogger>(), sp.GetRequiredService<IAppPaths>()));
        s.AddSingleton<IPackageVerifier>(sp => new PackageVerificationService(sp.GetRequiredService<IAuditLogger>()));
        s.AddSingleton(sp => new UpdateSecurityService(sp.GetRequiredService<IAuditLogger>(), trustedPublicKeyPem: null)); // no release key yet → updates off

        // ---- Hardware ----
        s.AddSingleton<IHardwareDiscoveryProvider>(sp => new WindowsHardwareDiscoveryProvider(sp.GetRequiredService<IAppLogger>()));
        s.AddSingleton<HardwareService>();

        // ---- Settings and appearance ----
        s.AddSingleton<AppSettingsService>();
        s.AddSingleton<AppearanceSettingsService>();
        s.AddSingleton<ThemeService>();
        s.AddSingleton<TypographyService>();
        s.AddSingleton<LayoutPreferenceService>();
        s.AddSingleton<AppearanceCoordinator>();

        // ---- Runtimes, sources, agents ----
        s.AddSingleton<OllamaDetector>();
        s.AddSingleton(sp => new OllamaRuntime(sp.GetRequiredService<INetworkClient>(), sp.GetRequiredService<OllamaDetector>(),
            new Uri(sp.GetRequiredService<AppSettingsService>().Current.OllamaEndpoint)));
        s.AddSingleton<IModelRuntime>(sp => sp.GetRequiredService<OllamaRuntime>());
        s.AddSingleton(sp => new OllamaLauncher(sp.GetRequiredService<IProcessExecutor>(), sp.GetRequiredService<OllamaDetector>(), sp.GetRequiredService<OllamaRuntime>()));
        s.AddSingleton<IDownloadProvider>(sp => sp.GetRequiredService<OllamaRuntime>());
        s.AddSingleton(sp => new OllamaCompatibilitySuite(sp.GetRequiredService<OllamaRuntime>()));
        s.AddSingleton<ICompatibilitySuite>(sp => sp.GetRequiredService<OllamaCompatibilitySuite>());
        s.AddSingleton(sp => new GatewayProbe(sp.GetRequiredService<OllamaRuntime>()));
        s.AddSingleton(sp => new HuggingFaceSource(sp.GetRequiredService<INetworkClient>(), () => sp.GetRequiredService<AppSettingsService>().Current.HuggingFaceCredentialId));
        s.AddSingleton<IModelSource>(sp => sp.GetRequiredService<HuggingFaceSource>());
        s.AddSingleton(sp => new ClaudeCodeProvider(sp.GetRequiredService<IProcessExecutor>()));
        s.AddSingleton(sp => new CodexProvider(sp.GetRequiredService<IProcessExecutor>()));
        s.AddSingleton<IAgentProvider>(sp => sp.GetRequiredService<ClaudeCodeProvider>());
        s.AddSingleton<IAgentProvider>(sp => sp.GetRequiredService<CodexProvider>());

        // ---- Agent tools: catalogue, installation (winget only), updates, repair, custom tools ----
        s.AddSingleton(sp => new WingetClient(sp.GetRequiredService<IProcessExecutor>()));
        s.AddSingleton<IAgentToolProvider>(sp => new ClaudeCodeToolProvider(sp.GetRequiredService<WingetClient>(), sp.GetRequiredService<ClaudeCodeProvider>()));
        s.AddSingleton<IAgentToolProvider>(sp => new CodexToolProvider(sp.GetRequiredService<WingetClient>(), sp.GetRequiredService<CodexProvider>()));
        s.AddSingleton(sp => new OllamaToolProvider(sp.GetRequiredService<WingetClient>(), sp.GetRequiredService<OllamaDetector>(), sp.GetRequiredService<IModelRuntime>()));
        s.AddSingleton<IAgentToolProvider>(sp => sp.GetRequiredService<OllamaToolProvider>());
        s.AddSingleton<IAgentToolProvider>(sp => new OllamaGatewayToolProvider(sp.GetRequiredService<IModelRuntime>(), sp.GetRequiredService<OllamaToolProvider>()));
        // The reviewed catalogue: more runtimes, gateways built into them, agent CLIs and supporting tools (winget, per-user).
        s.AddSingleton(sp => new BuiltInCatalogueSet(sp.GetRequiredService<WingetClient>(), sp.GetRequiredService<IProcessExecutor>(), sp.GetRequiredService<OllamaToolProvider>()));
        for (var i = 0; i < BuiltInCatalogueSet.Count; i++)
        {
            var index = i;
            s.AddSingleton<IAgentToolProvider>(sp => sp.GetRequiredService<BuiltInCatalogueSet>().All[index]);
        }
        s.AddSingleton<CustomAgentRegistry>();
        s.AddSingleton(sp => new AgentToolCatalogueService(sp.GetServices<IAgentToolProvider>(), sp.GetRequiredService<CustomAgentRegistry>(),
            def => new CustomAgentToolProvider(def, sp.GetRequiredService<IProcessExecutor>())));
        s.AddSingleton<AgentToolQueueProvider>();
        s.AddSingleton<IDownloadProvider>(sp => sp.GetRequiredService<AgentToolQueueProvider>());
        s.AddSingleton<ToolInstallVerifier>();
        s.AddSingleton<IInstalledItemVerifier>(sp => sp.GetRequiredService<ToolInstallVerifier>());
        s.AddSingleton<AgentToolInstallationService>();
        s.AddSingleton<AgentToolUpdateService>();
        s.AddSingleton<AgentToolRepairService>();

        // ---- Application services ----
        s.AddSingleton<RecommendationEngine>();
        s.AddSingleton<CompatibilityStore>();
        s.AddSingleton<StackService>();
        s.AddSingleton<ProjectService>();
        s.AddSingleton<ConfigurationOwnershipLedger>();
        s.AddSingleton<ConfigurationTransactionService>();
        s.AddSingleton<SessionService>();
        s.AddSingleton(sp => new ModelVerificationService(sp.GetRequiredService<IModelRuntime>(), sp.GetRequiredService<CompatibilityStore>(),
            sp.GetRequiredService<ICompatibilitySuite>(), sp.GetRequiredService<HardwareService>()));
        s.AddSingleton<IInstalledItemVerifier>(sp => sp.GetRequiredService<ModelVerificationService>());
        s.AddSingleton(sp => new DownloadManager(sp.GetServices<IDownloadProvider>(), sp.GetRequiredService<IAppLogger>(), sp.GetRequiredService<IAuditLogger>(), ModelDriveFreeBytes,
            sp.GetServices<IInstalledItemVerifier>(), sp.GetRequiredService<IDocumentStore>()));

        // ---- Templates, deterministic configuration, readiness and verification ----
        s.AddSingleton<TemplateValidationService>();
        s.AddSingleton<TemplateCatalogueService>();
        s.AddSingleton<ConfigurationBuilderService>();
        s.AddSingleton<IStackValidator>(sp => new StackValidator(sp.GetServices<IAgentProvider>(), sp.GetServices<IModelRuntime>(), sp.GetRequiredService<ISecurityPolicyService>(),
            sp.GetRequiredService<CompatibilityStore>(), sp.GetRequiredService<IPermissionService>(), sp.GetRequiredService<INetworkClient>(),
            (id, agent) => sp.GetRequiredService<TemplateCatalogueService>().Find(id)?.Agents.FirstOrDefault(a => a.AgentId == agent)));
        s.AddSingleton(sp => new TemplateEngine(sp.GetRequiredService<TemplateCatalogueService>(), sp.GetRequiredService<ConfigurationBuilderService>(), sp.GetServices<IAgentProvider>(),
            sp.GetRequiredService<IModelRuntime>(), sp.GetRequiredService<CompatibilityStore>(), sp.GetRequiredService<ISecurityPolicyService>(), sp.GetRequiredService<IPermissionService>(),
            sp.GetRequiredService<IProtectedPathService>(), sp.GetRequiredService<ProjectService>(), sp.GetRequiredService<StackService>(), sp.GetRequiredService<SessionService>(),
            sp.GetRequiredService<HardwareService>(), sp.GetRequiredService<CustomAgentRegistry>(), ModelDriveFreeBytes));
        s.AddSingleton<SupportedConfigurationMatrix>();
        s.AddSingleton(sp => new StackVerificationService(sp.GetRequiredService<StackService>(), sp.GetRequiredService<IStackValidator>(), sp.GetServices<IAgentProvider>(),
            sp.GetRequiredService<IModelRuntime>(), sp.GetRequiredService<CompatibilityStore>(), sp.GetRequiredService<HardwareService>(), sp.GetRequiredService<ConfigurationBuilderService>(),
            sp.GetRequiredService<TemplateCatalogueService>(), sp.GetRequiredService<ConfigurationTransactionService>(), sp.GetRequiredService<IProtectedPathService>(),
            sp.GetRequiredService<IProcessExecutor>(), sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<ISecurityPolicyService>(), sp.GetRequiredService<SupportedConfigurationMatrix>(),
            sp.GetRequiredService<IAuditLogger>(), AppVersion.ToString(3), sp.GetRequiredService<ICompatibilitySuite>(),
            async (model, ct) =>
            {
                var info = await sp.GetRequiredService<GatewayProbe>().ProbeAnthropicAsync(model, ct).ConfigureAwait(false);
                return (info.Compatibility, $"{info.Health}; {info.Compatibility}");
            }));

        // ---- Extensions ----
        s.AddSingleton<IExtensionHostServices, HostServicesAdapter>();
        s.AddSingleton<ExtensionHost>();
        s.AddSingleton(sp => new ExtensionGallery(sp.GetRequiredService<ExtensionHost>(), sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<IPackageVerifier>(), AppVersion));
        s.AddSingleton(sp => sp.GetRequiredService<ExtensionHost>().Contributions);

        // ---- Presentation ----
        if (options.Headless) s.AddSingleton<IApprovalService, DenyAllApprovals>();
        else s.AddSingleton<IApprovalService, WpfApprovalService>();
        s.AddSingleton<Navigator>();
        s.AddSingleton<ToolActions>();
        s.AddSingleton<DashboardViewModel>();
        s.AddSingleton<SetupViewModel>();
        s.AddSingleton<CatalogueViewModel>();
        s.AddSingleton<TemplatesViewModel>();
        s.AddSingleton<AgentsViewModel>();
        s.AddSingleton<ModelsViewModel>();
        s.AddSingleton<StacksViewModel>();
        s.AddSingleton<ProjectsViewModel>();
        s.AddSingleton<SessionsViewModel>();
        s.AddSingleton<ServicesViewModel>();
        s.AddSingleton<HardwareViewModel>();
        s.AddSingleton<DownloadsViewModel>();
        s.AddSingleton<ExtensionsViewModel>();
        s.AddSingleton<DeveloperViewModel>();
        s.AddSingleton<SecurityViewModel>();
        s.AddSingleton<LogsViewModel>();
        s.AddSingleton<AppearanceSettingsViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<MainViewModel>();

        return s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>Free space on the drive that holds Ollama's model store (OLLAMA_MODELS or %USERPROFILE%\.ollama).</summary>
    public static long? ModelDriveFreeBytes()
    {
        try
        {
            var dir = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
            if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama");
            var root = Path.GetPathRoot(dir);
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }
}
