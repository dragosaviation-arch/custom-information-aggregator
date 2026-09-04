namespace CIA.Desktop.Presentation;

public sealed record WorkspaceDefinition(
    WorkspaceArea Area,
    string IconGlyph,
    string Title,
    string Description);

public enum WorkspaceArea
{
    Load,
    Discovery,
    Database,
    Settings
}
