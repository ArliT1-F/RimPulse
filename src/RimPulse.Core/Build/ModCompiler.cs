using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace RimPulse.Core.Build;

public enum BuildDiagnosticSeverity
{
    Error = 0,
    Warning = 1,
    Info = 2,
}

/// <summary>One compiler message, mapped from Roslyn without losing the file, line and column.</summary>
public sealed record BuildDiagnostic(
    BuildDiagnosticSeverity Severity,
    string Id,
    string Message,
    string? FilePath,
    int Line,
    int Column)
{
    public string LocationDisplay => FilePath is null
        ? string.Empty
        : $"{Path.GetFileName(FilePath)}:{Line}:{Column}";

    public string SeverityLabel => Severity switch
    {
        BuildDiagnosticSeverity.Error => "error",
        BuildDiagnosticSeverity.Warning => "warning",
        _ => "info",
    };

    public string Display => FilePath is null
        ? $"{SeverityLabel} {Id}: {Message}"
        : $"{LocationDisplay} {SeverityLabel} {Id}: {Message}";
}

/// <summary>Everything one <see cref="ModCompiler.Compile"/> call needs.</summary>
public sealed record CompileRequest(
    string AssemblyName,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> ReferencePaths,
    IReadOnlyList<string>? DefineConstants = null,
    string? LangVersion = null,
    bool AllowUnsafe = false,
    bool NullableEnabled = false,
    string? OutputPath = null,
    bool EmitDebugSymbols = true);

/// <summary>The outcome of a compile: what was produced, and every diagnostic with its location.</summary>
public sealed record CompilationResult(
    bool Succeeded,
    string? OutputPath,
    string? PdbPath,
    IReadOnlyList<BuildDiagnostic> Diagnostics,
    IReadOnlyList<string> Problems,
    TimeSpan Elapsed)
{
    public int ErrorCount => Diagnostics.Count(diagnostic => diagnostic.Severity == BuildDiagnosticSeverity.Error);

    public int WarningCount => Diagnostics.Count(diagnostic => diagnostic.Severity == BuildDiagnosticSeverity.Warning);

    /// <summary>A one-line summary suitable for a status bar.</summary>
    public string Summary => Problems.Count > 0
        ? Problems[0]
        : Succeeded
            ? $"Compiled {Path.GetFileName(OutputPath ?? "assembly")} with {WarningCount} warning(s) in {Elapsed.TotalSeconds:0.0}s."
            : $"Compilation failed with {ErrorCount} error(s) and {WarningCount} warning(s).";
}

/// <summary>
/// Compiles a mod's C# sources in-process with Roslyn, against the assemblies supplied by the caller.
/// </summary>
/// <remarks>
/// <para>
/// In-process compilation is deliberate: a mod project's own <c>dotnet build</c> cannot run on Linux when it
/// targets .NET Framework 4.7.2, and RimPulse needs structured diagnostics rather than parsed console output.
/// </para>
/// <para>
/// The assembly is emitted to memory first and only written to disk when compilation succeeded, so a failed
/// build can never leave a half-written DLL that the game would load. <c>DEBUG</c> and <c>TRACE</c> are defined
/// unless the project defines them itself, because RimPulse builds a development (not release) assembly.
/// </para>
/// </remarks>
public static class ModCompiler
{
    /// <summary>Defines a development build implies; a project's own defines are merged, not replaced.</summary>
    private static readonly string[] ImplicitDefines = ["DEBUG", "TRACE"];

    public static CompilationResult Compile(CompileRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var problems = new List<string>();
        var diagnostics = new List<BuildDiagnostic>();

        if (string.IsNullOrWhiteSpace(request.AssemblyName))
        {
            problems.Add("The project declares no assembly name.");
            return new CompilationResult(false, null, null, diagnostics, problems, started.Elapsed);
        }

        var sourceFiles = new List<string>();
        foreach (var path in request.SourceFiles)
        {
            if (File.Exists(path))
            {
                sourceFiles.Add(path);
            }
            else
            {
                problems.Add($"Source file '{path}' does not exist.");
            }
        }

        if (sourceFiles.Count == 0)
        {
            problems.Add("The project has no readable C# source files to compile.");
            return new CompilationResult(false, null, null, diagnostics, problems, started.Elapsed);
        }

        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(ResolveLanguageVersion(request.LangVersion, problems))
            .WithPreprocessorSymbols(MergedDefines(request.DefineConstants));

        var syntaxTrees = new List<SyntaxTree>(sourceFiles.Count);
        foreach (var file in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string text;
            try
            {
                text = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"Could not read '{file}': {ex.Message}");
                continue;
            }

            syntaxTrees.Add(CSharpSyntaxTree.ParseText(
                SourceText.From(text, Encoding.UTF8),
                parseOptions,
                path: file,
                cancellationToken: cancellationToken));
        }

