using System.Text;

namespace RimPulse.Core.Build;

/// <summary>One thing worth writing to the diagnostic log: a one-line message and an indented detail block.</summary>
public sealed record BuildLogSummary(string Message, string? Detail);

/// <summary>
/// Turns a build into the text the diagnostic log keeps. A build can produce over a thousand compiler
/// diagnostics, so the log records the build's inputs, its reference set, the first diagnostics of each
/// severity and an error-code histogram — enough to diagnose a failure from the log file alone, without
/// dumping megabytes of output.
/// </summary>
public static class BuildLogReport
{
    /// <summary>How many diagnostics of one severity are written in full.</summary>
    public const int MaxListedDiagnostics = 40;

    /// <summary>How many reference assembly names are listed before the rest are summarised.</summary>
    public const int MaxListedReferences = 12;

    /// <summary>How many distinct error codes are listed in the histogram.</summary>
    public const int MaxListedErrorCodes = 8;

    /// <summary>
    /// Compiler error codes that usually mean an assembly is missing from the reference set rather than that
    /// the code is wrong. Used only to add an explanation to the log, never to change the build result.
    /// </summary>
    private static readonly string[] MissingReferenceCodes =
    [
        "CS0246", // type or namespace not found
        "CS0234", // type or namespace not found in namespace
        "CS0103", // name does not exist in the current context
        "CS0012", // type is defined in an assembly that is not referenced
        "CS1069", // type forwarded to an assembly that is not referenced
        "CS0400", // type or namespace not found in the global namespace
    ];

    public static BuildLogSummary Describe(
        BuildInputInspection inputs,
        GameReferenceSet references,
        ModBuildResult? build,
        string? referenceDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(references);

        var message = build?.Summary
                      ?? inputs.Problems.FirstOrDefault()
                      ?? references.Problems.FirstOrDefault()
                      ?? "There was nothing to build.";

        var builder = new StringBuilder();

        AppendInputs(builder, inputs);
        AppendReferences(builder, references, referenceDirectory);
        AppendPreCompilerProblems(builder, inputs, references);

        if (build is not null)
        {
            AppendDiagnostics(builder, build);
        }

        return new BuildLogSummary(message, builder.Length == 0 ? null : builder.ToString().TrimEnd());
    }

    private static void AppendInputs(StringBuilder builder, BuildInputInspection inputs)
    {
        builder.Append("Projects: ").Append(inputs.Units.Count);

        if (inputs.Units.Count == 0)
        {
            builder.AppendLine(" (no .csproj was discovered)");
            return;
        }

        builder.Append(" project(s), ")
            .Append(inputs.SourceFileCount)
            .Append(" source file(s)")
            .AppendLine();

        foreach (var unit in inputs.Units)
        {
            builder.Append("  ").Append(Path.GetFileName(unit.ProjectFilePath))
                .Append(" -> ").Append(unit.AssemblyName).Append(".dll, ")
                .Append(unit.TargetFramework ?? "no target framework").Append(", ")
                .Append(unit.SourceFiles.Count).Append(" source(s), ")
                .Append(unit.ReferencePaths.Count).Append(" reference(s), ")
                .Append(unit.ProjectReferences.Count).Append(" project reference(s)")
                .AppendLine();

            var options = new List<string>();

            if (unit.DefineConstants.Count > 0)
            {
                options.Add($"defines: {string.Join("; ", unit.DefineConstants)}");
            }

            if (!string.IsNullOrWhiteSpace(unit.LangVersion))
            {
                options.Add($"langversion: {unit.LangVersion}");
            }

            if (unit.AllowUnsafe)
            {
                options.Add("unsafe: yes");
            }

            if (unit.NullableEnabled)
            {
                options.Add("nullable: enabled");
            }

            if (options.Count > 0)
            {
                builder.Append("    ").AppendLine(string.Join(" | ", options));
            }
        }
    }

    private static void AppendReferences(StringBuilder builder, GameReferenceSet references, string? referenceDirectory)
    {
        builder.Append("References: ").Append(references.AssemblyPaths.Count).Append(" assembly(ies)");

        if (!string.IsNullOrWhiteSpace(referenceDirectory))
        {
            builder.Append(" from '").Append(referenceDirectory).Append('\'');
        }

        builder.AppendLine();

        if (references.AssemblyPaths.Count == 0)
        {
            return;
        }

        var names = references.AssemblyPaths
            .Select(path => Path.GetFileName(path))
            .Where(name => !string.IsNullOrEmpty(name))
            .ToArray();

        builder.Append("  ").AppendLine(string.Join(", ", names.Take(MaxListedReferences)));

        if (names.Length > MaxListedReferences)
        {
            builder.Append("  … and ").Append(names.Length - MaxListedReferences).AppendLine(" more");
        }
    }

