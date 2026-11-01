namespace RimPulse.App.ViewModels;

/// <summary>
/// One entry of the saved project list. The title is the directory name until the project has been
/// inspected, then it is the name from <c>About/About.xml</c>.
/// </summary>
public sealed class ProjectRow : ObservableObject
{
    private string _title;
    private string _detail;
    private bool _isMissing;

    public ProjectRow(string path, string? title = null, string? detail = null)
    {
        Path = path;
        _title = string.IsNullOrWhiteSpace(title) ? DirectoryName(path) : title;
        _detail = string.IsNullOrWhiteSpace(detail) ? path : detail;
    }

    public string Path { get; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>Package ID and supported versions once known, otherwise the directory path.</summary>
    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    /// <summary>True when the directory is known to be gone; the row stays so the path is not silently lost.</summary>
    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (SetProperty(ref _isMissing, value))
            {
                OnPropertyChanged(nameof(MissingLabel));
            }
        }
    }

    public string MissingLabel => _isMissing ? "NOT FOUND" : string.Empty;

    private static string DirectoryName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
