namespace CIA.Desktop.Presentation;

public sealed record WorkspaceDefinition(
    WorkspaceArea Area,
    string Title,
    string Description);

public enum WorkspaceArea
{
    Load,
    Discover,
    Database,
    ExtractionReviewExport,
    ActivityDiagnostics,
    SettingsMaintenance
}
