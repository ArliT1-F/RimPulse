using RimPulse.Core.Build;

namespace RimPulse.App.ViewModels;

/// <summary>
/// One line of build output: either a compiler diagnostic with its location, or a problem RimPulse
/// itself detected before the compiler ran (no project file, an unresolved hint path, …).
/// </summary>
public sealed class BuildDiagnosticRow
{
    private BuildDiagnosticRow(BuildDiagnostic? diagnostic, string? problem, string? projectName)
    {
        Diagnostic = diagnostic;
        Problem = problem;
        ProjectName = projectName;
    }

    public BuildDiagnostic? Diagnostic { get; }

    /// <summary>Set when RimPulse, not the compiler, produced this line.</summary>
    public string? Problem { get; }

    public string? ProjectName { get; }

    public bool IsError => Diagnostic?.Severity == BuildDiagnosticSeverity.Error;

    public static BuildDiagnosticRow FromDiagnostic(BuildDiagnostic diagnostic, string? projectName = null)
        => new(diagnostic, null, projectName);

    public static BuildDiagnosticRow FromProblem(string problem, string? projectName = null)
        => new(null, problem, projectName);

    /// <summary>Project, location, identifier and message, so a row is readable on its own.</summary>
    public string Display
    {
        get
        {
            if (Problem is not null)
            {
                return ProjectName is null ? Problem : $"{ProjectName}: {Problem}";
            }

            return ProjectName is null ? Diagnostic!.Display : $"{ProjectName}: {Diagnostic!.Display}";
        }
    }
}
