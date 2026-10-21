namespace DarkCipher.AgentControl.Domain.Configuration;

/// <summary>Who owns a configuration value. The app only changes values it owns, and never silently overwrites a value the
/// user changed after the app wrote it.</summary>
public enum ConfigOwnership { ApplicationManaged, UserManaged, ProviderManaged, TemplateManaged, ExtensionManaged, External }

public enum ConfigFormat { Json, TomlSubset }

/// <summary>A value the app intends to set, and who owns it.</summary>
public sealed record ConfigValue(string Key, string Value, ConfigOwnership Owner);

/// <summary>A user-changed value that the generated configuration would overwrite. Needs a decision.</summary>
public sealed record ConfigConflict(string Key, string CurrentValue, string ProposedValue, string LastWrittenByApp);

public enum ConflictDecision { KeepUserValue, UseGeneratedValue }

public enum TransactionState { Planned, Applied, Verified, Committed, RolledBack, Failed }

public sealed record TransactionStep(string Name, bool Ok, string Detail);

/// <summary>The record of one configuration transaction (persisted, shown in history, used for "Roll back last change").</summary>
public sealed record ConfigTransactionRecord
{
    public required string Id { get; init; }
    public required string TargetPath { get; init; }
    public required string Purpose { get; init; }
    public TransactionState State { get; init; }
    public string? BackupPath { get; init; }
    public bool TargetExisted { get; init; }
    public IReadOnlyList<TransactionStep> Steps { get; init; } = [];
    public IReadOnlyList<string> ChangedKeys { get; init; } = [];
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string Correlation { get; init; } = "";
}
