using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Export;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Core.Database;
using CIA.Core.Runtime;
using CIA.Desktop.Database;
using CIA.Desktop.Discovery;
using CIA.Desktop.Export;
using CIA.Desktop.Extraction;
using CIA.Desktop.Workflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CIA.Desktop.Presentation;

public sealed class DatabaseWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly ActiveDiscoveryConfiguration _discoveryConfiguration;
    private readonly IApplicationWorkflowCoordinator _workflowCoordinator;
    private readonly DatabaseBuildCoordinator? _databaseBuildCoordinator;
    private readonly IDatabaseReviewClient? _databaseReviewClient;
    private readonly ExtractionCoordinator? _extractionCoordinator;
    private readonly WorkbookExportCoordinator? _workbookExportCoordinator;
    private readonly IExportFolderPicker? _exportFolderPicker;
    private readonly IWorkbookCollisionResolver? _workbookCollisionResolver;
    private readonly ApplicationSettingsService? _settingsService;
    private readonly PostExportBehaviorCoordinator? _postExportBehaviorCoordinator;
    private readonly GlobalOperationProgress? _globalProgress;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private readonly ExportRoutingConfigurationPresentation _exportRouting = new();
    private readonly Dictionary<string, DatabaseColumnPresentation> _columnCache = new(
        StringComparer.Ordinal);
    private readonly List<string> _publishedColumnOrder = [];
    private readonly ObservableCollection<DatabaseColumnPresentation> _columns = [];
    private readonly ObservableCollection<ExportFieldPresentation> _exportColumns = [];
    private readonly ObservableCollection<ExportMetadataFieldPresentation> _exportMetadataFields = [];
    private readonly ObservableCollection<DatabaseColumnPresentation> _visibleColumns = [];
    private readonly VirtualizedDatabaseReviewCollection _records;
    private readonly ObservableCollection<DatabaseDatasetPresentation> _datasets = [];
    private readonly ObservableCollection<DatabaseMetadataFieldPresentation> _metadataFields = [];
    private readonly ObservableCollection<DatabaseMetadataFieldPresentation> _visibleMetadataFields = [];
    private readonly ObservableCollection<DatabaseColumnChoicePresentation> _columnChoices = [];
    private WorkflowArtifactStatus _databaseStatus;
    private OperationId? _publishedGenerationId;
    private CancellationTokenSource? _reviewCancellation;
    private readonly SemaphoreSlim _reviewLoadGate = new(1, 1);
    private readonly HashSet<int> _loadingReviewPages = [];
    private bool _isReviewLoading;
    private string? _reviewFailureDescription;
    private int _firstVisibleReviewOrdinal = 1;
    private int _visibleReviewRowCount = DatabaseReviewLimits.MaximumRowsPerPage;
    private int _publishedValueCount;
    private DatabaseGenerationSummary? _publishedGeneration;
    private DatabaseDatasetPresentation? _selectedDataset;
    private string? _searchText;
    private DatabaseRowInclusionFilter _rowInclusionFilter;
    private DatabaseMetadataVisibilityMode _metadataVisibilityMode = DatabaseMetadataVisibilityMode.None;
    private bool _synchronizingMetadataVisibility;
    private bool _synchronizingDatabaseColumnState;
    private WorkflowArtifactStatus _extractionStatus;
    private WorkflowOperationStatus? _latestExtractionAttempt;
    private string _outputFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "CIA",
        "Exports");
    private bool _alwaysAskWhereToExport = true;
    private string _workbookExportStatusText = "Choose an output folder to export the prepared workbook batch.";
    private int _disposed;

    public DatabaseWorkspaceViewModel(
        ActiveDiscoveryConfiguration discoveryConfiguration,
        IApplicationWorkflowCoordinator workflowCoordinator,
        DatabaseBuildCoordinator? databaseBuildCoordinator = null,
        IDatabaseReviewClient? databaseReviewClient = null,
        ExtractionCoordinator? extractionCoordinator = null,
        WorkbookExportCoordinator? workbookExportCoordinator = null,
        IExportFolderPicker? exportFolderPicker = null,
        IWorkbookCollisionResolver? workbookCollisionResolver = null,
        ApplicationSettingsService? settingsService = null,
        PostExportBehaviorCoordinator? postExportBehaviorCoordinator = null,
        GlobalOperationProgress? globalProgress = null)
    {
        ArgumentNullException.ThrowIfNull(discoveryConfiguration);
        ArgumentNullException.ThrowIfNull(workflowCoordinator);

        _discoveryConfiguration = discoveryConfiguration;
        _workflowCoordinator = workflowCoordinator;
        _databaseBuildCoordinator = databaseBuildCoordinator;
        _databaseReviewClient = databaseReviewClient;
        _extractionCoordinator = extractionCoordinator;
        _workbookExportCoordinator = workbookExportCoordinator;
        _exportFolderPicker = exportFolderPicker;
        _workbookCollisionResolver = workbookCollisionResolver;
        _settingsService = settingsService;
        _postExportBehaviorCoordinator = postExportBehaviorCoordinator;
        _globalProgress = globalProgress;
        if (settingsService?.Startup.Settings.LastUsedOutputDirectory is { } rememberedOutput
            && Directory.Exists(rememberedOutput))
        {
            _outputFolder = rememberedOutput;
        }
        _uiSynchronizationContext = SynchronizationContext.Current;
        Columns = new ReadOnlyObservableCollection<DatabaseColumnPresentation>(_columns);
        ExportColumns = new ReadOnlyObservableCollection<ExportFieldPresentation>(
            _exportColumns);
        ExportMetadataFields = new ReadOnlyObservableCollection<ExportMetadataFieldPresentation>(
            _exportMetadataFields);
        WorkbookDefinitions = _exportRouting.Workbooks;
        _exportRouting.Changed += OnExportRoutingChanged;
        _records = new VirtualizedDatabaseReviewCollection(CreateReviewRowPresentation);
        VisibleColumns = new ReadOnlyObservableCollection<DatabaseColumnPresentation>(
            _visibleColumns);
        Records = _records;
        Datasets = new ReadOnlyObservableCollection<DatabaseDatasetPresentation>(_datasets);
        MetadataFields = new ReadOnlyObservableCollection<DatabaseMetadataFieldPresentation>(
            _metadataFields);
        VisibleMetadataColumns = new ReadOnlyObservableCollection<DatabaseMetadataFieldPresentation>(
            _visibleMetadataFields);
        ColumnChoices = new ReadOnlyObservableCollection<DatabaseColumnChoicePresentation>(
            _columnChoices);
        foreach (var field in Enum.GetValues<DatabaseMetadataField>())
        {
            var presentation = new DatabaseMetadataFieldPresentation(
                field,
                GetMetadataFieldDisplayName(field),
                isVisible: false);
            RestoreMetadataColumnWidth(presentation);
            presentation.PropertyChanged += OnMetadataFieldPropertyChanged;
            _metadataFields.Add(presentation);
        }
        RebuildVisibleMetadataColumns();
        MoveColumnUpCommand = new RelayCommand<DatabaseColumnPresentation>(
            MoveColumnUp,
            CanMoveColumnUp);
        MoveColumnDownCommand = new RelayCommand<DatabaseColumnPresentation>(
            MoveColumnDown,
            CanMoveColumnDown);
        CreateExportWorkbookCommand = new RelayCommand(CreateExportWorkbook, HasSelectedDataset);
        MoveWorksheetUpCommand = new RelayCommand(
            () => MoveWorksheet(-1),
            () => CanMoveWorksheet(-1));
        MoveWorksheetDownCommand = new RelayCommand(
            () => MoveWorksheet(1),
            () => CanMoveWorksheet(1));
        ResetColumnLayoutCommand = new RelayCommand(ResetColumnLayout, HasColumns);
        ResetHeadersCommand = new RelayCommand(ResetHeaders, HasSelectedDataset);
        PrepareForExportCommand = new AsyncRelayCommand(
            PrepareForExportAsync,
            CanPrepareForExport);
        BrowseOutputFolderCommand = new RelayCommand(
            BrowseOutputFolder,
            () => _exportFolderPicker is not null);
        ExportToExcelCommand = new AsyncRelayCommand(
            ExportToExcelAsync,
            CanRunExportFlow);
        SetRowIncludedCommand = new AsyncRelayCommand<DatabaseReviewRowPresentation>(
            SetRowIncludedAsync,
            CanSetRowIncluded);
        IncludeVisibleRowsCommand = new AsyncRelayCommand(
            () => SetVisibleRowsIncludedAsync(true),
            CanIncludeVisibleRows);
        ExcludeVisibleRowsCommand = new AsyncRelayCommand(
            () => SetVisibleRowsIncludedAsync(false),
            CanExcludeVisibleRows);

        _databaseStatus = workflowCoordinator.Current.Database;
        _extractionStatus = workflowCoordinator.Current.Extraction;
        CaptureLatestExtractionAttempt(workflowCoordinator.Current);
        _workflowCoordinator.StateChanged += OnWorkflowStateChanged;
        if (_databaseBuildCoordinator is not null)
        {
            _databaseBuildCoordinator.PublishedGenerationChanged +=
                OnPublishedGenerationChanged;

            if (_databaseBuildCoordinator.CurrentGeneration is { } generation)
            {
                ApplyPublishedGeneration(generation);
            }
        }

        if (_extractionCoordinator is not null)
        {
            _extractionCoordinator.PublishedResultChanged += OnPublishedExtractionChanged;
        }

        SynchronizeExportReadinessContext();
    }

    public ReadOnlyObservableCollection<DatabaseColumnPresentation> Columns { get; }

    public ReadOnlyObservableCollection<ExportFieldPresentation> ExportColumns { get; }

    public ReadOnlyObservableCollection<ExportMetadataFieldPresentation> ExportMetadataFields { get; }

    public ReadOnlyObservableCollection<ExportWorkbookPresentation> WorkbookDefinitions { get; }

    public ReadOnlyObservableCollection<DatabaseColumnPresentation> VisibleColumns { get; }

    public IReadOnlyList<DatabaseReviewRowPresentation> Records { get; }

    public ReadOnlyObservableCollection<DatabaseDatasetPresentation> Datasets { get; }

    public ReadOnlyObservableCollection<DatabaseMetadataFieldPresentation> MetadataFields { get; }

    public ReadOnlyObservableCollection<DatabaseMetadataFieldPresentation> VisibleMetadataColumns { get; }

    public ReadOnlyObservableCollection<DatabaseColumnChoicePresentation> ColumnChoices { get; }

    public IRelayCommand<DatabaseColumnPresentation> MoveColumnUpCommand { get; }

    public IRelayCommand<DatabaseColumnPresentation> MoveColumnDownCommand { get; }

    public IRelayCommand CreateExportWorkbookCommand { get; }

    public IRelayCommand MoveWorksheetUpCommand { get; }

    public IRelayCommand MoveWorksheetDownCommand { get; }

    public IRelayCommand ResetColumnLayoutCommand { get; }

    public IRelayCommand ResetHeadersCommand { get; }

    public IAsyncRelayCommand PrepareForExportCommand { get; }

    public IRelayCommand BrowseOutputFolderCommand { get; }

    public IAsyncRelayCommand ExportToExcelCommand { get; }

    public IAsyncRelayCommand<DatabaseReviewRowPresentation> SetRowIncludedCommand { get; }

    public IAsyncRelayCommand IncludeVisibleRowsCommand { get; }

    public IAsyncRelayCommand ExcludeVisibleRowsCommand { get; }

    public DatabaseDatasetPresentation? SelectedDataset
    {
        get => _selectedDataset;
        set
        {
            if (ReferenceEquals(_selectedDataset, value))
            {
                return;
            }

            SynchronizeSelectedDatabaseColumnState();
            if (!SetProperty(ref _selectedDataset, value) || value is null || _publishedGeneration is null)
            {
                return;
            }
            ApplyDataset(value.Summary);
            ApplyExportDataset(value.Summary);
            OnPropertyChanged(nameof(RecordCountText));
            _ = BeginReviewSessionAsync(_publishedGeneration.OperationId);
        }
    }

    public string? SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value) && _publishedGenerationId is { } generationId
                && SelectedDataset is not null)
            {
                _ = BeginReviewSessionAsync(generationId);
            }
        }
    }

    public DatabaseRowInclusionFilter RowInclusionFilter
    {
        get => _rowInclusionFilter;
        set
        {
            if (SetProperty(ref _rowInclusionFilter, value) && _publishedGenerationId is { } generationId
                && SelectedDataset is not null)
            {
                _ = BeginReviewSessionAsync(generationId);
            }
        }
    }

    public IReadOnlyList<DatabaseRowInclusionFilter> RowInclusionFilters { get; } =
        Enum.GetValues<DatabaseRowInclusionFilter>();

    public DatabaseMetadataVisibilityMode MetadataVisibilityMode
    {
        get => _metadataVisibilityMode;
        set
        {
            if (SetProperty(ref _metadataVisibilityMode, value))
            {
                if (value != DatabaseMetadataVisibilityMode.Custom)
                {
                    _synchronizingMetadataVisibility = true;
                    foreach (var metadataField in _metadataFields)
                    {
                        metadataField.IsVisible = value switch
                        {
                            DatabaseMetadataVisibilityMode.None => false,
                            DatabaseMetadataVisibilityMode.Useful => IsUsefulMetadataField(metadataField.Field),
                            DatabaseMetadataVisibilityMode.All => true,
                            _ => metadataField.IsVisible
                        };
                    }
                    _synchronizingMetadataVisibility = false;
                }
                RebuildVisibleMetadataColumns();
                OnPropertyChanged(nameof(VisibleMetadataFields));
                SynchronizeSelectedDatabaseColumnState();
                RefreshOutputOverrideFields();
            }
        }
    }

    public IReadOnlyList<DatabaseMetadataVisibilityMode> MetadataVisibilityModes { get; } =
        Enum.GetValues<DatabaseMetadataVisibilityMode>();

    public IReadOnlyList<DatabaseMetadataField> VisibleMetadataFields =>
        _visibleMetadataFields.Select(metadataField => metadataField.Field).ToArray();

    public WorkflowArtifactStatus DatabaseStatus
    {
        get => _databaseStatus;
        private set
        {
            if (SetProperty(ref _databaseStatus, value))
            {
                OnPropertyChanged(nameof(DatabaseStateText));
                OnPropertyChanged(nameof(DatabaseStateContext));
                OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(EmptyStateDetail));
            }
        }
    }

    public string DatabaseStateText => DatabaseStatus switch
    {
        WorkflowArtifactStatus.Current => "Database current",
        WorkflowArtifactStatus.Stale => "Database out of date",
        _ => "Database not available"
    };

    public string DatabaseStateContext => DatabaseStatus switch
    {
        WorkflowArtifactStatus.Current =>
            "Discovery configuration aligned · last published Database remains authoritative.",
        WorkflowArtifactStatus.Stale =>
            "Discovery or its configuration changed · the last published Database is out of date.",
        _ => "No Database has been published for the current session."
    };

    public string EmptyStateTitle => DatabaseStatus switch
    {
        _ when IsReviewLoading => "Loading Database review",
        _ when _reviewFailureDescription is not null => "Database review unavailable",
        WorkflowArtifactStatus.Stale => "Database is out of date",
        WorkflowArtifactStatus.Current => "Database contains no reviewable values",
        _ => "Database not available"
    };

    public string EmptyStateDetail => DatabaseStatus switch
    {
        _ when IsReviewLoading =>
            "Reading the first bounded chunk from the active published Database.",
        _ when _reviewFailureDescription is not null => _reviewFailureDescription,
        WorkflowArtifactStatus.Stale =>
            "The retained published Database remains available for review but is out of date.",
        WorkflowArtifactStatus.Current =>
            "The active published Database has no reviewable values.",
        _ => "Database records will appear after a later Database creation/update workflow."
    };

    public string RecordCountText => SelectedDataset is { } dataset
        ? $"{dataset.Summary.RowCount:N0} rows · {dataset.Summary.ValueCount:N0} values"
        : $"{_publishedValueCount:N0} mapped values";

    public bool HasReviewRows => Records.Count > 0;

    public bool IsReviewLoading
    {
        get => _isReviewLoading;
        private set
        {
            if (SetProperty(ref _isReviewLoading, value))
            {
                OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(EmptyStateDetail));
            }
        }
    }

    public string ColumnCountText =>
        $"{VisibleColumns.Count + VisibleMetadataColumns.Count} / {Columns.Count + MetadataFields.Count} columns";

    public string ExportFieldCountText =>
        $"{ExportColumns.Count + ExportMetadataFields.Count} included";

    public string WorkbookExportFieldCountText =>
        SelectedDataset is null
            ? "0 selected"
            : $"{CaptureExportConfiguration().CreateIncludedOutputColumns(SelectedDataset.Summary.SourceSetId).Count} selected";

    public bool IsSelectedSetExportEnabled
    {
        get => SelectedExportSet?.IsEnabled == true;
        set
        {
            if (SelectedExportSet is not { } set || set.IsEnabled == value)
            {
                return;
            }

            set.IsEnabled = value;
            NotifyExportRoutingPropertiesChanged();
        }
    }

    public ExportWorkbookPresentation? SelectedExportWorkbook
    {
        get => SelectedExportSet is { } set
            ? WorkbookDefinitions.SingleOrDefault(workbook =>
                workbook.WorkbookDefinitionId == set.WorkbookDefinitionId)
            : null;
        set
        {
            if (SelectedExportSet is not { } set || value is null
                || set.WorkbookDefinitionId == value.WorkbookDefinitionId)
            {
                return;
            }

            _exportRouting.AssignWorkbook(set.SourceSetId, value.WorkbookDefinitionId);
            NotifyExportRoutingPropertiesChanged();
        }
    }

    public string SelectedWorksheetName
    {
        get => SelectedExportSet?.WorksheetName ?? string.Empty;
        set
        {
            if (SelectedExportSet is not { } set
                || string.Equals(set.WorksheetName, value, StringComparison.Ordinal))
            {
                return;
            }

            set.WorksheetName = value;
            NotifyExportRoutingPropertiesChanged();
        }
    }

    public string WorksheetOrderText => SelectedExportSet is { } set
        ? $"Worksheet {set.WorksheetOrder:N0}"
        : "No worksheet";

    public bool IsExportConfigurationValid => CurrentExportValidation.IsValid;

    public string ExportConfigurationValidationText
    {
        get
        {
            var validation = CurrentExportValidation;
            if (!validation.IsValid)
            {
                return validation.Failures[0].Description;
            }

            return validation.RunnableWorkbooks.Count == 0
                ? "No Source Sets are enabled for export."
                : $"Ready for SPR-141 routing · {validation.RunnableWorkbooks.Count:N0} workbook definition(s).";
        }
    }

    public bool IsExportAvailable
    {
        get
        {
            var readiness = _workbookExportCoordinator?.EvaluateReadiness();
            return readiness is { NormalOperationReady: true }
                or { AdditionalUserInputRequired: true };
        }
    }

    public ExtractionReviewState ExtractionReviewState
    {
        get
        {
            var workflow = _workflowCoordinator.Current;
            if (workflow.ActiveOperation?.Kind == WorkflowOperationKind.Extraction)
            {
                return ExtractionReviewState.Preparing;
            }

            var result = _extractionCoordinator?.CurrentResult;
            var latestAttempt = _latestExtractionAttempt;
            if (result is not null
                && (_extractionStatus == WorkflowArtifactStatus.Stale
                    || !ExtractionBasisMatchesReviewedDatabase))
            {
                return ExtractionReviewState.OutOfDate;
            }

            if (latestAttempt is not null
                && result?.OperationId != latestAttempt.Correlation.OperationId)
            {
                var unsuccessfulState = ToUnsuccessfulReviewState(latestAttempt.State);
                if (unsuccessfulState is not null)
                {
                    return unsuccessfulState.Value;
                }
            }

            if (_extractionStatus == WorkflowArtifactStatus.Current
                && result is not null
                && ExtractionBasisMatchesReviewedDatabase)
            {
                return _extractionCoordinator?.CurrentCompletion?.Outcome
                    == OperationOutcome.CompletedWithIssues
                    ? ExtractionReviewState.ReadyWithIssues
                    : ExtractionReviewState.Ready;
            }

            if (latestAttempt is not null)
            {
                var unsuccessfulState = ToUnsuccessfulReviewState(latestAttempt.State);
                if (unsuccessfulState is not null)
                {
                    return unsuccessfulState.Value;
                }
            }

            return ExtractionReviewState.NotPrepared;
        }
    }

    public string ExtractionReviewStateText => ExtractionReviewState switch
    {
        ExtractionReviewState.NotPrepared => "Not prepared",
        ExtractionReviewState.Preparing => "Preparing",
        ExtractionReviewState.Ready => "Ready",
        ExtractionReviewState.ReadyWithIssues => "Ready with issues",
        ExtractionReviewState.OutOfDate => "Out of date",
        ExtractionReviewState.Failed => "Failed",
        ExtractionReviewState.Cancelled => "Cancelled",
        ExtractionReviewState.Interrupted => "Interrupted",
        _ => throw new InvalidOperationException("Unknown Extraction review state.")
    };

    public string ExtractionReviewContext
    {
        get
        {
            var result = _extractionCoordinator?.CurrentResult;
            return ExtractionReviewState switch
            {
                ExtractionReviewState.NotPrepared =>
                    "Prepare the current Database before a later Excel export.",
                ExtractionReviewState.Preparing when
                    _workflowCoordinator.Current.LatestOperation?.State
                        == WorkflowOperationState.Cancelling =>
                    "Cancellation requested; no incomplete Extraction Result will be published.",
                ExtractionReviewState.Preparing when result is not null =>
                    "Preparing a replacement; the previous valid result remains retained.",
                ExtractionReviewState.Preparing =>
                    "Preparing an atomic Extraction Result from the current Database.",
                ExtractionReviewState.Ready =>
                    $"Prepared from the currently reviewed Database generation · {result?.DatabaseGeneration.ValueCount ?? 0:N0} values.",
                ExtractionReviewState.ReadyWithIssues => CreateCompletionContext(
                    _extractionCoordinator?.CurrentCompletion,
                    result?.DatabaseGeneration.ValueCount),
                ExtractionReviewState.OutOfDate =>
                    "The retained Extraction Result does not match the current Database generation.",
                ExtractionReviewState.Failed => CreateUnsuccessfulContext(
                    "The latest preparation failed",
                    result),
                ExtractionReviewState.Cancelled => CreateUnsuccessfulContext(
                    "The latest preparation was cancelled",
                    result),
                ExtractionReviewState.Interrupted => CreateUnsuccessfulContext(
                    "The latest preparation was interrupted",
                    result),
                _ => throw new InvalidOperationException("Unknown Extraction review state.")
            };
        }
    }

    public bool ExtractionBasisMatchesReviewedDatabase =>
        _publishedGenerationId is { } publishedGenerationId
        && _databaseBuildCoordinator?.CurrentGeneration is { } currentGeneration
        && currentGeneration.OperationId == publishedGenerationId
        && _extractionCoordinator?.CurrentResult is { } extractionResult
        && DatabaseGenerationSnapshotComparer.AreEquivalent(
            currentGeneration,
            extractionResult.DatabaseGeneration);

    public ExportConfigurationSnapshot CaptureExportConfiguration()
    {
        return _exportRouting.Capture();
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set
        {
            if (SetProperty(ref _outputFolder, value ?? string.Empty))
            {
                NotifyExportReadinessChanged();
            }
        }
    }

    public bool AlwaysAskWhereToExport
    {
        get => _alwaysAskWhereToExport;
        set => SetProperty(ref _alwaysAskWhereToExport, value);
    }

    public string WorkbookExportStatusText
    {
        get => _workbookExportStatusText;
        private set => SetProperty(ref _workbookExportStatusText, value);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _workflowCoordinator.StateChanged -= OnWorkflowStateChanged;
        _exportRouting.Changed -= OnExportRoutingChanged;
        if (_databaseBuildCoordinator is not null)
        {
            _databaseBuildCoordinator.PublishedGenerationChanged -=
                OnPublishedGenerationChanged;
        }

        if (_extractionCoordinator is not null)
        {
            _extractionCoordinator.PublishedResultChanged -= OnPublishedExtractionChanged;
        }

        var reviewCancellation = Interlocked.Exchange(ref _reviewCancellation, null);
        reviewCancellation?.Cancel();
        reviewCancellation?.Dispose();
        foreach (var column in _columnCache.Values)
        {
            column.PropertyChanged -= OnColumnPropertyChanged;
        }
    }

    private bool HasColumns()
    {
        return Columns.Count > 0;
    }

    private bool HasSelectedDataset() => SelectedDataset is not null;

    private ExportSourceSetPresentation? SelectedExportSet => SelectedDataset is null
        ? null
        : _exportRouting.SourceSets.SingleOrDefault(set =>
            set.SourceSetId == SelectedDataset.Summary.SourceSetId);

    private ExportConfigurationValidationResult CurrentExportValidation =>
        _publishedGeneration is { IsHierarchyAware: true } generation
            ? ExportConfigurationValidator.Validate(CaptureExportConfiguration(), generation)
            : new ExportConfigurationValidationResult(
                [new ExportConfigurationValidationFailure(
                    "missing-hierarchy-database",
                    "A hierarchy-aware Database is required before export can be configured.")],
                []);

    private bool CanMoveColumnUp(DatabaseColumnPresentation? column)
    {
        return column is not null && _columns.IndexOf(column) > 0;
    }

    private bool CanMoveColumnDown(DatabaseColumnPresentation? column)
    {
        return column is not null
            && _columns.IndexOf(column) is var index
            && index >= 0
            && index < _columns.Count - 1;
    }

    private void MoveColumnUp(DatabaseColumnPresentation? column)
    {
        if (column is not null)
        {
            MoveColumn(column, -1);
        }
    }

    private void MoveColumnDown(DatabaseColumnPresentation? column)
    {
        if (column is not null)
        {
            MoveColumn(column, 1);
        }
    }

    private void MoveColumn(DatabaseColumnPresentation column, int offset)
    {
        var currentIndex = _columns.IndexOf(column);
        var nextIndex = currentIndex + offset;
        if (currentIndex < 0 || nextIndex < 0 || nextIndex >= _columns.Count)
        {
            return;
        }

        _columns.Move(currentIndex, nextIndex);
        UpdatePositionsAndPresentation();
        SynchronizeSelectedDatabaseColumnState();
        RefreshOutputOverrideFields();
    }

    private void ResetColumnLayout()
    {
        foreach (var column in _columns)
        {
            column.ResetPresentation();
        }
        foreach (var choice in _columnChoices.Where(choice => choice.IsGenerated).ToArray())
        {
            choice.IsIncluded = true;
        }
        foreach (var metadataColumn in _metadataFields)
        {
            metadataColumn.ResetWidth();
        }

        ReorderColumns(_publishedColumnOrder);
        UpdatePositionsAndPresentation();
        PersistDatabaseColumnWidths(_columns);
        PersistMetadataColumnWidths(_metadataFields);
        RebuildColumnChoices();
        SynchronizeSelectedDatabaseColumnState();
        RefreshOutputOverrideFields();
    }

    private void ResetHeaders()
    {
        SelectedExportSet?.ResetHeaders();
    }

    private void CreateExportWorkbook()
    {
        if (SelectedExportSet is { } set)
        {
            _exportRouting.CreateWorkbookFor(set.SourceSetId);
        }
    }

    private bool CanMoveWorksheet(int offset)
    {
        if (SelectedExportSet is not { } set)
        {
            return false;
        }

        var workbookSets = _exportRouting.SourceSets
            .Where(candidate => candidate.WorkbookDefinitionId == set.WorkbookDefinitionId)
            .OrderBy(candidate => candidate.WorksheetOrder)
            .ToArray();
        var index = Array.IndexOf(workbookSets, set);
        return index >= 0 && index + offset >= 0 && index + offset < workbookSets.Length;
    }

    private void MoveWorksheet(int offset)
    {
        if (SelectedExportSet is { } set)
        {
            _exportRouting.MoveWorksheet(set.SourceSetId, offset);
        }
    }

    private IReadOnlyList<DatabaseColumnMapping> CapturePublishedColumns()
    {
        var current = _discoveryConfiguration.Current;
        return current.SourceSets.SelectMany(set => DatabaseTagMapper.CreateFieldMappings(
                set.SourceSetId,
                current.Items,
                _discoveryConfiguration.DatabaseTagOverridesByIdentity))
            .GroupBy(mapping => mapping.EffectiveName, StringComparer.Ordinal)
            .Select(group => new DatabaseColumnMapping(
                group.Key,
                group.SelectMany(mapping => mapping.DetailedIdentities)
                    .Select(identity => identity.InformationType)
                    .Distinct(StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    private void PublishDatabaseGeneration(
        IReadOnlyList<DatabaseColumnMapping> publishedColumns)
    {
        var publishedIds = publishedColumns
            .Select(column => CreateColumnIdentity(column.SourceInformationTypes))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var publishedColumn in publishedColumns)
        {
            var columnIdentity = CreateColumnIdentity(
                publishedColumn.SourceInformationTypes);
            if (!_columnCache.TryGetValue(columnIdentity, out var column))
            {
                column = new DatabaseColumnPresentation(
                    columnIdentity,
                    publishedColumn);
                RestoreDatabaseColumnWidth(column);
                column.PropertyChanged += OnColumnPropertyChanged;
                _columnCache.Add(columnIdentity, column);
            }
            else
            {
                column.UpdateMapping(publishedColumn);
            }
        }

        foreach (var removedIdentity in _columnCache.Keys
                     .Where(identity => !publishedIds.Contains(identity))
                     .ToArray())
        {
            _columnCache[removedIdentity].PropertyChanged -= OnColumnPropertyChanged;
            _columnCache.Remove(removedIdentity);
        }

        var retainedPresentationOrder = _columns
            .Where(column => publishedIds.Contains(column.MappingIdentity))
            .Select(column => column.MappingIdentity)
            .ToList();
        retainedPresentationOrder.AddRange(publishedColumns
            .Select(column => CreateColumnIdentity(column.SourceInformationTypes))
            .Where(identity => !retainedPresentationOrder.Contains(
                identity,
                StringComparer.Ordinal)));
        _publishedColumnOrder.Clear();
        _publishedColumnOrder.AddRange(
            publishedColumns.Select(column => CreateColumnIdentity(
                column.SourceInformationTypes)));
        ReorderColumns(retainedPresentationOrder);
        UpdatePositionsAndPresentation();
    }

    private void ReorderColumns(IEnumerable<string> orderedColumnIdentities)
    {
        _columns.Clear();
        foreach (var columnIdentity in orderedColumnIdentities)
        {
            if (_columnCache.TryGetValue(columnIdentity, out var column))
            {
                _columns.Add(column);
            }
        }
    }

    private void UpdatePositionsAndPresentation()
    {
        for (var index = 0; index < _columns.Count; index++)
        {
            _columns[index].SetPosition(index + 1);
        }

        RebuildVisibleColumns();
        NotifyColumnSummariesChanged();
        MoveColumnUpCommand.NotifyCanExecuteChanged();
        MoveColumnDownCommand.NotifyCanExecuteChanged();
        ResetColumnLayoutCommand.NotifyCanExecuteChanged();
        ResetHeadersCommand.NotifyCanExecuteChanged();
    }

    private void RebuildVisibleColumns()
    {
        _visibleColumns.Clear();
        foreach (var column in _columns.Where(column => column.IsVisible))
        {
            _visibleColumns.Add(column);
        }

        UpdateResizeBoundaries();
        RebuildReviewRows();
        OnPropertyChanged(nameof(ColumnCountText));
    }

    private void NotifyColumnSummariesChanged()
    {
        OnPropertyChanged(nameof(ColumnCountText));
        OnPropertyChanged(nameof(ExportFieldCountText));
        OnPropertyChanged(nameof(WorkbookExportFieldCountText));
    }

    private void NotifyExportConfigurationChanged()
    {
        OnPropertyChanged(nameof(ExportFieldCountText));
        OnPropertyChanged(nameof(WorkbookExportFieldCountText));
        OnPropertyChanged(nameof(IsExportConfigurationValid));
        OnPropertyChanged(nameof(ExportConfigurationValidationText));
        NotifyExportReadinessChanged();
    }

    private void OnExportRoutingChanged(object? sender, EventArgs e)
    {
        DispatchToUi(() =>
        {
            if (_synchronizingDatabaseColumnState)
            {
                NotifyExportRoutingPropertiesChanged();
                return;
            }

            if (SelectedDataset is { } selectedDataset)
            {
                ApplyExportDataset(selectedDataset.Summary);
            }
            else
            {
                NotifyExportRoutingPropertiesChanged();
            }
        });
    }

    private void NotifyExportRoutingPropertiesChanged()
    {
        NotifyExportConfigurationChanged();
        OnPropertyChanged(nameof(IsSelectedSetExportEnabled));
        OnPropertyChanged(nameof(SelectedExportWorkbook));
        OnPropertyChanged(nameof(SelectedWorksheetName));
        OnPropertyChanged(nameof(WorksheetOrderText));
        CreateExportWorkbookCommand.NotifyCanExecuteChanged();
        MoveWorksheetUpCommand.NotifyCanExecuteChanged();
        MoveWorksheetDownCommand.NotifyCanExecuteChanged();
        ResetHeadersCommand.NotifyCanExecuteChanged();
    }

    private void OnColumnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DatabaseColumnPresentation.IsVisible))
        {
            RebuildVisibleColumns();
            SynchronizeSelectedDatabaseColumnState();
            RefreshOutputOverrideFields();
        }

    }

    internal DatabaseColumnWidthFeedback? ResizeAdjacentDatabaseColumns(
        object leftColumn,
        double horizontalChange)
    {
        ArgumentNullException.ThrowIfNull(leftColumn);

        var columns = GetVisibleResizableColumns();
        var leftIndex = Array.FindIndex(columns, column => ReferenceEquals(column, leftColumn));
        if (leftIndex < 0 || leftIndex >= columns.Length - 1)
        {
            return null;
        }

        var rightColumn = columns[leftIndex + 1];
        var leftWidth = GetResizableColumnWidth(leftColumn);
        var rightWidth = GetResizableColumnWidth(rightColumn);
        var appliedChange = ColumnWidthPreferences.ApplyAdjacentDelta(
            leftWidth,
            rightWidth,
            horizontalChange,
            GetResizableColumnMinimum(leftColumn),
            GetResizableColumnMinimum(rightColumn),
            DatabaseColumnPresentation.MaximumWidth);
        SetResizableColumnWidth(leftColumn, leftWidth + appliedChange);
        SetResizableColumnWidth(rightColumn, rightWidth - appliedChange);
        return new DatabaseColumnWidthFeedback(
            leftColumn,
            rightColumn,
            GetResizableColumnWidth(leftColumn),
            GetResizableColumnWidth(rightColumn));
    }

    internal DatabaseColumnWidthFeedback? GetAdjacentDatabaseColumnWidths(
        object leftColumn)
    {
        return ResizeAdjacentDatabaseColumns(leftColumn, horizontalChange: 0);
    }

    internal void PersistDatabaseColumnWidths(DatabaseColumnWidthFeedback feedback)
    {
        var widths = new List<(string Key, double Width)>(2);
        AddColumnWidthPreference(widths, feedback.LeftColumn);
        AddColumnWidthPreference(widths, feedback.RightColumn);
        ColumnWidthPreferences.Save(_settingsService, widths.ToArray());
    }

    internal void PersistDatabaseColumnWidths(
        IEnumerable<DatabaseColumnPresentation> columns)
    {
        ColumnWidthPreferences.Save(
            _settingsService,
            columns.Select(column => (
                GetDatabaseColumnPreferenceKey(column.MappingIdentity),
                column.Width)).ToArray());
    }

    private void RestoreDatabaseColumnWidth(DatabaseColumnPresentation column)
    {
        column.Width = ColumnWidthPreferences.Resolve(
            _settingsService,
            GetDatabaseColumnPreferenceKey(column.MappingIdentity),
            DatabaseColumnPresentation.DefaultWidth,
            DatabaseColumnPresentation.MinimumWidth,
            DatabaseColumnPresentation.MaximumWidth);
    }

    private static string GetDatabaseColumnPreferenceKey(string mappingIdentity) =>
        $"database.review.{mappingIdentity}";

    private void RestoreMetadataColumnWidth(DatabaseMetadataFieldPresentation column)
    {
        column.Width = ColumnWidthPreferences.Resolve(
            _settingsService,
            GetMetadataColumnPreferenceKey(column.Field),
            DatabaseMetadataFieldPresentation.DefaultWidth,
            DatabaseMetadataFieldPresentation.MinimumWidth,
            DatabaseMetadataFieldPresentation.MaximumWidth);
    }

    private void PersistMetadataColumnWidths(
        IEnumerable<DatabaseMetadataFieldPresentation> columns)
    {
        ColumnWidthPreferences.Save(
            _settingsService,
            columns.Select(column => (
                GetMetadataColumnPreferenceKey(column.Field),
                column.Width)).ToArray());
    }

    private static string GetMetadataColumnPreferenceKey(DatabaseMetadataField field) =>
        $"database.metadata.{field}";

    private object[] GetVisibleResizableColumns() =>
        [.. _visibleColumns.Cast<object>(), .. _visibleMetadataFields.Cast<object>()];

    private void UpdateResizeBoundaries()
    {
        foreach (var column in _metadataFields)
        {
            column.SetCanResizeWithNext(false);
        }
        foreach (var column in _columns)
        {
            column.SetCanResizeWithNext(false);
        }

        var columns = GetVisibleResizableColumns();
        for (var index = 0; index < columns.Length - 1; index++)
        {
            SetCanResizeWithNext(columns[index], true);
        }
    }

    private static double GetResizableColumnWidth(object column) => column switch
    {
        DatabaseMetadataFieldPresentation metadata => metadata.Width,
        DatabaseColumnPresentation data => data.Width,
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };

    private static double GetResizableColumnMinimum(object column) => column switch
    {
        DatabaseMetadataFieldPresentation => DatabaseMetadataFieldPresentation.MinimumWidth,
        DatabaseColumnPresentation => DatabaseColumnPresentation.MinimumWidth,
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };

    private static void SetResizableColumnWidth(object column, double width)
    {
        switch (column)
        {
            case DatabaseMetadataFieldPresentation metadata:
                metadata.Width = width;
                break;
            case DatabaseColumnPresentation data:
                data.Width = width;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(column));
        }
    }

    private static void SetCanResizeWithNext(object column, bool value)
    {
        switch (column)
        {
            case DatabaseMetadataFieldPresentation metadata:
                metadata.SetCanResizeWithNext(value);
                break;
            case DatabaseColumnPresentation data:
                data.SetCanResizeWithNext(value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(column));
        }
    }

    private static void AddColumnWidthPreference(
        ICollection<(string Key, double Width)> widths,
        object column)
    {
        switch (column)
        {
            case DatabaseMetadataFieldPresentation metadata:
                widths.Add((GetMetadataColumnPreferenceKey(metadata.Field), metadata.Width));
                break;
            case DatabaseColumnPresentation data:
                widths.Add((GetDatabaseColumnPreferenceKey(data.MappingIdentity), data.Width));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(column));
        }
    }

    private void OnWorkflowStateChanged(object? sender, WorkflowStateSnapshot e)
    {
        var publishedColumns = _databaseBuildCoordinator is null
            && IsSuccessfulDatabasePublication(e)
            ? CapturePublishedColumns()
            : null;
        DispatchToUi(() =>
        {
            if (publishedColumns is not null)
            {
                PublishDatabaseGeneration(publishedColumns);
            }

            DatabaseStatus = e.Database;
            _extractionStatus = e.Extraction;
            CaptureLatestExtractionAttempt(e);
            NotifyExtractionReviewChanged();
            NotifyRowInclusionCommandsChanged();
            NotifyExportReadinessChanged();
        });
    }

    private void OnPublishedGenerationChanged(
        object? sender,
        DatabaseGenerationSummary? generation)
    {
        DispatchToUi(() =>
        {
            if (generation is null)
            {
                ClearPublishedGeneration();
            }
            else
            {
                ApplyPublishedGeneration(generation);
            }
            NotifyExtractionReviewChanged();
        });
    }

    private void ClearPublishedGeneration()
    {
        _publishedGeneration = null;
        _publishedGenerationId = null;
        _publishedValueCount = 0;
        var cancellation = Interlocked.Exchange(ref _reviewCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        _reviewFailureDescription = null;
        _records.ClearReview();
        _datasets.Clear();
        SelectedDataset = null;
        NotifyReviewChanged();
    }

    private void OnPublishedExtractionChanged(
        object? sender,
        CIA.Contracts.Extraction.ExtractionResultSummary result)
    {
        DispatchToUi(NotifyExtractionReviewChanged);
    }

    private void ApplyPublishedGeneration(DatabaseGenerationSummary generation)
    {
        _publishedGeneration = generation;
        _publishedGenerationId = generation.OperationId;
        _publishedValueCount = generation.ValueCount;
        _reviewFailureDescription = null;
        _records.ClearReview();
        _datasets.Clear();
        if (generation.IsHierarchyAware)
        {
            _exportRouting.Synchronize(generation);
            foreach (var dataset in generation.Datasets.OrderBy(dataset => dataset.Ordinal))
            {
                _datasets.Add(new DatabaseDatasetPresentation(dataset));
            }
            SelectedDataset = _datasets.FirstOrDefault();
        }
        else
        {
            PublishDatabaseGeneration(generation.Mapping.Columns);
        }
        NotifyReviewChanged();

        if (_databaseReviewClient is not null && !generation.IsHierarchyAware)
        {
            // Legacy generations are invalidated by schema v4 and cannot be row-reviewed.
            _reviewFailureDescription = "This legacy Database must be rebuilt before row-aware review.";
        }
    }

    private void OnMetadataFieldPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DatabaseMetadataFieldPresentation.IsVisible)
            || _synchronizingMetadataVisibility
            || _synchronizingDatabaseColumnState)
        {
            return;
        }

        if (_metadataVisibilityMode != DatabaseMetadataVisibilityMode.Custom)
        {
            _metadataVisibilityMode = DatabaseMetadataVisibilityMode.Custom;
            OnPropertyChanged(nameof(MetadataVisibilityMode));
        }
        RebuildVisibleMetadataColumns();
        OnPropertyChanged(nameof(VisibleMetadataFields));
        SynchronizeSelectedDatabaseColumnState();
        RefreshOutputOverrideFields();
    }

    private void RebuildVisibleMetadataColumns()
    {
        _visibleMetadataFields.Clear();
        foreach (var field in _metadataFields.Where(field => field.IsVisible))
        {
            _visibleMetadataFields.Add(field);
        }
        UpdateResizeBoundaries();
        OnPropertyChanged(nameof(VisibleMetadataFields));
        if (_records.Count > 0)
        {
            RebuildReviewRows();
        }
    }

    private void ApplyDataset(DatabaseDatasetSummary dataset)
    {
        var mappingsByKey = dataset.Mappings
            .GroupBy(mapping => mapping.FieldKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var columns = dataset.Columns.Select(column =>
        {
            var mappings = mappingsByKey[column.Identity.FieldKey];
            var header = column.Identity.RepeatCoordinates.Coordinates.Count == 0
                ? column.EffectiveName
                : $"{column.EffectiveName} {column.Identity.RepeatCoordinates}";
            return new DatabaseColumnMapping(
                header,
                mappings.SelectMany(mapping => mapping.DetailedIdentities)
                    .Select(identity => identity.InformationType)
                    .Distinct(StringComparer.Ordinal).ToArray());
        }).ToArray();
        PublishDatabaseGeneration(columns, dataset.Columns.Select(CreateColumnIdentity).ToArray());
    }

    private void ApplyExportDataset(DatabaseDatasetSummary dataset)
    {
        var set = _exportRouting.SourceSets.SingleOrDefault(candidate =>
            candidate.SourceSetId == dataset.SourceSetId);
        if (set is not null)
        {
            _synchronizingDatabaseColumnState = true;
            try
            {
                ReorderColumns(set.Fields.Select(field =>
                    CreateColumnIdentity(field.DatabaseColumnIdentity)));
                foreach (var column in _columns)
                {
                    column.IsVisible = set.Fields.Single(field =>
                        CreateColumnIdentity(field.DatabaseColumnIdentity)
                            == column.MappingIdentity).IsExported;
                }
                foreach (var metadata in _metadataFields)
                {
                    metadata.IsVisible = set.MetadataFields.Single(field =>
                        field.MetadataField == metadata.Field).IsExported;
                }
            }
            finally
            {
                _synchronizingDatabaseColumnState = false;
            }
        }

        UpdatePositionsAndPresentation();
        RebuildVisibleMetadataColumns();
        RefreshOutputOverrideFields();
        RebuildColumnChoices();
        NotifyExportRoutingPropertiesChanged();
    }

    private void RefreshOutputOverrideFields()
    {
        _exportColumns.Clear();
        _exportMetadataFields.Clear();
        if (SelectedExportSet is not { } set)
        {
            return;
        }

        foreach (var column in _columns.Where(column => column.IsVisible))
        {
            var field = set.Fields.SingleOrDefault(candidate =>
                CreateColumnIdentity(candidate.DatabaseColumnIdentity) == column.MappingIdentity);
            if (field is not null)
            {
                _exportColumns.Add(field);
            }
        }
        foreach (var metadata in _metadataFields.Where(metadata => metadata.IsVisible))
        {
            var field = set.MetadataFields.Single(candidate =>
                candidate.MetadataField == metadata.Field);
            _exportMetadataFields.Add(field);
        }

        NotifyColumnSummariesChanged();
    }

    private void SynchronizeSelectedDatabaseColumnState()
    {
        if (_synchronizingDatabaseColumnState || SelectedExportSet is not { } set)
        {
            return;
        }

        var fieldsByIdentity = set.Fields.ToDictionary(
            field => CreateColumnIdentity(field.DatabaseColumnIdentity),
            StringComparer.Ordinal);
        var fields = _columns
            .Where(column => fieldsByIdentity.ContainsKey(column.MappingIdentity))
            .Select(column => new DatabaseExportFieldState(
                fieldsByIdentity[column.MappingIdentity].DatabaseColumnIdentity,
                column.IsVisible))
            .ToArray();
        var metadata = _metadataFields.Select(field => new DatabaseExportMetadataState(
            field.Field,
            field.IsVisible)).ToArray();

        _synchronizingDatabaseColumnState = true;
        try
        {
            set.ApplyDatabaseColumnState(fields, metadata);
        }
        finally
        {
            _synchronizingDatabaseColumnState = false;
        }
    }

    private void RebuildColumnChoices()
    {
        _columnChoices.Clear();
        foreach (var column in _columns)
        {
            var exportField = SelectedExportSet?.Fields.SingleOrDefault(field =>
                CreateColumnIdentity(field.DatabaseColumnIdentity) == column.MappingIdentity);
            if (exportField is null)
            {
                continue;
            }

            column.IsVisible = exportField.IsExported;
            _columnChoices.Add(new DatabaseColumnChoicePresentation(
                column.DatabaseField,
                "Generated",
                isIncluded: () => column.IsVisible,
                setIncluded: value =>
                {
                    column.IsVisible = value;
                },
                column));
        }

        foreach (var metadata in _metadataFields)
        {
            var exportField = SelectedExportSet?.MetadataFields.SingleOrDefault(field =>
                field.MetadataField == metadata.Field);
            if (exportField is null)
            {
                continue;
            }

            metadata.IsVisible = exportField.IsExported;
            _columnChoices.Add(new DatabaseColumnChoicePresentation(
                metadata.DisplayName,
                "Metadata",
                isIncluded: () => metadata.IsVisible,
                setIncluded: value =>
                {
                    metadata.IsVisible = value;
                },
                dataColumn: null));
        }

        OnPropertyChanged(nameof(ColumnCountText));
    }

    private bool CanPrepareForExport()
    {
        return _extractionCoordinator?.CanExtract() == true;
    }

    private async Task PrepareForExportAsync()
    {
        if (_extractionCoordinator is not null)
        {
            await _extractionCoordinator.ExtractAsync().ConfigureAwait(false);
        }
    }

    private void BrowseOutputFolder()
    {
        var selected = _exportFolderPicker?.Browse(OutputFolder);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            OutputFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
            WorkbookExportStatusText = RememberOutputFolder()
                ? "Output folder selected and remembered."
                : "Output folder selected for this session.";
        }
    }

    private bool CanRunExportFlow()
    {
        if (_workflowCoordinator.Current.ActiveOperation is not null
            || _extractionCoordinator is null
            || _workbookExportCoordinator is null
            || _workbookCollisionResolver is null)
        {
            return false;
        }

        var validation = CurrentExportValidation;
        if (!validation.IsValid
            || validation.RunnableWorkbooks.Count == 0
            || !CaptureExportConfiguration().SourceSets.Any(set =>
                set.IsEnabled && set.Fields.Any(field => field.IsValueIncluded)))
        {
            return false;
        }

        return IsExportAvailable || _extractionCoordinator.CanExtract();
    }

    private async Task ExportToExcelAsync()
    {
        if (_workbookExportCoordinator is null
            || _workbookCollisionResolver is null
            || _extractionCoordinator is null)
        {
            return;
        }

        var initialValidation = CurrentExportValidation;
        if (!initialValidation.IsValid
            || initialValidation.RunnableWorkbooks.Count == 0
            || !CaptureExportConfiguration().SourceSets.Any(set =>
                set.IsEnabled && set.Fields.Any(field => field.IsValueIncluded)))
        {
            WorkbookExportStatusText = initialValidation.Failures.FirstOrDefault()?.Description
                ?? "At least one value field must be included for export.";
            return;
        }

        if (_workflowCoordinator.Current.Extraction != WorkflowArtifactStatus.Current
            || !ExtractionBasisMatchesReviewedDatabase
            || _extractionCoordinator.CurrentResult is null)
        {
            WorkbookExportStatusText = "Preparing the current Database for export...";
            var preparationProgress = _globalProgress?.Begin(
                "Preparing export",
                "Extracting current Database");
            var preparation = await _extractionCoordinator.ExtractAsync();
            if (!preparation.Accepted
                || _workflowCoordinator.Current.Extraction != WorkflowArtifactStatus.Current
                || !ExtractionBasisMatchesReviewedDatabase
                || _extractionCoordinator.CurrentResult is null)
            {
                if (preparationProgress is { } failedPreparation)
                {
                    var cancelled = _workflowCoordinator.Current.LatestOperation?.State
                        == WorkflowOperationState.Cancelled;
                    _globalProgress?.Complete(
                        failedPreparation,
                        cancelled
                            ? GlobalOperationProgressState.Cancelled
                            : GlobalOperationProgressState.Failed,
                        cancelled ? "cancelled" : "failed");
                }
                WorkbookExportStatusText = preparation.Rejection?.Reason
                    ?? "Export preparation did not publish a current matching Extraction Result.";
                NotifyExportReadinessChanged();
                return;
            }

            if (preparationProgress is { } completedPreparation)
            {
                _globalProgress?.Complete(
                    completedPreparation,
                    _extractionCoordinator.CurrentCompletion?.Outcome
                        == OperationOutcome.CompletedWithIssues
                        ? GlobalOperationProgressState.CompletedWithIssues
                        : GlobalOperationProgressState.CompletedSuccessfully,
                    _extractionCoordinator.CurrentCompletion?.Outcome
                        == OperationOutcome.CompletedWithIssues
                        ? "completed with issues"
                        : "completed successfully");
            }
        }

        var extractionResult = _extractionCoordinator.CurrentResult!;

        if (AlwaysAskWhereToExport)
        {
            var selected = _exportFolderPicker?.Browse(OutputFolder);
            if (string.IsNullOrWhiteSpace(selected))
            {
                WorkbookExportStatusText = "Export cancelled before processing started.";
                return;
            }

            OutputFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
            RememberOutputFolder();
        }

        if (!IsExportAvailable)
        {
            WorkbookExportStatusText = "The prepared Extraction Result, export configuration, and output folder must all be ready.";
            return;
        }

        var resolvedDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(OutputFolder));
        var overwriteAuthorizations = new Dictionary<WorkbookDefinitionId, string>();
        WorkbookPublicationPlan publicationPlan;
        ExportConfigurationSnapshot configuration;
        while (true)
        {
            configuration = CaptureExportConfiguration();
            var validation = ExportConfigurationValidator.Validate(configuration, extractionResult);
            if (!validation.IsValid
                || validation.RunnableWorkbooks.Count == 0
                || !configuration.SourceSets.Any(set =>
                    set.IsEnabled && set.Fields.Any(field => field.IsValueIncluded)))
            {
                WorkbookExportStatusText = validation.Failures.FirstOrDefault()?.Description
                    ?? "At least one value field must be included for export.";
                return;
            }

            var targets = validation.RunnableWorkbooks.Select(runnable =>
            {
                var finalPath = Path.Combine(
                    resolvedDirectory,
                    runnable.Workbook.FileName);
                return new WorkbookCollisionTarget(
                    runnable.Workbook.WorkbookDefinitionId,
                    runnable.Workbook.FileName,
                    finalPath,
                    File.Exists(finalPath));
            }).ToArray();
            var unresolvedCollisions = targets.Where(target =>
                target.Exists
                && (!overwriteAuthorizations.TryGetValue(
                        target.WorkbookDefinitionId,
                        out var authorizedPath)
                    || !string.Equals(
                        authorizedPath,
                        target.FinalPath,
                        StringComparison.OrdinalIgnoreCase))).ToArray();
            if (unresolvedCollisions.Length == 0)
            {
                publicationPlan = new WorkbookPublicationPlan(
                    resolvedDirectory,
                    targets.Select(target => new WorkbookPublicationTarget(
                        target.WorkbookDefinitionId,
                        target.FinalPath,
                        overwriteAuthorizations.TryGetValue(
                                target.WorkbookDefinitionId,
                                out var authorizedPath)
                            && string.Equals(
                                authorizedPath,
                                target.FinalPath,
                                StringComparison.OrdinalIgnoreCase)
                                ? WorkbookPublicationDisposition.OverwriteExisting
                                : WorkbookPublicationDisposition.CreateNew)).ToArray());
                break;
            }

            var resolution = _workbookCollisionResolver.Resolve(
                new WorkbookCollisionResolutionRequest(
                    resolvedDirectory,
                    targets,
                    configuration.Workbooks
                        .Where(workbook => targets.All(target =>
                            target.WorkbookDefinitionId != workbook.WorkbookDefinitionId))
                        .Select(workbook => workbook.FileName)
                        .ToArray()));
            if (resolution is null
                || resolution.Decisions.Any(decision =>
                    decision.Action == WorkbookCollisionAction.Cancel))
            {
                WorkbookExportStatusText = "Export cancelled before processing started.";
                return;
            }

            var decisionsById = resolution.Decisions.ToDictionary(decision =>
                decision.WorkbookDefinitionId);
            if (decisionsById.Count != unresolvedCollisions.Length
                || unresolvedCollisions.Any(target =>
                    !decisionsById.ContainsKey(target.WorkbookDefinitionId)))
            {
                WorkbookExportStatusText = "Every colliding workbook requires an explicit decision.";
                return;
            }

            var renamed = false;
            foreach (var collision in unresolvedCollisions)
            {
                var decision = decisionsById[collision.WorkbookDefinitionId];
                if (decision.Action == WorkbookCollisionAction.Overwrite)
                {
                    overwriteAuthorizations[collision.WorkbookDefinitionId] = collision.FinalPath;
                    continue;
                }

                if (decision.Action != WorkbookCollisionAction.DifferentName
                    || !ExportConfigurationValidator.IsValidWorkbookFileName(
                        decision.DifferentFileName ?? string.Empty)
                    || string.Equals(
                        decision.DifferentFileName,
                        collision.FileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    WorkbookExportStatusText = "A different workbook name must be valid, unique, and changed.";
                    return;
                }

                WorkbookDefinitions.Single(workbook =>
                    workbook.WorkbookDefinitionId == collision.WorkbookDefinitionId).FileName =
                    decision.DifferentFileName!;
                overwriteAuthorizations.Remove(collision.WorkbookDefinitionId);
                renamed = true;
            }

            if (!renamed)
            {
                continue;
            }

            var renamedValidation = ExportConfigurationValidator.Validate(
                CaptureExportConfiguration(),
                extractionResult);
            if (!renamedValidation.IsValid)
            {
                WorkbookExportStatusText = renamedValidation.Failures[0].Description;
                return;
            }
        }

        WorkbookExportStatusText = "Exporting workbook batch...";
        NotifyExportReadinessChanged();
        var exportProgress = _globalProgress?.Begin(
            "Exporting to Excel",
            publicationPlan.Targets.Count == 1
                ? $"Publishing {Path.GetFileName(publicationPlan.Targets[0].FinalPath)}"
                : $"Publishing {publicationPlan.Targets.Count:N0} workbooks");
        var execution = await _workbookExportCoordinator.ExportWithBatchAsync(
                publicationPlan,
                configuration)
            .ConfigureAwait(false);
        var publishedBatchMatchesOperation = execution.Command.Accepted
            && execution.PublishedBatch is { } publishedBatch
            && execution.Command.Operation?.OperationId == publishedBatch.OperationId;
        if (exportProgress is { } exportUpdateId)
        {
            var cancelled = _workflowCoordinator.Current.LatestOperation?.State
                == WorkflowOperationState.Cancelled;
            _globalProgress?.Complete(
                exportUpdateId,
                publishedBatchMatchesOperation
                    ? _workflowCoordinator.Current.LatestOperation?.State
                        == WorkflowOperationState.CompletedWithIssues
                        ? GlobalOperationProgressState.CompletedWithIssues
                        : GlobalOperationProgressState.CompletedSuccessfully
                    : cancelled
                        ? GlobalOperationProgressState.Cancelled
                        : GlobalOperationProgressState.Failed,
                publishedBatchMatchesOperation
                    ? _workflowCoordinator.Current.LatestOperation?.State
                        == WorkflowOperationState.CompletedWithIssues
                        ? "completed with issues"
                        : "completed successfully"
                    : cancelled ? "cancelled" : "failed");
        }
        DispatchToUi(() =>
        {
            var batch = execution.PublishedBatch;
            if (execution.Command.Accepted
                && batch is not null
                && execution.Command.Operation?.OperationId == batch.OperationId)
            {
                var postExportAction = _postExportBehaviorCoordinator?.Apply(
                    execution.Command.Operation.OperationId,
                    batch);
                WorkbookExportStatusText =
                    $"Published {batch.Workbooks.Count:N0} workbook(s): {string.Join(", ", batch.Workbooks.Select(workbook => workbook.FinalPath))}";
                if (postExportAction is { Succeeded: false })
                {
                    WorkbookExportStatusText = postExportAction.FailureDescription!;
                }
            }
            else
            {
                WorkbookExportStatusText = execution.Command.Rejection?.Reason
                    ?? "The workbook batch was not published.";
            }

            NotifyExportReadinessChanged();
        });
    }

    private bool RememberOutputFolder()
    {
        if (_settingsService is null || !Directory.Exists(OutputFolder))
        {
            return false;
        }

        var result = _settingsService.Save(
            _settingsService.Current with { LastUsedOutputDirectory = OutputFolder });
        return result.Succeeded;
    }

    public int CachedReviewPageCount => _records.CachedPageCount;

    public async Task EnsureReviewRowsAvailableAsync(
        int firstVisibleOrdinal,
        int visibleRowCount)
    {
        if (_publishedGenerationId is not { } generationId
            || SelectedDataset is not { } dataset
            || _reviewCancellation is not { } cancellation
            || Records.Count == 0)
        {
            return;
        }

        var first = Math.Clamp(firstVisibleOrdinal, 1, Records.Count);
        var last = Math.Clamp(
            first + Math.Max(visibleRowCount, 1) - 1,
            first,
            Records.Count);
        _firstVisibleReviewOrdinal = first;
        _visibleReviewRowCount = last - first + 1;
        DispatchToUi(NotifyRowInclusionCommandsChanged);
        var firstPage = VirtualizedDatabaseReviewCollection.GetPageStart(first);
        var lastPage = VirtualizedDatabaseReviewCollection.GetPageStart(last);
        for (var pageStart = firstPage;
             pageStart <= lastPage;
             pageStart += DatabaseReviewLimits.MaximumRowsPerPage)
        {
            await LoadReviewChunkAsync(
                    generationId,
                    dataset.Summary.SourceSetId,
                    pageStart,
                    cancellation,
                    isInitial: false)
                .ConfigureAwait(false);
        }
    }

    private async Task BeginReviewSessionAsync(OperationId generationId)
    {
        if (_databaseReviewClient is null || SelectedDataset is not { } dataset)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(
            ref _reviewCancellation,
            cancellation);
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        lock (_loadingReviewPages)
        {
            _loadingReviewPages.Clear();
        }
        DispatchToUi(() =>
        {
            _records.ClearReview();
            _firstVisibleReviewOrdinal = 1;
            _visibleReviewRowCount = DatabaseReviewLimits.MaximumRowsPerPage;
            _reviewFailureDescription = null;
            IsReviewLoading = true;
            NotifyReviewChanged();
        });

        await LoadReviewChunkAsync(
                generationId,
                dataset.Summary.SourceSetId,
                startRowOrdinal: 1,
                cancellation,
                isInitial: true)
            .ConfigureAwait(false);
    }

    private async Task LoadReviewChunkAsync(
        OperationId generationId,
        SourceSetId sourceSetId,
        int startRowOrdinal,
        CancellationTokenSource cancellation,
        bool isInitial)
    {
        if (_databaseReviewClient is null || (!isInitial && _records.IsPageLoaded(startRowOrdinal)))
        {
            return;
        }

        lock (_loadingReviewPages)
        {
            if (!_loadingReviewPages.Add(startRowOrdinal))
            {
                return;
            }
        }

        try
        {
            await _reviewLoadGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                var result = await _databaseReviewClient.ReadPageAsync(
                        new DatabaseReviewQuery(
                            generationId,
                            sourceSetId,
                            startRowOrdinal,
                            DatabaseReviewLimits.MaximumRowsPerPage,
                            SearchText,
                            RowInclusionFilter),
                        cancellation.Token)
                    .ConfigureAwait(false);
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                DispatchToUi(() =>
                {
                    var contextIsCurrent = _publishedGenerationId == generationId
                        && SelectedDataset?.Summary.SourceSetId == sourceSetId
                        && ReferenceEquals(_reviewCancellation, cancellation);
                    if (!contextIsCurrent)
                    {
                        return;
                    }

                    if (!result.Accepted
                        || result.Page is null
                        || result.Page.GenerationId != generationId
                        || result.Page.Dataset.SourceSetId != sourceSetId
                        || (!isInitial
                            && result.Page.TotalPresentationRowCount != _records.Count)
                        || !PageMatchesPublishedColumns(result.Page))
                    {
                        if (isInitial)
                        {
                            _records.ClearReview();
                        }
                        _reviewFailureDescription = result.FailureDescription
                            ?? "The active published Database could not provide this review chunk.";
                    }
                    else if (isInitial)
                    {
                        _records.Reset(
                            result.Page.TotalPresentationRowCount,
                            result.Page.StartRowOrdinal,
                            result.Page.Rows);
                        _reviewFailureDescription = null;
                    }
                    else
                    {
                        _records.SetPage(
                            result.Page.StartRowOrdinal,
                            result.Page.Rows,
                            result.Page.TotalPresentationRowCount);
                        _reviewFailureDescription = null;
                    }

                    if (isInitial)
                    {
                        IsReviewLoading = false;
                    }
                    NotifyReviewChanged();
                });
            }
            finally
            {
                _reviewLoadGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            DispatchToUi(() =>
            {
                var contextIsCurrent = _publishedGenerationId == generationId
                    && SelectedDataset?.Summary.SourceSetId == sourceSetId
                    && ReferenceEquals(_reviewCancellation, cancellation);
                if (!contextIsCurrent)
                {
                    return;
                }

                if (isInitial)
                {
                    _records.ClearReview();
                    IsReviewLoading = false;
                }
                _reviewFailureDescription =
                    "The active published Database could not provide this review chunk.";
                NotifyReviewChanged();
            });
        }
        finally
        {
            lock (_loadingReviewPages)
            {
                _loadingReviewPages.Remove(startRowOrdinal);
            }
        }
    }

    private bool PageMatchesPublishedColumns(DatabaseReviewPage page)
    {
        var publishedNames = _columns
            .Select(column => column.MappingIdentity)
            .ToHashSet(StringComparer.Ordinal);
        return page.Columns.Count == publishedNames.Count
            && page.Columns.All(column => publishedNames.Contains(CreateColumnIdentity(column)));
    }

    private void RebuildReviewRows()
    {
        _records.Reproject(CreateReviewRowPresentation);
        NotifyReviewChanged();
    }

    private DatabaseReviewRowPresentation CreateReviewRowPresentation(DatabaseReviewRow row)
    {
        var cells = _visibleColumns.Select(column =>
        {
            var cell = row.Cells.SingleOrDefault(value =>
                string.Equals(
                    CreateColumnIdentity(value.ColumnIdentity),
                    column.MappingIdentity,
                    StringComparison.Ordinal));
            return new DatabaseReviewCellPresentation(column, cell);
        }).ToArray();
        return new DatabaseReviewRowPresentation(
            row.Ordinal,
            row.IsIncluded,
            row.RecordIdentity,
            row.Source,
            row.HasConflict,
            _visibleMetadataFields.Select(field => new DatabaseMetadataCellPresentation(
                field,
                DatabaseRowMetadataProjection.GetValue(
                    row.Source,
                    row.RecordHierarchy,
                    row.Cells.SelectMany(cell => cell.Values).Select(value =>
                        new DatabaseRowMetadataValue(value.DetailedIdentity, value.Lineage)),
                    field.Field))).ToArray(),
            cells,
            IsLoading: false);
    }

    private void NotifyReviewChanged()
    {
        OnPropertyChanged(nameof(HasReviewRows));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDetail));
        NotifyRowInclusionCommandsChanged();
    }

    private void NotifyExtractionReviewChanged()
    {
        OnPropertyChanged(nameof(ExtractionReviewState));
        OnPropertyChanged(nameof(ExtractionReviewStateText));
        OnPropertyChanged(nameof(ExtractionReviewContext));
        OnPropertyChanged(nameof(ExtractionBasisMatchesReviewedDatabase));
        PrepareForExportCommand.NotifyCanExecuteChanged();
        NotifyExportReadinessChanged();
    }

    private void NotifyExportReadinessChanged()
    {
        SynchronizeExportReadinessContext();
        OnPropertyChanged(nameof(IsExportAvailable));
        ExportToExcelCommand.NotifyCanExecuteChanged();
    }

    private void SynchronizeExportReadinessContext()
    {
        if (_workbookExportCoordinator is null)
        {
            return;
        }

        ExportConfigurationSnapshot? configuration = null;
        try
        {
            configuration = CaptureExportConfiguration();
        }
        catch (ArgumentException)
        {
            // An incomplete presentation configuration is truthfully not export-ready.
        }

        _workbookExportCoordinator.UpdateReadinessContext(configuration, OutputFolder);
    }

    private void CaptureLatestExtractionAttempt(WorkflowStateSnapshot state)
    {
        if (state.LatestOperation?.Kind == WorkflowOperationKind.Extraction)
        {
            _latestExtractionAttempt = state.LatestOperation;
        }
    }

    private void DispatchToUi(Action update)
    {
        var uiContext = _uiSynchronizationContext;
        if (uiContext is null || ReferenceEquals(uiContext, SynchronizationContext.Current))
        {
            update();
            return;
        }

        uiContext.Post(_ => update(), null);
    }

    private static string CreateColumnIdentity(
        IReadOnlyList<string> sourceInformationTypes)
    {
        var identity = new StringBuilder();
        foreach (var informationType in sourceInformationTypes)
        {
            identity.Append(informationType.Length);
            identity.Append(':');
            identity.Append(informationType);
        }

        return identity.ToString();
    }

    private static bool IsUsefulMetadataField(DatabaseMetadataField field) => field is
        DatabaseMetadataField.SourceFile
        or DatabaseMetadataField.SourceKind
        or DatabaseMetadataField.RecordHierarchy;

    private static string GetMetadataFieldDisplayName(DatabaseMetadataField field) => field switch
    {
        DatabaseMetadataField.SourceSet => "Source Set",
        DatabaseMetadataField.SourceFile => "Source File",
        DatabaseMetadataField.FullSourcePath => "Full Source Path",
        DatabaseMetadataField.SourceId => "Source ID",
        DatabaseMetadataField.FileModified => "File Modified",
        DatabaseMetadataField.SourceKind => "Source Kind",
        DatabaseMetadataField.ContainerProvenance => "Container Provenance",
        DatabaseMetadataField.RecordHierarchy => "Record Hierarchy",
        DatabaseMetadataField.ValuePath => "Value / XML Path",
        DatabaseMetadataField.TraversalOrdinal => "Traversal Ordinal",
        DatabaseMetadataField.CandidateKind => "Candidate Kind",
        DatabaseMetadataField.StructuralIdentity => "Structural Identity",
        _ => field.ToString()
    };

    private static string CreateColumnIdentity(DatabaseColumnDefinition column) =>
        CreateColumnIdentity(column.Identity);

    private static string CreateColumnIdentity(DatabaseColumnIdentity identity) =>
        $"{identity.SourceSetId}:{identity.FieldKey}:{string.Join(',', identity.RepeatCoordinates.Coordinates)}";

    private void PublishDatabaseGeneration(
        IReadOnlyList<DatabaseColumnMapping> publishedColumns,
        IReadOnlyList<string> identities)
    {
        if (publishedColumns.Count != identities.Count)
        {
            throw new ArgumentException("Database presentation columns require matching typed identities.");
        }
        var pairs = publishedColumns.Zip(identities).ToArray();
        var publishedIds = identities.ToHashSet(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            if (!_columnCache.TryGetValue(pair.Second, out var column))
            {
                column = new DatabaseColumnPresentation(pair.Second, pair.First);
                RestoreDatabaseColumnWidth(column);
                column.PropertyChanged += OnColumnPropertyChanged;
                _columnCache.Add(pair.Second, column);
            }
            else
            {
                column.UpdateMapping(pair.First);
            }
        }
        var order = _columns.Where(column => publishedIds.Contains(column.MappingIdentity))
            .Select(column => column.MappingIdentity)
            .Concat(identities.Where(identity => !_columns.Any(column => column.MappingIdentity == identity)))
            .ToArray();
        _publishedColumnOrder.Clear();
        _publishedColumnOrder.AddRange(identities);
        ReorderColumns(order);
        UpdatePositionsAndPresentation();
    }

    private async Task SetRowIncludedAsync(DatabaseReviewRowPresentation? row)
    {
        if (row is null)
        {
            return;
        }
        await SetRowsIncludedAsync([row], !row.IsIncluded).ConfigureAwait(false);
    }

    private Task SetVisibleRowsIncludedAsync(bool included) =>
        SetRowsIncludedAsync(VisibleLoadedReviewRows(), included);

    private IReadOnlyList<DatabaseReviewRowPresentation> VisibleLoadedReviewRows()
    {
        var lastVisibleOrdinal = _firstVisibleReviewOrdinal + _visibleReviewRowCount - 1;
        return _records.LoadedRows
            .Where(row => row.Ordinal >= _firstVisibleReviewOrdinal
                && row.Ordinal <= lastVisibleOrdinal)
            .ToArray();
    }

    private async Task SetRowsIncludedAsync(
        IReadOnlyList<DatabaseReviewRowPresentation> rows,
        bool included)
    {
        var rowsToChange = rows
            .Where(row => !row.IsLoading && row.IsIncluded != included)
            .ToArray();
        if (_databaseReviewClient is null || _publishedGenerationId is not { } generationId
            || SelectedDataset is null || rowsToChange.Length == 0)
        {
            return;
        }

        var authorization = _workflowCoordinator.RecordDatabaseReviewChanged();
        if (!authorization.Accepted)
        {
            return;
        }

        var result = await _databaseReviewClient.SetRowsIncludedAsync(
            new DatabaseRowInclusionChange(
                generationId, SelectedDataset.Summary.SourceSetId,
                rowsToChange.Select(row => row.Ordinal).ToArray(), included)).ConfigureAwait(false);
        if (result.Accepted && result.ChangedRowCount > 0)
        {
            _records.InvalidateOrdinals(rowsToChange.Select(row => row.Ordinal));
            foreach (var pageStart in rowsToChange
                         .Select(row => VirtualizedDatabaseReviewCollection.GetPageStart(row.Ordinal))
                         .Distinct())
            {
                if (_reviewCancellation is { } cancellation)
                {
                    await LoadReviewChunkAsync(
                            generationId,
                            SelectedDataset.Summary.SourceSetId,
                            pageStart,
                            cancellation,
                            isInitial: false)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private bool CanSetRowIncluded(DatabaseReviewRowPresentation? row) =>
        row is { IsLoading: false } && CanChangeRowInclusion();

    private bool CanIncludeVisibleRows() =>
        CanChangeRowInclusion() && VisibleLoadedReviewRows().Any(row => !row.IsIncluded);

    private bool CanExcludeVisibleRows() =>
        CanChangeRowInclusion() && VisibleLoadedReviewRows().Any(row => row.IsIncluded);

    private bool CanChangeRowInclusion() =>
        _databaseReviewClient is not null
        && _publishedGenerationId is not null
        && SelectedDataset is not null
        && _workflowCoordinator.Current.Database == WorkflowArtifactStatus.Current
        && _workflowCoordinator.Current.ActiveOperation is null;

    private void NotifyRowInclusionCommandsChanged()
    {
        SetRowIncludedCommand.NotifyCanExecuteChanged();
        IncludeVisibleRowsCommand.NotifyCanExecuteChanged();
        ExcludeVisibleRowsCommand.NotifyCanExecuteChanged();
    }

    private static bool IsSuccessfulDatabasePublication(WorkflowStateSnapshot state)
    {
        return state.Database == WorkflowArtifactStatus.Current
            && state.ActiveOperation is null
            && state.LatestOperation is
            {
                Kind: WorkflowOperationKind.DatabaseBuild,
                State: WorkflowOperationState.CompletedSuccessfully
                    or WorkflowOperationState.CompletedWithIssues
            };
    }

    private static ExtractionReviewState? ToUnsuccessfulReviewState(
        WorkflowOperationState state)
    {
        return state switch
        {
            WorkflowOperationState.Failed => ExtractionReviewState.Failed,
            WorkflowOperationState.Cancelled => ExtractionReviewState.Cancelled,
            WorkflowOperationState.InterruptedIncomplete => ExtractionReviewState.Interrupted,
            _ => null
        };
    }

    private static string CreateCompletionContext(
        OperationCompletion? completion,
        int? valueCount)
    {
        if (completion is null)
        {
            return "A valid Extraction Result was prepared with recorded item issues.";
        }

        var completed = completion.Items.Count(
            item => item.State == OperationItemState.ProcessedSuccessfully);
        var failed = completion.Items.Count(item => item.State == OperationItemState.Failed);
        var unprocessed = completion.Items.Count(
            item => item.State == OperationItemState.Unprocessed);
        return $"Prepared from the currently reviewed Database generation · {valueCount ?? 0:N0} values · {completed:N0} completed, {failed:N0} failed, {unprocessed:N0} unprocessed.";
    }

    private static string CreateUnsuccessfulContext(
        string latestAttemptDescription,
        CIA.Contracts.Extraction.ExtractionResultSummary? retainedResult)
    {
        return retainedResult is null
            ? $"{latestAttemptDescription}; no Extraction Result was published."
            : $"{latestAttemptDescription}; the previous valid Extraction Result remains retained.";
    }
}

public enum ExtractionReviewState
{
    NotPrepared = 0,
    Preparing = 1,
    Ready = 2,
    ReadyWithIssues = 3,
    OutOfDate = 4,
    Failed = 5,
    Cancelled = 6,
    Interrupted = 7
}

public sealed record DatabaseDatasetPresentation(DatabaseDatasetSummary Summary)
{
    public string DisplayName => Summary.DisplayName;

    public string Context => $"{Summary.RowCount:N0} rows · {Summary.Columns.Count:N0} columns · {Summary.RepeatedDataLayout}";
}

public sealed record DatabaseReviewRowPresentation(
    int Ordinal,
    bool IsIncluded,
    string RecordIdentity,
    DatabaseSourceMetadata? Source,
    bool HasConflict,
    IReadOnlyList<DatabaseMetadataCellPresentation> MetadataCells,
    IReadOnlyList<DatabaseReviewCellPresentation> Cells,
    bool IsLoading)
{
    internal static DatabaseReviewRowPresentation CreateLoading(int ordinal) => new(
        ordinal,
        IsIncluded: false,
        RecordIdentity: string.Empty,
        Source: null,
        HasConflict: false,
        MetadataCells: [],
        Cells: [],
        IsLoading: true);
}

public sealed record DatabaseMetadataCellPresentation(
    DatabaseMetadataFieldPresentation Column,
    string Value)
{
    public DatabaseMetadataField Field => Column.Field;

    public string DisplayName => Column.DisplayName;
}

public sealed class DatabaseColumnChoicePresentation : ObservableObject
{
    private readonly Func<bool> _isIncluded;
    private readonly Action<bool> _setIncluded;

    internal DatabaseColumnChoicePresentation(
        string displayName,
        string category,
        Func<bool> isIncluded,
        Action<bool> setIncluded,
        DatabaseColumnPresentation? dataColumn)
    {
        DisplayName = displayName;
        Category = category;
        _isIncluded = isIncluded;
        _setIncluded = setIncluded;
        DataColumn = dataColumn;
    }

    public string DisplayName { get; }

    public string Category { get; }

    public bool IsGenerated => DataColumn is not null;

    public DatabaseColumnPresentation? DataColumn { get; }

    public bool IsIncluded
    {
        get => _isIncluded();
        set
        {
            if (value == _isIncluded())
            {
                return;
            }

            _setIncluded(value);
            OnPropertyChanged();
        }
    }
}

public sealed class DatabaseMetadataFieldPresentation : ObservableObject
{
    public const double DefaultWidth = 140;
    internal const double MinimumWidth = 60;
    internal const double MaximumWidth = 2000;
    private bool _isVisible;
    private double _width = DefaultWidth;
    private bool _canResizeWithNext;

    internal DatabaseMetadataFieldPresentation(
        DatabaseMetadataField field,
        string displayName,
        bool isVisible)
    {
        Field = field;
        DisplayName = displayName;
        _isVisible = isVisible;
    }

    public DatabaseMetadataField Field { get; }

    public string DisplayName { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, Math.Clamp(value, MinimumWidth, MaximumWidth));
    }

    public bool CanResizeWithNext
    {
        get => _canResizeWithNext;
        private set => SetProperty(ref _canResizeWithNext, value);
    }

    internal void SetCanResizeWithNext(bool value)
    {
        CanResizeWithNext = value;
    }

    internal void ResetWidth()
    {
        Width = DefaultWidth;
    }
}

public sealed class DatabaseReviewCellPresentation
{
    internal DatabaseReviewCellPresentation(
        DatabaseColumnPresentation column,
        DatabaseReviewCell? cell)
    {
        Column = column;
        Cell = cell;
    }

    public DatabaseColumnPresentation Column { get; }

    public DatabaseReviewCell? Cell { get; }

    public DatabaseReviewValue? Value => Cell?.Values.FirstOrDefault();

    public string DisplayValue => Cell is null
        ? string.Empty
        : string.Join(" | ", Cell.Values.Select(value => value.Value).Distinct(StringComparer.Ordinal));

    public bool HasConflict => Cell?.HasConflict == true;

    public string? SourceContext => Value is null
        ? null
        : $"Source information: {Value.SourceInformationType}{Environment.NewLine}Source ID: {Value.SourceId}{Environment.NewLine}Path: {Value.DetailedIdentity.StructuralPath}";
}

public sealed class DatabaseColumnPresentation : ObservableObject
{
    public const double DefaultWidth = 160;
    internal const double MinimumWidth = 60;
    internal const double MaximumWidth = 2000;
    private string _databaseField;
    private bool _isVisible = true;
    private double _width = DefaultWidth;
    private int _position;
    private bool _canResizeWithNext;

    internal DatabaseColumnPresentation(
        string mappingIdentity,
        DatabaseColumnMapping mapping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingIdentity);
        ArgumentNullException.ThrowIfNull(mapping);

        MappingIdentity = mappingIdentity;
        SourceInformationTypes = mapping.SourceInformationTypes;
        _databaseField = mapping.DatabaseTagName;
    }

    internal string MappingIdentity { get; }

    public string InformationType => SourceInformationTypes[0];

    public IReadOnlyList<string> SourceInformationTypes { get; }

    public string DatabaseField
    {
        get => _databaseField;
        private set => SetProperty(ref _databaseField, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, Math.Clamp(value, MinimumWidth, MaximumWidth));
    }

    public int Position
    {
        get => _position;
        private set => SetProperty(ref _position, value);
    }

    public bool CanResizeWithNext
    {
        get => _canResizeWithNext;
        private set => SetProperty(ref _canResizeWithNext, value);
    }

    internal void UpdateMapping(DatabaseColumnMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (!SourceInformationTypes.SequenceEqual(
                mapping.SourceInformationTypes,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "A published Database column cannot change its source information identities.");
        }

        DatabaseField = mapping.DatabaseTagName;
    }

    internal void SetPosition(int position)
    {
        Position = position;
    }

    internal void SetCanResizeWithNext(bool value)
    {
        CanResizeWithNext = value;
    }

    internal void ResetPresentation()
    {
        IsVisible = true;
        Width = DefaultWidth;
    }

}

internal sealed record DatabaseColumnWidthFeedback(
    object LeftColumn,
    object RightColumn,
    double LeftWidth,
    double RightWidth);