    private static void AppendPreCompilerProblems(
        StringBuilder builder, BuildInputInspection inputs, GameReferenceSet references)
    {
        var problems = inputs.Problems.Concat(references.Problems).ToArray();
        if (problems.Length == 0)
        {
            return;
        }

        builder.Append("Problems found before compiling: ").Append(problems.Length).AppendLine();

        foreach (var problem in problems.Take(MaxListedDiagnostics))
        {
            builder.Append("  ").AppendLine(problem);
        }

        if (problems.Length > MaxListedDiagnostics)
        {
            builder.Append("  … and ").Append(problems.Length - MaxListedDiagnostics).AppendLine(" more");
        }
    }

    private static void AppendDiagnostics(StringBuilder builder, ModBuildResult build)
    {
        var located = build.Units
            .SelectMany(unit => unit.Compilation.Diagnostics.Select(diagnostic => (unit.AssemblyName, Diagnostic: diagnostic)))
            .ToArray();

        var errors = located.Where(entry => entry.Diagnostic.Severity == BuildDiagnosticSeverity.Error).ToArray();
        var warnings = located.Where(entry => entry.Diagnostic.Severity == BuildDiagnosticSeverity.Warning).ToArray();

        AppendSeverity(builder, "Errors", errors);
        AppendSeverity(builder, "Warnings", warnings);

        if (errors.Length > 0)
        {
            AppendErrorCodes(builder, errors.Select(entry => entry.Diagnostic.Id));

            var unresolved = errors
                .Select(entry => entry.Diagnostic.Id)
                .FirstOrDefault(MissingReferenceCodes.Contains);

            if (unresolved is not null)
            {
                builder.Append("Hint: ").Append(unresolved)
                    .AppendLine(" usually means the assembly that declares the type is not referenced. RimPulse references");
                builder.AppendLine("  every .dll in the installation's managed directory, every HintPath and every restored");
                builder.AppendLine("  NuGet package it could resolve; conditional and imported MSBuild items are not evaluated.");
            }
        }

        if (build.Problems.Count > 0)
        {
            builder.Append("Build problems: ").Append(build.Problems.Count).AppendLine();

            foreach (var problem in build.Problems.Take(MaxListedDiagnostics))
            {
                builder.Append("  ").AppendLine(problem);
            }

            if (build.Problems.Count > MaxListedDiagnostics)
            {
                builder.Append("  … and ").Append(build.Problems.Count - MaxListedDiagnostics).AppendLine(" more");
            }
        }
    }

    private static void AppendSeverity(
        StringBuilder builder,
        string label,
        IReadOnlyList<(string AssemblyName, BuildDiagnostic Diagnostic)> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        builder.Append(label).Append(": ").Append(entries.Count);

        if (entries.Count > MaxListedDiagnostics)
        {
            builder.Append(" (first ").Append(MaxListedDiagnostics).Append(')');
        }

        builder.AppendLine();

        foreach (var entry in entries.Take(MaxListedDiagnostics))
        {
            builder.Append("  ").Append(entry.AssemblyName).Append(": ").AppendLine(entry.Diagnostic.Display);
        }

        if (entries.Count > MaxListedDiagnostics)
        {
            builder.Append("  … ").Append(entries.Count - MaxListedDiagnostics)
                .Append(" more ").Append(label.ToLowerInvariant()).AppendLine(" not shown");
        }
    }

    private static void AppendErrorCodes(StringBuilder builder, IEnumerable<string> ids)
    {
        var histogram = ids
            .GroupBy(id => id, StringComparer.Ordinal)
            .Select(group => (Code: group.Key, Count: group.Count()))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Code, StringComparer.Ordinal)
            .ToArray();

        var description = string.Join(", ", histogram.Take(MaxListedErrorCodes).Select(entry => $"{entry.Code} ×{entry.Count}"));

        if (histogram.Length > MaxListedErrorCodes)
        {
            description += $", … {histogram.Length - MaxListedErrorCodes} more code(s)";
        }

        builder.Append("Error codes: ").AppendLine(description);
    }
}
