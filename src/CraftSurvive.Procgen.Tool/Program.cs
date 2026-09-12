using CraftSurvive.Procgen.Artifacts;
using CraftSurvive.Procgen;
using CraftSurvive.Procgen.Generation;
using CraftSurvive.Procgen.Workbench;
using CraftSurvive.Procgen.Workloads;

return await ProcgenTool.RunAsync(args);

internal static class ProcgenTool
{
    public static Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 1 && StringComparer.Ordinal.Equals(args[0], "--self-check"))
            {
                ToolSelfCheck.Run();
                Console.WriteLine("CraftSurvive procgen artifact/tool self-check passed.");
                return Task.FromResult(0);
            }
            if (args.Length > 0 && StringComparer.Ordinal.Equals(args[0], "generate-workload-corpus"))
                return Task.FromResult(GenerateWorkloadCorpus(ParseWorkloadCorpus(args)));
            if (args.Length > 0 && StringComparer.Ordinal.Equals(args[0], "generate-workbench"))
                return Task.FromResult(GenerateWorkbench(ParseWorkbench(args)));
            if (args.Length > 0 && StringComparer.Ordinal.Equals(args[0], "repair-workbench"))
                return Task.FromResult(RepairWorkbench(ParseWorkbenchRepair(args)));
            var command = ParseGenerate(args);
            var requestBytes = File.ReadAllBytes(command.RequestPath);
            var request = ArtifactJson.DeserializeRequest(requestBytes);
            var result = new ArtifactGenerator().Generate(request);
            var resultBytes = ArtifactJson.SerializeResult(result);
            var resultHash = ArtifactIdentity.HashBytes(resultBytes);
            var requestHash = ArtifactIdentity.HashRequest(request);
            var exitCode = result.Accepted ? 0 : 3;
            var receipt = new ArtifactReceipt(
                ArtifactReceipt.CurrentKind,
                "generate",
                result.Accepted,
                exitCode,
                requestHash,
                resultHash,
                Path.GetFullPath(command.ResultPath),
                Path.GetFullPath(command.ReceiptPath),
                result.ResultIdentity,
                result.Rejection);
            var write = AtomicArtifactWriter.WritePair(command.ResultPath, resultBytes, command.ReceiptPath, ArtifactJson.SerializeReceipt(receipt));
            foreach (var cleanupFailure in write.CleanupFailures) Console.Error.WriteLine($"warning: artifacts committed but a recoverable backup cleanup failed: {cleanupFailure}");
            Console.WriteLine($"{(result.Accepted ? "accepted" : "rejected")} result={resultHash} identity={result.ResultIdentity ?? result.Rejection?.Code}");
            return Task.FromResult(exitCode);
        }
        catch (ArtifactValidationException exception)
        {
            Console.Error.WriteLine($"request rejected [{exception.Code}]: {exception.Message}");
            return Task.FromResult(2);
        }
        catch (ArtifactIoException exception)
        {
            Console.Error.WriteLine($"I/O failure [{exception.Code}]: {exception.Message}");
            return Task.FromResult(4);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"I/O failure: {exception.Message}");
            return Task.FromResult(4);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"tool failure: {exception.Message}");
            return Task.FromResult(5);
        }
    }

    private static GenerateCommand ParseGenerate(IReadOnlyList<string> args)
    {
        if (args.Count != 7 || !StringComparer.Ordinal.Equals(args[0], "generate")) throw new ArtifactValidationException("usage", "Usage: generate --request <request.json> --out <result.json> --receipt <receipt.json>.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            var key = args[index];
            if (key is not ("--request" or "--out" or "--receipt") || !values.TryAdd(key, args[index + 1]) || string.IsNullOrWhiteSpace(args[index + 1])) throw new ArtifactValidationException("usage", "Each required option must appear exactly once with a nonempty value.");
        }
        if (values.Count != 3) throw new ArtifactValidationException("usage", "Usage requires --request, --out, and --receipt.");
        return new GenerateCommand(values["--request"], values["--out"], values["--receipt"]);
    }

    private static int GenerateWorkloadCorpus(WorkloadCorpusCommand command)
    {
        var corpus = new ArtifactWorkloadCorpusGenerator().GenerateRepresentative();
        var corpusBytes = ArtifactJson.SerializeWorkloadCorpus(corpus);
        var corpusHash = ArtifactIdentity.HashBytes(corpusBytes);
        var receipt = new ArtifactReceipt(
            ArtifactReceipt.CurrentKind,
            "generate-workload-corpus",
            true,
            0,
            corpus.Provenance.SuiteIdentity,
            corpusHash,
            Path.GetFullPath(command.CorpusPath),
            Path.GetFullPath(command.ReceiptPath),
            corpus.CorpusIdentity,
            null);
        var write = AtomicArtifactWriter.WritePair(command.CorpusPath, corpusBytes, command.ReceiptPath, ArtifactJson.SerializeReceipt(receipt));
        foreach (var cleanupFailure in write.CleanupFailures) Console.Error.WriteLine($"warning: artifacts committed but a recoverable backup cleanup failed: {cleanupFailure}");
        Console.WriteLine($"generated workload corpus={corpusHash} identity={corpus.CorpusIdentity}");
        return 0;
    }

    private static WorkloadCorpusCommand ParseWorkloadCorpus(IReadOnlyList<string> args)
    {
        if (args.Count != 5 || !StringComparer.Ordinal.Equals(args[0], "generate-workload-corpus")) throw new ArtifactValidationException("usage", "Usage: generate-workload-corpus --out <corpus.json> --receipt <receipt.json>.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            var key = args[index];
            if (key is not ("--out" or "--receipt") || !values.TryAdd(key, args[index + 1]) || string.IsNullOrWhiteSpace(args[index + 1])) throw new ArtifactValidationException("usage", "Each required option must appear exactly once with a nonempty value.");
        }
        if (values.Count != 2) throw new ArtifactValidationException("usage", "Workload corpus generation requires --out and --receipt.");
        return new WorkloadCorpusCommand(values["--out"], values["--receipt"]);
    }

    private static int GenerateWorkbench(WorkbenchCommand command)
    {
        var candidate = WorkbenchExperiment.Generate(command.Seed, command.Motif, command.Counterexample);
        var candidateBytes = WorkbenchCandidateJson.Serialize(candidate);
        var candidateHash = ArtifactIdentity.HashBytes(candidateBytes);
        var receipt = new ArtifactReceipt(
            ArtifactReceipt.CurrentKind,
            "generate-workbench",
            true,
            0,
            ArtifactIdentity.HashBytes(System.Text.Encoding.UTF8.GetBytes($"workbench-seed:{command.Seed}:motif:{command.Motif}:counterexample:{command.Counterexample.ToString().ToLowerInvariant()}")),
            candidateHash,
            Path.GetFullPath(command.CandidatePath),
            Path.GetFullPath(command.ReceiptPath),
            WorkbenchCandidateJson.Identity(candidate),
            null);
        var write = AtomicArtifactWriter.WritePair(command.CandidatePath, candidateBytes, command.ReceiptPath, ArtifactJson.SerializeReceipt(receipt));
        foreach (var cleanupFailure in write.CleanupFailures) Console.Error.WriteLine($"warning: artifacts committed but a recoverable backup cleanup failed: {cleanupFailure}");
        Console.WriteLine($"generated workbench candidate={candidateHash} identity={receipt.ResultIdentity}");
        return 0;
    }

    private static WorkbenchCommand ParseWorkbench(IReadOnlyList<string> args)
    {
        if (args.Count is < 7 or > 11 || args.Count % 2 == 0 || !StringComparer.Ordinal.Equals(args[0], "generate-workbench")) throw new ArtifactValidationException("usage", "Usage: generate-workbench --seed <unsigned-seed> --out <candidate.json> --receipt <receipt.json> [--motif <motif>] [--counterexample true|false].");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            var key = args[index];
            if (key is not ("--seed" or "--out" or "--receipt" or "--motif" or "--counterexample") || !values.TryAdd(key, args[index + 1]) || string.IsNullOrWhiteSpace(args[index + 1])) throw new ArtifactValidationException("usage", "Each option must appear at most once with a nonempty value.");
        }
        if (!values.ContainsKey("--seed") || !values.ContainsKey("--out") || !values.ContainsKey("--receipt") || !ulong.TryParse(values["--seed"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seed))
            throw new ArtifactValidationException("usage", "generate-workbench requires an unsigned --seed, --out, and --receipt.");
        var motif = values.GetValueOrDefault("--motif", WorkbenchExperiment.CurrentMotif);
        if (motif is not (WorkbenchExperiment.CurrentMotif or WorkbenchExperiment.RecoveryMotif or WorkbenchExperiment.PreviewMotif or WorkbenchExperiment.LargeMotif)) throw new ArtifactValidationException("usage", "generate-workbench accepts only the named return-shortcut, spent-key-recovery, visible-before-access, or branching-complex motifs.");
        var counterexample = false;
        if (values.TryGetValue("--counterexample", out var counterexampleText) && !bool.TryParse(counterexampleText, out counterexample)) throw new ArtifactValidationException("usage", "--counterexample must be true or false.");
        return new WorkbenchCommand(seed, motif, counterexample, values["--out"], values["--receipt"]);
    }

    private static int RepairWorkbench(WorkbenchRepairCommand command)
    {
        var inputPath = Path.GetFullPath(command.InputPath);
        if (StringComparer.Ordinal.Equals(inputPath, Path.GetFullPath(command.ResultPath))
            || StringComparer.Ordinal.Equals(inputPath, Path.GetFullPath(command.ReceiptPath)))
            throw new ArtifactValidationException("repair_input_alias", "Repair input must be distinct from both result and receipt outputs.");
        var parent = WorkbenchCandidateJson.Deserialize(File.ReadAllBytes(inputPath));
        WorkbenchRepairReceipt receipt;
        try
        {
            receipt = WorkbenchRepairJson.Create(parent, command.Operation);
        }
        catch (InvalidOperationException exception)
        {
            throw new ArtifactValidationException("workbench_repair_invalid", exception.Message);
        }
        var write = AtomicArtifactWriter.WritePair(
            command.ResultPath,
            WorkbenchCandidateJson.Serialize(receipt.Result),
            command.ReceiptPath,
            WorkbenchRepairJson.Serialize(receipt));
        foreach (var cleanupFailure in write.CleanupFailures) Console.Error.WriteLine($"warning: artifacts committed but a recoverable backup cleanup failed: {cleanupFailure}");
        Console.WriteLine($"repaired workbench candidate operation={receipt.Operation} identity={receipt.ResultIdentity}");
        return 0;
    }

    private static WorkbenchRepairCommand ParseWorkbenchRepair(IReadOnlyList<string> args)
    {
        if (args.Count != 9 || !StringComparer.Ordinal.Equals(args[0], "repair-workbench")) throw new ArtifactValidationException("usage", "Usage: repair-workbench --input <parent.json> --operation <operation> --out <result.json> --receipt <receipt.json>.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            var key = args[index];
            if (key is not ("--input" or "--operation" or "--out" or "--receipt") || !values.TryAdd(key, args[index + 1]) || string.IsNullOrWhiteSpace(args[index + 1])) throw new ArtifactValidationException("usage", "Each repair option must appear exactly once with a nonempty value.");
        }
        if (values.Count != 4) throw new ArtifactValidationException("usage", "repair-workbench requires --input, --operation, --out, and --receipt.");
        return new WorkbenchRepairCommand(values["--input"], values["--operation"], values["--out"], values["--receipt"]);
    }

    private sealed record GenerateCommand(string RequestPath, string ResultPath, string ReceiptPath);
    private sealed record WorkloadCorpusCommand(string CorpusPath, string ReceiptPath);
    private sealed record WorkbenchCommand(ulong Seed, string Motif, bool Counterexample, string CandidatePath, string ReceiptPath);
    private sealed record WorkbenchRepairCommand(string InputPath, string Operation, string ResultPath, string ReceiptPath);
}

