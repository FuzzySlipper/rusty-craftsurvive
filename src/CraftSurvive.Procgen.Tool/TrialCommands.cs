using CraftSurvive.Procgen.Artifacts;

internal static class TrialCommands
{
    public static int Run(IReadOnlyList<string> args) => args[0] switch
    {
        "trial-bank" => GenerateBank(ParseBank(args)),
        "trial-inspect" => InspectBank(ParseInspect(args)),
        "trial-evaluate" => Evaluate(ParseEvaluate(args)),
        _ => throw new ArtifactValidationException("usage", "Usage: trial-bank --out <bank.json> --baseline <baseline.json>; trial-inspect --bank <bank.json>; or trial-evaluate --bank <bank.json> --submission <submission.json> --out <report.json> --receipt <submission-receipt.json>.")
    };

    private static int GenerateBank(BankCommand command)
    {
        var bank = WorkbenchTrial.Generate();
        var baseline = WorkbenchTrial.Baseline(bank);
        WritePair(command.BankPath, WorkbenchTrialJson.SerializeBank(bank), command.BaselinePath,
            WorkbenchTrialJson.SerializeSubmission(baseline));
        Console.WriteLine($"generated trial bank identity={bank.Identity}");
        return 0;
    }

    private static int InspectBank(InspectCommand command)
    {
        var bank = WorkbenchTrialJson.ReadBank(File.ReadAllBytes(command.BankPath));
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(WorkbenchTrialJson.SerializeObservations(WorkbenchTrial.Inspect(bank))));
        return 0;
    }

    private static int Evaluate(EvaluateCommand command)
    {
        RejectInputOutputAliases(command.BankPath, command.SubmissionPath, command.ReportPath, command.ReceiptPath);
        var bank = WorkbenchTrialJson.ReadBank(File.ReadAllBytes(command.BankPath));
        var submission = WorkbenchTrialJson.ReadSubmission(File.ReadAllBytes(command.SubmissionPath));
        var evaluation = WorkbenchTrial.Evaluate(bank, submission);
        WritePair(command.ReportPath, WorkbenchTrialJson.SerializeEvaluation(evaluation), command.ReceiptPath,
            WorkbenchTrialJson.SerializeSubmission(submission));
        Console.WriteLine($"evaluated trial accepted={evaluation.Accepted} bank={evaluation.BankIdentity}");
        return evaluation.Accepted == evaluation.Results.Length ? 0 : 3;
    }

    private static void WritePair(string firstPath, byte[] first, string secondPath, byte[] second)
    {
        var write = AtomicArtifactWriter.WritePair(firstPath, first, secondPath, second);
        foreach (var cleanupFailure in write.CleanupFailures)
            Console.Error.WriteLine($"warning: artifacts committed but a recoverable backup cleanup failed: {cleanupFailure}");
    }

    private static BankCommand ParseBank(IReadOnlyList<string> args)
    {
        var values = ParseOptions(args, "trial-bank", "Usage: trial-bank --out <bank.json> --baseline <baseline.json>.", "--out", "--baseline");
        return new BankCommand(values["--out"], values["--baseline"]);
    }

    private static InspectCommand ParseInspect(IReadOnlyList<string> args)
    {
        var values = ParseOptions(args, "trial-inspect", "Usage: trial-inspect --bank <bank.json>.", "--bank");
        return new InspectCommand(values["--bank"]);
    }

    private static EvaluateCommand ParseEvaluate(IReadOnlyList<string> args)
    {
        var values = ParseOptions(args, "trial-evaluate", "Usage: trial-evaluate --bank <bank.json> --submission <submission.json> --out <report.json> --receipt <submission-receipt.json>.", "--bank", "--submission", "--out", "--receipt");
        return new EvaluateCommand(values["--bank"], values["--submission"], values["--out"], values["--receipt"]);
    }

    private static Dictionary<string, string> ParseOptions(IReadOnlyList<string> args, string command, string usage, params string[] options)
    {
        if (args.Count != options.Length * 2 + 1 || !StringComparer.Ordinal.Equals(args[0], command))
            throw new ArtifactValidationException("usage", usage);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            var key = args[index];
            var value = args[index + 1];
            if (!options.Contains(key, StringComparer.Ordinal) || !values.TryAdd(key, value) || string.IsNullOrWhiteSpace(value))
                throw new ArtifactValidationException("usage", "Each required option must appear exactly once with a nonempty value.");
        }
        if (values.Count != options.Length) throw new ArtifactValidationException("usage", usage);
        return values;
    }

    private static void RejectInputOutputAliases(string bankPath, string submissionPath, string reportPath, string receiptPath)
    {
        var inputs = new[] { Path.GetFullPath(bankPath), Path.GetFullPath(submissionPath) };
        var outputs = new[] { Path.GetFullPath(reportPath), Path.GetFullPath(receiptPath) };
        if (inputs.Any(input => outputs.Contains(input, StringComparer.Ordinal)))
            throw new ArtifactValidationException("trial_input_output_alias", "Trial bank and submission inputs must be distinct from report and receipt outputs.");
    }

    private sealed record BankCommand(string BankPath, string BaselinePath);
    private sealed record InspectCommand(string BankPath);
    private sealed record EvaluateCommand(string BankPath, string SubmissionPath, string ReportPath, string ReceiptPath);
}
