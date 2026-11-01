using RimPulse.App.Services;
using RimPulse.Core.Credits;

namespace RimPulse.App.ViewModels;

/// <summary>
/// One row of the Credits panel. Each row owns the commands that open its links so the XAML needs no
/// relative-source bindings back to the window's view model.
/// </summary>
public sealed class CreditEntryViewModel
{
    private readonly ILinkLauncher _launcher;
    private readonly Action<string>? _reportStatus;

    public CreditEntryViewModel(CreditEntry entry, ILinkLauncher launcher, Action<string>? reportStatus = null)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _reportStatus = reportStatus;

        OpenPrimaryLinkCommand = new RelayCommand(() => Open(entry.Url));
        OpenRelatedLinkCommand = new RelayCommand(() => Open(entry.RelatedUrl), () => entry.HasRelatedUrl);
    }

    public CreditEntry Entry { get; }

    public string ProjectName => Entry.ProjectName;

    public string CreatorLine => Entry.CreatorLine;

    public string Summary => Entry.Summary;

    public string Relevance => Entry.Relevance;

    public string LicenseLine => Entry.License;

    public string LicenseText => $"License: {Entry.License}";

    public string UrlText => Entry.Url;

    public string UsageLabel => Entry.UsageLabel;

    public bool IsReference => Entry.Usage == CreditUsage.Reference;

    public bool IsAttributed => Entry.Usage != CreditUsage.Reference;

    public string PrimaryLinkLabel => "Open project";

    public bool HasRelatedUrl => Entry.HasRelatedUrl;

    public string RelatedLinkLabel => Entry.RelatedUrlLabel ?? "Open related link";

    public RelayCommand OpenPrimaryLinkCommand { get; }

    public RelayCommand OpenRelatedLinkCommand { get; }

    private void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var result = _launcher.Open(url);
        _reportStatus?.Invoke(result.Succeeded
            ? $"Opened {url}"
            : $"Could not open {url}: {result.Message}");
    }
}