internal static class ToolSelfCheck
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"craftsurvive-procgen-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var generator = new ArtifactGenerator();
            var request = Request(991, GenerationPolicy.Normal);
            var first = generator.Generate(request);
            var second = generator.Generate(request);
            Equal(ArtifactIdentity.HashResult(first), ArtifactIdentity.HashResult(second), "same explicit seed and policy must produce identical durable output");

            var changedSeed = generator.Generate(Request(992, GenerationPolicy.Normal));
            NotEqual(first.ResultIdentity!, changedSeed.ResultIdentity!, "different seed must change accepted identity");
            var changedPreset = generator.Generate(Request(991, GenerationPolicy.Spread));
            NotEqual(first.ResultIdentity!, changedPreset.ResultIdentity!, "different complete policy/preset identity must change accepted identity");

            var malformed = "{\"kind\":\"craftsurvive_procgen.csharp_generation_request.v1\",\"unknown\":true}"u8.ToArray();
            Expect("invalid_request_json", () => ArtifactJson.DeserializeRequest(malformed));

            var resultPath = Path.Combine(root, "result.json");
            var receiptPath = Path.Combine(root, "receipt.json");
            var requestPath = Path.Combine(root, "request.json");
            File.WriteAllBytes(requestPath, ArtifactJson.SerializeRequest(request));
            Equal(0, ProcgenTool.RunAsync(new[] { "generate", "--request", requestPath, "--out", resultPath, "--receipt", receiptPath }).GetAwaiter().GetResult(), "CLI generation must accept a valid explicit request");
            var resultBytes = ArtifactJson.SerializeResult(first);
            Equal(ArtifactIdentity.HashResult(first), ArtifactIdentity.HashBytes(File.ReadAllBytes(resultPath)), "in-memory and staged file artifacts must agree");
            True(!Directory.EnumerateFiles(root, ".*.stage").Any() && !Directory.EnumerateFiles(root, ".*.backup").Any(), "atomic pair write must leave no staging files");

            var before = File.ReadAllBytes(resultPath);
            Expect("output_paths_alias", () => AtomicArtifactWriter.WritePair(resultPath, "replacement"u8, resultPath, "receipt"u8));
            True(before.SequenceEqual(File.ReadAllBytes(resultPath)), "preflight rejection must leave published output untouched");

            var cleanupResult = Path.Combine(root, "cleanup-result.json");
            var cleanupReceipt = Path.Combine(root, "cleanup-receipt.json");
            File.WriteAllText(cleanupResult, "old-result");
            File.WriteAllText(cleanupReceipt, "old-receipt");
            var cleanupOutcome = AtomicArtifactWriter.WritePairWithProbe(cleanupResult, "new-result"u8, cleanupReceipt, "new-receipt"u8, stage =>
            {
                if (stage == AtomicWriteStage.CleanupFirst) throw new IOException("injected backup cleanup failure");
            });
            True(!cleanupOutcome.CleanupCompleted && cleanupOutcome.CleanupFailures.Count == 1, "post-commit cleanup failures must be observable without rollback");
            Equal("new-result", File.ReadAllText(cleanupResult), "cleanup failure must not roll back the first committed artifact");
            Equal("new-receipt", File.ReadAllText(cleanupReceipt), "cleanup failure must not roll back the second committed artifact");

            var malformedPath = Path.Combine(root, "malformed-request.json");
            var malformedResult = Path.Combine(root, "malformed-result.json");
            var malformedReceipt = Path.Combine(root, "malformed-receipt.json");
            File.WriteAllBytes(malformedPath, malformed);
            Equal(2, ProcgenTool.RunAsync(new[] { "generate", "--request", malformedPath, "--out", malformedResult, "--receipt", malformedReceipt }).GetAwaiter().GetResult(), "malformed requests must return the request failure status");
            True(!File.Exists(malformedResult) && !File.Exists(malformedReceipt), "malformed requests must not publish partial output");

            var generatedCorpus = new ArtifactWorkloadCorpusGenerator().GenerateRepresentative();
            var regeneratedCorpus = new ArtifactWorkloadCorpusGenerator().GenerateRepresentative();
            var generatedCorpusBytes = ArtifactJson.SerializeWorkloadCorpus(generatedCorpus);
            EqualBytes(generatedCorpusBytes, ArtifactJson.SerializeWorkloadCorpus(regeneratedCorpus), "representative C# corpus regeneration must be byte-stable");
            var corpusPath = Path.Combine(root, "workload-corpus.json");
            var corpusReceiptPath = Path.Combine(root, "workload-corpus.receipt.json");
            Equal(0, ProcgenTool.RunAsync(new[] { "generate-workload-corpus", "--out", corpusPath, "--receipt", corpusReceiptPath }).GetAwaiter().GetResult(), "CLI workload corpus generation must accept the current C# corpus");
            var readCorpus = ArtifactJson.DeserializeWorkloadCorpus(File.ReadAllBytes(corpusPath));
            Equal(generatedCorpus.CorpusIdentity, readCorpus.CorpusIdentity, "strict workload corpus readback must retain current provenance and identity");
            Expect("workload_corpus_identity_invalid", () => WorkloadCorpusValidator.Validate(readCorpus with { CorpusIdentity = "tampered" }));
            var committedCorpusPath = RepositoryFile("tests", "Procgen", "fixtures", "csharp-workload-corpus.v1.json");
            EqualBytes(generatedCorpusBytes, File.ReadAllBytes(committedCorpusPath), "checked workload corpus must regenerate through the owning C# path");
            ArtifactJson.DeserializeWorkloadCorpus(File.ReadAllBytes(committedCorpusPath));

            var workbenchInput = Path.Combine(root, "workbench-parent.json");
            var workbenchResult = Path.Combine(root, "workbench-result.json");
            var workbenchReceipt = Path.Combine(root, "workbench-repair.receipt.json");
            var workbenchFailure = WorkbenchExperiment.Generate(991, WorkbenchExperiment.CurrentMotif, counterexample: true);
            File.WriteAllBytes(workbenchInput, WorkbenchCandidateJson.Serialize(workbenchFailure));
            var workbenchInputBytes = File.ReadAllBytes(workbenchInput);
            Equal(0, ProcgenTool.RunAsync(new[] { "repair-workbench", "--input", workbenchInput, "--operation", WorkbenchRepair.RestoreSwitch, "--out", workbenchResult, "--receipt", workbenchReceipt }).GetAwaiter().GetResult(), "CLI workbench repair must publish the canonical repair pair");
            EqualBytes(workbenchInputBytes, File.ReadAllBytes(workbenchInput), "CLI workbench repair must preserve its input artifact");
            var repairedWorkbench = WorkbenchCandidateJson.Deserialize(File.ReadAllBytes(workbenchResult));
            True(repairedWorkbench.SwitchEnabled, "CLI workbench repair must restore only the named candidate fact");
            var repairedReceipt = WorkbenchRepairJson.Deserialize(File.ReadAllBytes(workbenchReceipt));
            Equal(WorkbenchCandidateJson.Identity(repairedWorkbench), repairedReceipt.ResultIdentity, "CLI repair result and strict receipt must roundtrip as a coherent pair");
            Equal(2, ProcgenTool.RunAsync(new[] { "repair-workbench", "--input", workbenchResult, "--operation", WorkbenchRepair.RestoreSwitch, "--out", Path.Combine(root, "workbench-noop.json"), "--receipt", Path.Combine(root, "workbench-noop.receipt.json") }).GetAwaiter().GetResult(), "CLI workbench repair must reject a repeat no-op");
            Equal(2, ProcgenTool.RunAsync(new[] { "repair-workbench", "--input", workbenchInput, "--operation", WorkbenchRepair.RestoreSwitch, "--out", workbenchInput, "--receipt", Path.Combine(root, "workbench-alias.receipt.json") }).GetAwaiter().GetResult(), "CLI workbench repair must reject input/result aliases");
            Equal(2, ProcgenTool.RunAsync(new[] { "repair-workbench", "--input", workbenchInput, "--operation", WorkbenchRepair.RestoreSwitch, "--out", Path.Combine(root, "workbench-alias.json"), "--receipt", workbenchInput }).GetAwaiter().GetResult(), "CLI workbench repair must reject input/receipt aliases");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ArtifactGenerationRequest Request(ulong seed, GenerationPolicy policy)
    {
        var core = new GraphCore();
        var candidate = core.CreateInitial(new SeedIntent("artifact-check", "Artifact self check", new[] { "lock", "loop" }), seed);
        candidate = Accept(core.Apply(candidate, GraphRule.LockKeyLoop, seed + 1));
        return new ArtifactGenerationRequest(ArtifactGenerationRequest.CurrentKind, seed, GenerationPresets.Get(policy.Id), policy, candidate, ShapeCatalog.Default);
    }

    private static Candidate Accept(RuleApplication result) => result.Accepted ? result.Candidate : throw new InvalidOperationException(string.Join(", ", result.Diagnostics.Select(diagnostic => diagnostic.Code)));
    private static string RepositoryFile(params string[] pathSegments)
    {
        for (var current = new DirectoryInfo(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, Path.Combine(pathSegments));
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException($"Could not find checked repository artifact '{Path.Combine(pathSegments)}'.");
    }
    private static void Expect(string code, Action action)
    {
        try { action(); throw new InvalidOperationException($"Expected {code}."); }
        catch (ArtifactValidationException exception) when (StringComparer.Ordinal.Equals(exception.Code, code)) { }
        catch (ArtifactIoException exception) when (StringComparer.Ordinal.Equals(exception.Code, code)) { }
    }
    private static void Equal(string left, string right, string detail) { if (!StringComparer.Ordinal.Equals(left, right)) throw new InvalidOperationException(detail); }
    private static void Equal(int left, int right, string detail) { if (left != right) throw new InvalidOperationException(detail); }
    private static void NotEqual(string left, string right, string detail) { if (StringComparer.Ordinal.Equals(left, right)) throw new InvalidOperationException(detail); }
    private static void EqualBytes(byte[] left, byte[] right, string detail) { if (!left.AsSpan().SequenceEqual(right)) throw new InvalidOperationException(detail); }
    private static void True(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
}
