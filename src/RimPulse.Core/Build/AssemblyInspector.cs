using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RimPulse.Core.Build;

/// <summary>One type defined in a compiled assembly, as metadata describes it.</summary>
public sealed record AssemblyTypeInfo(string Namespace, string Name, bool IsPublic)
{
    public string FullName => string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";
}

/// <summary>
/// What a compiled assembly contains, read from its metadata without loading or executing it. This is what
/// RimPulse can honestly say about a build result beyond "the compiler succeeded".
/// </summary>
public sealed record AssemblyInspection(
    string AssemblyPath,
    string? Name,
    string? Version,
    IReadOnlyList<string> References,
    IReadOnlyList<AssemblyTypeInfo> Types,
    IReadOnlyList<string> Problems)
{
    /// <summary>Set when the type list was truncated; metadata of a large mod can define thousands.</summary>
    public bool TypesTruncated { get; init; }

    public int TypeCount => Types.Count;

    public bool ReferencesHarmony => References.Any(reference =>
        reference.Equals("0Harmony", StringComparison.OrdinalIgnoreCase)
        || reference.StartsWith("Harmony", StringComparison.OrdinalIgnoreCase));

    public string Summary => Name is null
        ? Problems.FirstOrDefault() ?? "The assembly could not be read."
        : $"{Name} {Version}: {TypeCount}{(TypesTruncated ? "+" : string.Empty)} type(s), " +
          $"{References.Count} reference(s){(ReferencesHarmony ? ", Harmony patching" : string.Empty)}.";
}

/// <summary>
/// Reads a compiled mod assembly with <c>System.Reflection.Metadata</c>: names, version, assembly references
/// and the defined types. The assembly is never loaded into the RimPulse process, so inspecting a mod cannot
/// execute mod code.
/// </summary>
public static class AssemblyInspector
{
    /// <summary>Upper bound on the types reported, so a huge assembly cannot flood the UI.</summary>
    public const int MaxTypes = 1000;

    public static AssemblyInspection Inspect(string assemblyPath)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            problems.Add("No assembly path was given.");
            return new AssemblyInspection(assemblyPath ?? string.Empty, null, null, [], [], problems);
        }

        if (!File.Exists(assemblyPath))
        {
            problems.Add($"The assembly '{assemblyPath}' does not exist.");
            return new AssemblyInspection(assemblyPath, null, null, [], [], problems);
        }

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);

            if (!peReader.HasMetadata)
            {
                problems.Add($"'{Path.GetFileName(assemblyPath)}' is a native binary, not a .NET assembly.");
                return new AssemblyInspection(assemblyPath, null, null, [], [], problems);
            }

            var reader = peReader.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                problems.Add($"'{Path.GetFileName(assemblyPath)}' is a module without an assembly manifest.");
                return new AssemblyInspection(assemblyPath, null, null, [], [], problems);
            }

            var definition = reader.GetAssemblyDefinition();
            var name = reader.GetString(definition.Name);
            var version = definition.Version?.ToString();

            var references = new List<string>();
            foreach (var handle in reader.AssemblyReferences)
            {
                references.Add(reader.GetString(reader.GetAssemblyReference(handle).Name));
            }

            references.Sort(StringComparer.OrdinalIgnoreCase);

            var types = new List<AssemblyTypeInfo>();
            var truncated = false;

            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                var typeName = reader.GetString(type.Name);

                // <Module> is the pseudo-type every assembly defines for global members.
                if (typeName == "<Module>")
                {
                    continue;
                }

                if (types.Count >= MaxTypes)
                {
                    truncated = true;
                    break;
                }

                var visibility = type.Attributes & TypeAttributes.VisibilityMask;
                var isPublic = visibility is TypeAttributes.Public or TypeAttributes.NestedPublic or TypeAttributes.NestedFamily;

                types.Add(new AssemblyTypeInfo(
                    reader.GetString(type.Namespace),
                    typeName,
                    isPublic));
            }

            return new AssemblyInspection(assemblyPath, name, version, references, types, problems)
            {
                TypesTruncated = truncated,
            };
        }
        catch (BadImageFormatException ex)
        {
            problems.Add($"'{Path.GetFileName(assemblyPath)}' is not a valid .NET assembly: {ex.Message}");
            return new AssemblyInspection(assemblyPath, null, null, [], [], problems);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problems.Add($"Could not read '{Path.GetFileName(assemblyPath)}': {ex.Message}");
            return new AssemblyInspection(assemblyPath, null, null, [], [], problems);
        }
    }
}
