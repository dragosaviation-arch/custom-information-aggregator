namespace CIA.Desktop.Presentation;

public sealed record CiaContributorIdentity(
    string Name,
    string ProfessionalIdentification,
    string DisplayUrl,
    Uri Destination);

public static class CiaProductIdentity
{
    public const string ApplicationMonogram = "C";
    public const string ApplicationShortName = "CIA";
    public const string ApplicationName = "Custom Information Aggregator";
    public const string ApplicationTagline = "FOCUS · EXTRACT · REVIEW";

    // A fork may replace CurrentDeveloper and the product branding above.
    public static CiaContributorIdentity CurrentDeveloper { get; } = new(
        "Dragos Tone",
        "Project Manager",
        "github.com/dragosaviation-arch/custom-information-aggregator",
        new Uri("https://github.com/dragosaviation-arch/custom-information-aggregator"));

    // OriginalAuthor is protected attribution. Forks should not repurpose this identity.
    public static CiaContributorIdentity OriginalAuthor { get; } = new(
        "Dragos Tone",
        "Project Manager",
        "github.com/dragosaviation-arch",
        new Uri("https://github.com/dragosaviation-arch/custom-information-aggregator"));
}
