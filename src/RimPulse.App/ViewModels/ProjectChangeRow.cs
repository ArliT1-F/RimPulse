using RimPulse.Core.Monitoring;
using RimPulse.Core.Validation;

namespace RimPulse.App.ViewModels;

/// <summary>A detected file change for display; it does not imply a build or reload.</summary>
public sealed class ProjectChangeRow(ProjectChange change, XmlCheckResult? xmlCheck)
{
    public string Time => change.DetectedAt.ToString("HH:mm:ss");
    public string Path => change.RelativePath;
    public string Category => change.Category switch
    {
        ProjectChangeCategory.CSharp => "C# source",
        ProjectChangeCategory.AboutMetadata => "About.xml",
        ProjectChangeCategory.Language => "Language",
        ProjectChangeCategory.Xml => "XML",
        ProjectChangeCategory.Texture => "Texture",
        ProjectChangeCategory.Audio => "Audio",
        _ => "Other",
    };
    public string Change => change.Changes.ToString();
    public string Validation => xmlCheck?.Message ?? "Not checked";
}
