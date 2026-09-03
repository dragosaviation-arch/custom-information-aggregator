using CIA.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CIA.Desktop.Presentation;

public sealed class MainWindowViewModel : ObservableObject
{
    private WorkspaceDefinition _selectedWorkspace;

    public MainWindowViewModel(ApplicationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Session = session;
        Workspaces = Array.AsReadOnly<WorkspaceDefinition>(
        [
            new(WorkspaceArea.Load, "Load", "Source loading and active-session source selection."),
            new(WorkspaceArea.Discover, "Discover", "Information discovery and mapping."),
            new(WorkspaceArea.Database, "Database", "Structured results and filtering."),
            new(WorkspaceArea.ExtractionReviewExport, "Extraction / Review / Export", "Extraction, result review, and export."),
            new(WorkspaceArea.ActivityDiagnostics, "Activity / Diagnostics", "Operation activity and diagnostic information."),
            new(WorkspaceArea.SettingsMaintenance, "Settings / Maintenance", "Application settings and managed-data maintenance.")
        ]);
        _selectedWorkspace = Workspaces[0];
    }

    public ApplicationSession Session { get; }

    public IReadOnlyList<WorkspaceDefinition> Workspaces { get; }

    public WorkspaceDefinition SelectedWorkspace
    {
        get => _selectedWorkspace;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!Workspaces.Contains(value))
            {
                throw new ArgumentException("The selected workspace is not part of this shell.", nameof(value));
            }

            SetProperty(ref _selectedWorkspace, value);
        }
    }
}