        if (syntaxTrees.Count == 0)
        {
            return new CompilationResult(false, null, null, diagnostics, problems, started.Elapsed);
        }

        var references = new List<MetadataReference>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in request.ReferencePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!seenPaths.Add(path))
            {
                continue;
            }

            // Roslyn rejects two references with the same identity (CS1703) and then reports every shared type
            // as ambiguous, so a duplicate simple name is dropped here as a last guard rather than compiled in.
            var fileName = Path.GetFileName(path);
            if (fileName.Length > 0 && !seenNames.Add(fileName))
            {
                problems.Add($"'{fileName}' is referenced more than once; only the first copy ('{path}') was used.");
                continue;
            }

            try
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
            {
                problems.Add($"'{Path.GetFileName(path)}' could not be used as a reference: {ex.Message}");
            }
        }

        if (references.Count == 0)
        {
            problems.Add("No reference assemblies could be loaded, so nothing can be compiled against the game.");
            return new CompilationResult(false, null, null, diagnostics, problems, started.Elapsed);
        }

        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Debug,
            allowUnsafe: request.AllowUnsafe,
            nullableContextOptions: request.NullableEnabled
                ? NullableContextOptions.Enable
                : NullableContextOptions.Disable,
            deterministic: true,
            concurrentBuild: true);

        var compilation = CSharpCompilation.Create(request.AssemblyName, syntaxTrees, references, options);

        using var peStream = new MemoryStream();
        using var pdbStream = request.EmitDebugSymbols ? new MemoryStream() : null;
        var emitOptions = new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb);

        EmitResult emitResult;
        try
        {
            emitResult = compilation.Emit(
                peStream,
                pdbStream,
                options: emitOptions,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            problems.Add($"The compiler could not emit the assembly: {ex.Message}");
            return new CompilationResult(false, null, null, diagnostics, problems, started.Elapsed);
        }

        diagnostics.AddRange(emitResult.Diagnostics
            .Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden)
            .Select(MapDiagnostic)
            .OrderBy(diagnostic => diagnostic.Severity)
            .ThenBy(diagnostic => diagnostic.FilePath, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Line)
            .ThenBy(diagnostic => diagnostic.Column));

        if (!emitResult.Success)
        {
            return new CompilationResult(false, null, null, diagnostics, problems, started.Elapsed);
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            // Validation-only run: the assembly was produced in memory and is discarded.
            return new CompilationResult(true, null, null, diagnostics, problems, started.Elapsed);
        }

        string? pdbPath = null;
        try
        {
            var outputDirectory = Path.GetDirectoryName(request.OutputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            File.WriteAllBytes(request.OutputPath, peStream.ToArray());

            if (pdbStream is not null && pdbStream.Length > 0)
            {
                pdbPath = Path.ChangeExtension(request.OutputPath, ".pdb");
                File.WriteAllBytes(pdbPath, pdbStream.ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problems.Add($"The compiled assembly could not be written to '{request.OutputPath}': {ex.Message}");
            return new CompilationResult(false, null, null, diagnostics, problems, started.Elapsed);
        }

        return new CompilationResult(true, request.OutputPath, pdbPath, diagnostics, problems, started.Elapsed);
    }

    private static LanguageVersion ResolveLanguageVersion(string? value, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LanguageVersion.Latest;
        }

        if (LanguageVersionFacts.TryParse(value.Trim(), out var parsed))
        {
            return parsed;
        }

        problems.Add($"The project's LangVersion '{value}' is not a language version this compiler knows; the latest one was used.");
        return LanguageVersion.Latest;
    }

    private static IReadOnlyList<string> MergedDefines(IReadOnlyList<string>? projectDefines)
    {
        var defines = new List<string>();

        foreach (var define in (projectDefines ?? []).Concat(ImplicitDefines))
        {
            var trimmed = define?.Trim();
            if (!string.IsNullOrEmpty(trimmed)
                && !defines.Contains(trimmed, StringComparer.Ordinal))
            {
                defines.Add(trimmed);
            }
        }

        return defines;
    }

    private static BuildDiagnostic MapDiagnostic(Diagnostic diagnostic)
    {
        var severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => BuildDiagnosticSeverity.Error,
            DiagnosticSeverity.Warning => BuildDiagnosticSeverity.Warning,
            _ => BuildDiagnosticSeverity.Info,
        };

        string? filePath = null;
        var line = 0;
        var column = 0;

        if (diagnostic.Location.IsInSource)
        {
            var span = diagnostic.Location.GetLineSpan();
            filePath = string.IsNullOrEmpty(span.Path) ? null : span.Path;
            line = span.StartLinePosition.Line + 1;
            column = span.StartLinePosition.Character + 1;
        }

        return new BuildDiagnostic(
            severity,
            diagnostic.Id,
            diagnostic.GetMessage(CultureInfo.InvariantCulture),
            filePath,
            line,
            column);
    }
}
