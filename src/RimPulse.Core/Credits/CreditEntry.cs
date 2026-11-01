namespace RimPulse.Core.Credits;

/// <summary>How RimPulse relates to a third-party project.</summary>
public enum CreditUsage
{
    /// <summary>Studied for reference only. No code, data or assets are used.</summary>
    Reference = 0,

    /// <summary>Code was adapted. The entry must state the original author and keep this visible in the UI.</summary>
    Adapted = 1,

    /// <summary>A binary, asset or dataset is shipped with RimPulse.</summary>
    Bundled = 2,

    /// <summary>Used to build or test RimPulse, but not shipped to users.</summary>
    DevelopmentDependency = 3,
}

/// <summary>
/// One third-party project referenced by RimPulse: what it is, who made it, under which license, and how it
/// relates to a RimPulse milestone.
/// </summary>
/// <remarks>
/// Descriptions and licenses were read from the projects' GitHub repositories when this catalog was written.
/// They are facts about someone else's work, so they are stored in one place and asserted by tests: every
/// entry must have a creator, a license statement and an https link.
/// </remarks>
public sealed record CreditEntry(
    string ProjectName,
    string Creator,
    string Url,
    string Summary,
    string Relevance,
    string License,
    CreditUsage Usage = CreditUsage.Reference,
    string? RelatedUrl = null,
    string? RelatedUrlLabel = null)
{
    public string CreatorLine => $"by {Creator}";

    public bool HasRelatedUrl => !string.IsNullOrWhiteSpace(RelatedUrl);

    public string UsageLabel => Usage switch
    {
        CreditUsage.Adapted => "Adapted code — attribution required",
        CreditUsage.Bundled => "Component included",
        CreditUsage.DevelopmentDependency => "Development dependency (not shipped)",
        _ => "Reference only",
    };
}
