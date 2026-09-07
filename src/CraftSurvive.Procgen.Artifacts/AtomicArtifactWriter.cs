namespace CraftSurvive.Procgen.Artifacts;

/// <summary>Stages a result/receipt pair beside their destinations, then commits
/// both replacements with rollback on any failure. Destination symlinks and
/// aliased paths are rejected before staging.</summary>
public static class AtomicArtifactWriter
{
    public static AtomicWriteOutcome WritePair(string resultPath, ReadOnlySpan<byte> result, string receiptPath, ReadOnlySpan<byte> receipt) =>
        WritePairCore(resultPath, result, receiptPath, receipt, null);

    internal static AtomicWriteOutcome WritePairWithProbe(string resultPath, ReadOnlySpan<byte> result, string receiptPath, ReadOnlySpan<byte> receipt, Action<AtomicWriteStage> beforeStage) =>
        WritePairCore(resultPath, result, receiptPath, receipt, beforeStage);

    private static AtomicWriteOutcome WritePairCore(string resultPath, ReadOnlySpan<byte> result, string receiptPath, ReadOnlySpan<byte> receipt, Action<AtomicWriteStage>? beforeStage)
    {
        var first = Target.Prepare(resultPath);
        var second = Target.Prepare(receiptPath);
        if (StringComparer.Ordinal.Equals(first.Destination, second.Destination))
            throw new ArtifactIoException("output_paths_alias", "Result and receipt destinations must be distinct files.");
        try
        {
            beforeStage?.Invoke(AtomicWriteStage.StageFirst);
            first.Stage(result);
            beforeStage?.Invoke(AtomicWriteStage.StageSecond);
            second.Stage(receipt);
            beforeStage?.Invoke(AtomicWriteStage.BackupFirst);
            first.Backup();
            beforeStage?.Invoke(AtomicWriteStage.BackupSecond);
            second.Backup();
            beforeStage?.Invoke(AtomicWriteStage.CommitFirst);
            first.Commit();
            beforeStage?.Invoke(AtomicWriteStage.CommitSecond);
            second.Commit();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var rollback = Rollback(second, first);
            throw new ArtifactIoException("atomic_write_failed", rollback is null ? exception.Message : $"{exception.Message}; rollback failed: {rollback}", exception);
        }
        catch
        {
            _ = Rollback(second, first);
            throw;
        }

        // Publication is now durable. Cleanup failures must never enter the
        // rollback path: a removed backup cannot restore an earlier target.
        var cleanupFailures = Cleanup(first, second, beforeStage);
        return new AtomicWriteOutcome(cleanupFailures);
    }

    private static string? Rollback(params Target[] targets)
    {
        var failures = new List<string>();
        foreach (var target in targets)
        {
            try { target.Rollback(); }
            catch (Exception exception) { failures.Add(exception.Message); }
        }
        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    private static IReadOnlyList<string> Cleanup(Target first, Target second, Action<AtomicWriteStage>? beforeStage)
    {
        var failures = new List<string>();
        Cleanup(first, AtomicWriteStage.CleanupFirst, beforeStage, failures);
        Cleanup(second, AtomicWriteStage.CleanupSecond, beforeStage, failures);
        return failures;
    }

    private static void Cleanup(Target target, AtomicWriteStage stage, Action<AtomicWriteStage>? beforeStage, List<string> failures)
    {
        try
        {
            beforeStage?.Invoke(stage);
            target.CleanupBackup();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(exception.Message);
        }
    }

    private sealed class Target
    {
        private Target(string destination, string stage, string backup, bool existed)
        {
            Destination = destination;
            _stage = stage;
            _backup = backup;
            _existed = existed;
        }

        public string Destination { get; }
        private readonly string _stage;
        private readonly string _backup;
        private readonly bool _existed;
        private bool _staged;
        private bool _backedUp;
        private bool _committed;

        public static Target Prepare(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArtifactIoException("output_path_empty", "Output path must be nonempty.");
            var destination = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(parent) || Path.GetFileName(destination).Length == 0) throw new ArtifactIoException("output_path_invalid", "Output path must name a file.");
            Directory.CreateDirectory(parent);
            if (Directory.Exists(destination)) throw new ArtifactIoException("output_path_directory", $"Output target '{destination}' is a directory.");
            if (File.Exists(destination) && new FileInfo(destination).LinkTarget is not null) throw new ArtifactIoException("output_path_symlink", $"Output target '{destination}' must not be a symbolic link.");
            var nonce = Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);
            return new Target(destination, Path.Combine(parent, $".{Path.GetFileName(destination)}.{nonce}.stage"), Path.Combine(parent, $".{Path.GetFileName(destination)}.{nonce}.backup"), File.Exists(destination));
        }

        public void Stage(ReadOnlySpan<byte> bytes)
        {
            using var stream = new FileStream(_stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            _staged = true;
        }

        public void Backup()
        {
            if (!_existed) return;
            File.Move(Destination, _backup);
            _backedUp = true;
        }

        public void Commit()
        {
            File.Move(_stage, Destination);
            _staged = false;
            _committed = true;
        }

        public void CleanupBackup()
        {
            if (_backedUp)
            {
                File.Delete(_backup);
                _backedUp = false;
            }
        }

        public void Rollback()
        {
            if (_committed && File.Exists(Destination)) File.Delete(Destination);
            _committed = false;
            if (_backedUp && File.Exists(_backup)) File.Move(_backup, Destination);
            _backedUp = false;
            if (_staged && File.Exists(_stage)) File.Delete(_stage);
            _staged = false;
        }
    }
}

/// <summary>Successful publication may retain recoverable backup files if their
/// post-commit cleanup failed. Callers can report those paths for later cleanup
/// without treating the committed artifacts as rolled back.</summary>
public sealed record AtomicWriteOutcome(IReadOnlyList<string> CleanupFailures)
{
    public bool CleanupCompleted => CleanupFailures.Count == 0;
}

internal enum AtomicWriteStage
{
    StageFirst,
    StageSecond,
    BackupFirst,
    BackupSecond,
    CommitFirst,
    CommitSecond,
    CleanupFirst,
    CleanupSecond,
}
