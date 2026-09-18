using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CIA.Contracts.Export;
using CIA.Desktop.Export;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CIA.Desktop.Views;

public partial class WorkbookCollisionDialog : Window, INotifyPropertyChanged
{
    private readonly WorkbookCollisionResolutionRequest _request;
    private string? _validationMessage;

    public WorkbookCollisionDialog(WorkbookCollisionResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        InitializeComponent();
        _request = request;
        Rows = new ObservableCollection<WorkbookCollisionRowPresentation>(
            request.Targets.Where(target => target.Exists)
                .Select(target => new WorkbookCollisionRowPresentation(target)));
        DataContext = this;
    }

    public ObservableCollection<WorkbookCollisionRowPresentation> Rows { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WorkbookCollisionResolution? Resolution { get; private set; }

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (string.Equals(_validationMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            _validationMessage = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(ValidationMessage)));
        }
    }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        if (Rows.Any(row => row.SelectedOption.Action == WorkbookCollisionAction.Cancel))
        {
            DialogResult = false;
            return;
        }

        var decisions = Rows.Select(row => new WorkbookCollisionDecision(
            row.WorkbookDefinitionId,
            row.SelectedOption.Action,
            row.SelectedOption.Action == WorkbookCollisionAction.DifferentName
                ? row.DifferentFileName
                : null)).ToArray();
        var resultingNames = _request.ReservedFileNames.Concat(
            _request.Targets.Select(target =>
            {
                var decision = decisions.SingleOrDefault(candidate =>
                    candidate.WorkbookDefinitionId == target.WorkbookDefinitionId);
                return decision?.Action == WorkbookCollisionAction.DifferentName
                    ? decision.DifferentFileName ?? string.Empty
                    : target.FileName;
            })).ToArray();
        var invalidName = resultingNames.FirstOrDefault(name =>
            !ExportConfigurationValidator.IsValidWorkbookFileName(name));
        if (invalidName is not null)
        {
            ValidationMessage = "Every different name must be a valid .xlsx filename.";
            return;
        }

        if (resultingNames.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != resultingNames.Length)
        {
            ValidationMessage = "Workbook filenames must remain unique.";
            return;
        }

        var renamedCollision = resultingNames.FirstOrDefault(name =>
            File.Exists(Path.Combine(_request.OutputDirectory, name))
            && !_request.Targets.Any(target =>
                target.Exists
                && string.Equals(target.FileName, name, StringComparison.OrdinalIgnoreCase)
                && decisions.Single(decision =>
                    decision.WorkbookDefinitionId == target.WorkbookDefinitionId).Action
                    == WorkbookCollisionAction.Overwrite));
        if (renamedCollision is not null)
        {
            ValidationMessage = $"'{renamedCollision}' also exists. Choose another filename.";
            return;
        }

        ValidationMessage = null;
        Resolution = new WorkbookCollisionResolution(decisions);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

public sealed record WorkbookCollisionActionOption(
    WorkbookCollisionAction Action,
    string Label);

public sealed class WorkbookCollisionRowPresentation : ObservableObject
{
    private WorkbookCollisionActionOption _selectedOption;
    private string _differentFileName;

    internal WorkbookCollisionRowPresentation(WorkbookCollisionTarget target)
    {
        WorkbookDefinitionId = target.WorkbookDefinitionId;
        OriginalFileName = target.FileName;
        FinalPath = target.FinalPath;
        Options =
        [
            new WorkbookCollisionActionOption(WorkbookCollisionAction.Cancel, "Cancel"),
            new WorkbookCollisionActionOption(WorkbookCollisionAction.Overwrite, "Overwrite"),
            new WorkbookCollisionActionOption(WorkbookCollisionAction.DifferentName, "Different name")
        ];
        _selectedOption = Options[0];
        _differentFileName = CreateDifferentName(target.FileName);
    }

    public WorkbookDefinitionId WorkbookDefinitionId { get; }

    public string OriginalFileName { get; }

    public string FinalPath { get; }

    public IReadOnlyList<WorkbookCollisionActionOption> Options { get; }

    public WorkbookCollisionActionOption SelectedOption
    {
        get => _selectedOption;
        set => SetProperty(ref _selectedOption, value);
    }

    public string DifferentFileName
    {
        get => _differentFileName;
        set => SetProperty(ref _differentFileName, value ?? string.Empty);
    }

    private static string CreateDifferentName(string fileName) =>
        $"{Path.GetFileNameWithoutExtension(fileName)} (2).xlsx";
}
