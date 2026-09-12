namespace CraftSurvive.Procgen.Workbench;

/// <summary>Canonical one-field repairs for the bounded workbench motifs.
/// Repairs preserve every resolved decision other than their named semantic
/// boolean and never broaden the experiment's acceptance rules.</summary>
public static class WorkbenchRepair
{
    public const string RestoreSwitch = "restore-switch";
    public const string RestoreRecovery = "restore-recovery";
    public const string RestorePreview = "restore-preview";

    public static string[] Operations(WorkbenchCandidate candidate)
    {
        EnsureValid(candidate);
        var operations = new List<string>(3);
        if (!candidate.SwitchEnabled && UsesSwitch(candidate.Motif)) operations.Add(RestoreSwitch);
        if (!candidate.RecoveryEnabled && StringComparer.Ordinal.Equals(candidate.Motif, WorkbenchExperiment.RecoveryMotif)) operations.Add(RestoreRecovery);
        if (!candidate.PreviewOpening && StringComparer.Ordinal.Equals(candidate.Motif, WorkbenchExperiment.PreviewMotif)) operations.Add(RestorePreview);
        return operations.ToArray();
    }

    public static WorkbenchCandidate Apply(WorkbenchCandidate candidate, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        EnsureValid(candidate);
        if (!Operations(candidate).Contains(operation, StringComparer.Ordinal))
            throw new InvalidOperationException($"The workbench repair '{operation}' is not applicable to this candidate.");
        return operation switch
        {
            RestoreSwitch => candidate with { SwitchEnabled = true },
            RestoreRecovery => candidate with { RecoveryEnabled = true },
            RestorePreview => candidate with { PreviewOpening = true },
            _ => throw new InvalidOperationException($"The workbench repair '{operation}' is not supported."),
        };
    }

    public static string ChangedField(string operation) => operation switch
    {
        RestoreSwitch => "switchEnabled",
        RestoreRecovery => "recoveryEnabled",
        RestorePreview => "previewOpening",
        _ => throw new InvalidOperationException($"The workbench repair '{operation}' is not supported."),
    };

    private static bool UsesSwitch(string motif) =>
        StringComparer.Ordinal.Equals(motif, WorkbenchExperiment.CurrentMotif)
        || StringComparer.Ordinal.Equals(motif, WorkbenchExperiment.PreviewMotif);

    private static void EnsureValid(WorkbenchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var errors = WorkbenchExperiment.Validate(candidate);
        if (errors.Length > 0)
            throw new InvalidOperationException($"Workbench candidate is invalid: {string.Join(", ", errors)}.");
    }
}
