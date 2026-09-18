using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Export;
using CIA.Contracts.Operations;
using CIA.Core.Database;
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
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private readonly ExportRoutingConfigurationPresentation _exportRouting = new();
    private readonly Dictionary<string, DatabaseColumnPresentation> _columnCache = new(
        StringComparer.Ordinal);
    private readonly List<string> _publishedColumnOrder = [];
    private readonly ObservableCollection<DatabaseColumnPresentation> _columns = [];
    private readonly ObservableCollection<ExportFieldPresentation> _exportColumns = [];
    private readonly ObservableCollection<ExportMetadataFieldPresentation> _exportMetadataFields = [];
    private readonly ObservableCollection<DatabaseColumnPresentation> _visibleColumns = [];
    private readonly ObservableCollection<DatabaseReviewRowPresentation> _records = [];
    private readonly ObservableCollection<DatabaseDatasetPresentation> _datasets = [];
    private readonly ObservableCollection<DatabaseMetadataFieldPresentation> _metadataFields = [];
    private readonly ObservableCollection<DatabaseMetadataFieldPresentation> _visibleMetadataFields = [];
    private WorkflowArtifactStatus _databaseStatus;
    private DatabaseReviewPage? _reviewPage;
    private OperationId? _publishedGenerationId;
    private CancellationTokenSource? _reviewCancellation;
    private bool _isReviewLoading;
    private string? _reviewFailureDescription;
    private int _publishedValueCount;
    private DatabaseGenerationSummary? _publishedGeneration;
    private DatabaseDatasetPresentation? _selectedDataset;
    private string? _searchText;
    private DatabaseRowInclusionFilter _rowInclusionFilter;
    private DatabaseMetadataVisibilityMode _metadataVisibilityMode = DatabaseMetadataVisibilityMode.Useful;
    private bool _synchronizingMetadataVisibility;
    private WorkflowArtifactStatus _extractionStatus;
    private WorkflowOperationStatus? _latestExtractionAttempt;
    private int _disposed;

    public DatabaseWorkspaceViewModel(
        ActiveDiscoveryConfiguration discoveryConfiguration,
        IApplicationWorkflowCoordinator workflowCoordinator,
        DatabaseBuildCoordinator? databaseBuildCoordinator = null,
        IDatabaseReviewClient? databaseReviewClient = null,
        ExtractionCoordinator? extractionCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(discoveryConfiguration);
        ArgumentNullException.ThrowIfNull(workflowCoordinator);

        _discoveryConfiguration = discoveryConfiguration;
        _workflowCoordinator = workflowCoordinator;
        _databaseBuildCoordinator = databaseBuildCoordinator;
        _databaseReviewClient = databaseReviewClient;
        _extractionCoordinator = extractionCoordinator;
        _uiSynchronizationContext = SynchronizationContext.Current;
        Columns = new ReadOnlyObservableCollection<DatabaseColumnPresentation>(_columns);
        ExportColumns = new ReadOnlyObservableCollection<ExportFieldPresentation>(
            _exportColumns);
        ExportMetadataFields = new ReadOnlyObservableCollection<ExportMetadataFieldPresentation>(
            _exportMetadataFields);
        WorkbookDefinitions = _exportRouting.Workbooks;
        _exportRouting.Changed += OnExportRoutingChanged;
        VisibleColumns = new ReadOnlyObservableCollection<DatabaseColumnPresentation>(
            _visibleColumns);
        Records = new ReadOnlyObservableCollection<DatabaseReviewRowPresentation>(_records);
        Datasets = new ReadOnlyObservableCollection<DatabaseDatasetPresentation>(_datasets);
        MetadataFields = new ReadOnlyObservableCollection<DatabaseMetadataFieldPresentation>(
            _metadataFields);
        VisibleMetadataColumns = new ReadOnlyObservableCollection<DatabaseMetadataFieldPresentation>(
            _visibleMetadataFields);
        foreach (var field in Enum.GetValues<DatabaseMetadataField>())
        {
            var presentation = new DatabaseMetadataFieldPresentation(
                field,
                GetMetadataFieldDisplayName(field),
                IsUsefulMetadataField(field));
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
        MoveExportFieldUpCommand = new RelayCommand<ExportFieldPresentation>(
            MoveExportFieldUp,
            CanMoveExportFieldUp);
        MoveExportFieldDownCommand = new RelayCommand<ExportFieldPresentation>(
            MoveExportFieldDown,
            CanMoveExportFieldDown);
        CreateExportWorkbookCommand = new RelayCommand(CreateExportWorkbook, HasSelectedDataset);
        MoveWorksheetUpCommand = new RelayCommand(
            () => MoveWorksheet(-1),
            () => CanMoveWorksheet(-1));
        MoveWorksheetDownCommand = new RelayCommand(
            () => MoveWorksheet(1),
            () => CanMoveWorksheet(1));
        ResetColumnLayoutCommand = new RelayCommand(ResetColumnLayout, HasColumns);
        ResetHeadersCommand = new RelayCommand(ResetHeaders, HasSelectedDataset);
        ResetExportCommand = new RelayCommand(ResetExport, HasSelectedDataset);
        PreviousReviewPageCommand = new AsyncRelayCommand(
            PreviousReviewPageAsync,
            CanMoveToPreviousReviewPage);
        NextReviewPageCommand = new AsyncRelayCommand(
            NextReviewPageAsync,
            CanMoveToNextReviewPage);
        PrepareForExportCommand = new AsyncRelayCommand(
            PrepareForExportAsync,
            CanPrepareForExport);
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
    }

    public ReadOnlyObservableCollection<DatabaseColumnPresentation> Columns { get; }

    public ReadOnlyObservableCollection<ExportFieldPresentation> ExportColumns { get; }

    public ReadOnlyObservableCollection<ExportMetadataFieldPresentation> ExportMetadataFields { get; }

    public ReadOnlyObservableCollection<ExportWorkbookPresentation> WorkbookDefinitions { get; }

    public ReadOnlyObservableCollection<DatabaseColumnPresentation> VisibleColumns { get; }

    public ReadOnlyObservableCollection<DatabaseReviewRowPresentation> Records { get; }

    public ReadOnlyObservableCollection<DatabaseDatasetPresentation> Datasets { get; }

    public ReadOnlyObservableCollection<DatabaseMetadataFieldPresentation> MetadataFields { get; }

    public ReadOnlyObservableCollection<DatabaseMetadataFieldPresentation> VisibleMetadataColumns { get; }

    public IRelayCommand<DatabaseColumnPresentation> MoveColumnUpCommand { get; }

    public IRelayCommand<DatabaseColumnPresentation> MoveColumnDownCommand { get; }

    public IRelayCommand<ExportFieldPresentation> MoveExportFieldUpCommand { get; }

    public IRelayCommand<ExportFieldPresentation> MoveExportFieldDownCommand { get; }

    public IRelayCommand CreateExportWorkbookCommand { get; }

    public IRelayCommand MoveWorksheetUpCommand { get; }

    public IRelayCommand MoveWorksheetDownCommand { get; }

    public IRelayCommand ResetColumnLayoutCommand { get; }

    public IRelayCommand ResetHeadersCommand { get; }

    public IRelayCommand ResetExportCommand { get; }

    public IAsyncRelayCommand PreviousReviewPageCommand { get; }

    public IAsyncRelayCommand NextReviewPageCommand { get; }

    public IAsyncRelayCommand PrepareForExportCommand { get; }

    public IAsyncRelayCommand<DatabaseReviewRowPresentation> SetRowIncludedCommand { get; }

    public IAsyncRelayCommand IncludeVisibleRowsCommand { get; }

    public IAsyncRelayCommand ExcludeVisibleRowsCommand { get; }

    public DatabaseDatasetPresentation? SelectedDataset
    {
        get => _selectedDataset;
        set
        {
            if (!SetProperty(ref _selectedDataset, value) || value is null || _publishedGeneration is null)
            {
                return;
            }
            ApplyDataset(value.Summary);
            ApplyExportDataset(value.Summary);
            OnPropertyChanged(nameof(RecordCountText));
            _ = LoadReviewPageAsync(_publishedGeneration.OperationId, 1);
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
                _ = LoadReviewPageAsync(generationId, 1);
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
                _ = LoadReviewPageAsync(generationId, 1);
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
            "Reading a bounded page from the active published Database.",
        _ when _reviewFailureDescription is not null => _reviewFailureDescription,
        WorkflowArtifactStatus.Stale =>
            "The retained published Database remains available for review but is out of date.",
        WorkflowArtifactStatus.Current =>
            "The active published Database has no values on this review page.",
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

    public string ReviewPageText
    {
        get
        {
            if (_reviewPage is null)
            {
                return "Page 0 of 0";
            }

            var pageNumber = ((_reviewPage.StartRowOrdinal - 1)
                / DatabaseReviewLimits.MaximumRowsPerPage) + 1;
            var pageCount = Math.Max(
                1,
                (int)Math.Ceiling(
                    _reviewPage.TotalPresentationRowCount
                    / (double)DatabaseReviewLimits.MaximumRowsPerPage));
            return $"Page {pageNumber:N0} of {pageCount:N0}";
        }
    }

    public string ColumnCountText => $"{VisibleColumns.Count} / {Columns.Count} columns";

    public string ExportFieldCountText =>
        $"{ExportColumns.Count(column => column.IsExported)} / {ExportColumns.Count} fields";

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

    public bool IsExportAvailable => false;

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

    public string OutputFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "CIA",
        "Exports");

    public bool AlwaysAskWhereToExport { get; set; } = true;

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
    }

    private bool CanMoveExportFieldUp(ExportFieldPresentation? column)
    {
        return column is not null && _exportColumns.IndexOf(column) > 0;
    }

    private bool CanMoveExportFieldDown(ExportFieldPresentation? column)
    {
        return column is not null
            && _exportColumns.IndexOf(column) is var index
            && index >= 0
            && index < _exportColumns.Count - 1;
    }

    private void MoveExportFieldUp(ExportFieldPresentation? column)
    {
        MoveExportField(column, -1);
    }

    private void MoveExportFieldDown(ExportFieldPresentation? column)
    {
        MoveExportField(column, 1);
    }

    private void MoveExportField(ExportFieldPresentation? column, int offset)
    {
        if (column is null)
        {
            return;
        }

        var currentIndex = _exportColumns.IndexOf(column);
        var nextIndex = currentIndex + offset;
        if (currentIndex < 0 || nextIndex < 0 || nextIndex >= _exportColumns.Count)
        {
            return;
        }

        SelectedExportSet?.MoveField(column, offset);
    }

    private void ResetColumnLayout()
    {
        foreach (var column in _columns)
        {
            column.ResetPresentation();
        }

        ReorderColumns(_publishedColumnOrder);
        UpdatePositionsAndPresentation();
    }

    private void ResetHeaders()
    {
        SelectedExportSet?.ResetHeaders();
    }

    private void ResetExport()
    {
        SelectedExportSet?.ResetFields();
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
        ResetExportCommand.NotifyCanExecuteChanged();
    }

    private void RebuildVisibleColumns()
    {
        _visibleColumns.Clear();
        foreach (var column in _columns.Where(column => column.IsVisible))
        {
            _visibleColumns.Add(column);
        }

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
    }

    private void OnExportRoutingChanged(object? sender, EventArgs e)
    {
        DispatchToUi(() =>
        {
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
        MoveExportFieldUpCommand.NotifyCanExecuteChanged();
        MoveExportFieldDownCommand.NotifyCanExecuteChanged();
        ResetHeadersCommand.NotifyCanExecuteChanged();
        ResetExportCommand.NotifyCanExecuteChanged();
    }

    private void OnColumnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DatabaseColumnPresentation.IsVisible))
        {
            RebuildVisibleColumns();
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
        });
    }

    private void OnPublishedGenerationChanged(
        object? sender,
        DatabaseGenerationSummary generation)
    {
        DispatchToUi(() =>
        {
            ApplyPublishedGeneration(generation);
            NotifyExtractionReviewChanged();
        });
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
        _reviewPage = null;
        _reviewFailureDescription = null;
        _records.Clear();
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
            || _synchronizingMetadataVisibility)
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
    }

    private void RebuildVisibleMetadataColumns()
    {
        _visibleMetadataFields.Clear();
        foreach (var field in _metadataFields.Where(field => field.IsVisible))
        {
            _visibleMetadataFields.Add(field);
        }
        OnPropertyChanged(nameof(VisibleMetadataFields));
        if (_reviewPage is not null)
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
        _exportColumns.Clear();
        _exportMetadataFields.Clear();
        var set = _exportRouting.SourceSets.SingleOrDefault(candidate =>
            candidate.SourceSetId == dataset.SourceSetId);
        if (set is not null)
        {
            foreach (var field in set.Fields)
            {
                _exportColumns.Add(field);
            }
            foreach (var field in set.MetadataFields)
            {
                _exportMetadataFields.Add(field);
            }
        }

        NotifyExportRoutingPropertiesChanged();
    }

    private async Task PreviousReviewPageAsync()
    {
        if (_publishedGenerationId is not { } generationId || _reviewPage is null)
        {
            return;
        }

        var startRowOrdinal = Math.Max(
            1,
            _reviewPage.StartRowOrdinal - DatabaseReviewLimits.MaximumRowsPerPage);
        await LoadReviewPageAsync(generationId, startRowOrdinal).ConfigureAwait(false);
    }

    private async Task NextReviewPageAsync()
    {
        if (_publishedGenerationId is not { } generationId || _reviewPage is null)
        {
            return;
        }

        var startRowOrdinal = checked(
            _reviewPage.StartRowOrdinal + DatabaseReviewLimits.MaximumRowsPerPage);
        await LoadReviewPageAsync(generationId, startRowOrdinal).ConfigureAwait(false);
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

    private bool CanMoveToPreviousReviewPage()
    {
        return !IsReviewLoading && _reviewPage?.StartRowOrdinal > 1;
    }

    private bool CanMoveToNextReviewPage()
    {
        return !IsReviewLoading
            && _reviewPage is { } page
            && page.StartRowOrdinal + DatabaseReviewLimits.MaximumRowsPerPage
                <= page.TotalPresentationRowCount;
    }

    private async Task LoadReviewPageAsync(
        OperationId generationId,
        int startRowOrdinal)
    {
        if (_databaseReviewClient is null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(
            ref _reviewCancellation,
            cancellation);
        previousCancellation?.Cancel();
        DispatchToUi(() => IsReviewLoading = true);

        try
        {
            var result = await _databaseReviewClient.ReadPageAsync(
                    new DatabaseReviewQuery(
                        generationId,
                        SelectedDataset?.Summary.SourceSetId
                            ?? throw new InvalidOperationException("A Database dataset must be selected."),
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
                if (_publishedGenerationId != generationId)
                {
                    return;
                }

                if (!result.Accepted
                    || result.Page is null
                    || result.Page.GenerationId != generationId
                    || !PageMatchesPublishedColumns(result.Page))
                {
                    _reviewPage = null;
                    _records.Clear();
                    _reviewFailureDescription = result.FailureDescription
                        ?? "The active published Database could not provide this review page.";
                }
                else
                {
                    _reviewPage = result.Page;
                    _reviewFailureDescription = null;
                    RebuildReviewRows();
                }

                IsReviewLoading = false;
                NotifyReviewChanged();
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _reviewCancellation,
                null,
                cancellation);
            cancellation.Dispose();
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
        _records.Clear();
        if (_reviewPage is null || _visibleColumns.Count == 0)
        {
            NotifyReviewChanged();
            return;
        }

        foreach (var row in _reviewPage.Rows)
        {
            var cells = _visibleColumns.Select(column =>
            {
                var cell = row.Cells.SingleOrDefault(value =>
                    string.Equals(CreateColumnIdentity(value.ColumnIdentity), column.MappingIdentity, StringComparison.Ordinal));
                return new DatabaseReviewCellPresentation(column, cell);
            }).ToArray();
            _records.Add(new DatabaseReviewRowPresentation(
                row.Ordinal,
                row.IsIncluded,
                row.RecordIdentity,
                row.Source,
                row.HasConflict,
                _visibleMetadataFields.Select(field => new DatabaseMetadataCellPresentation(
                    field.Field,
                    field.DisplayName,
                    GetMetadataValue(row, field.Field))).ToArray(),
                cells));
        }

        NotifyReviewChanged();
    }

    private void NotifyReviewChanged()
    {
        OnPropertyChanged(nameof(HasReviewRows));
        OnPropertyChanged(nameof(ReviewPageText));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDetail));
        PreviousReviewPageCommand.NotifyCanExecuteChanged();
        NextReviewPageCommand.NotifyCanExecuteChanged();
        NotifyRowInclusionCommandsChanged();
    }

    private void NotifyExtractionReviewChanged()
    {
        OnPropertyChanged(nameof(ExtractionReviewState));
        OnPropertyChanged(nameof(ExtractionReviewStateText));
        OnPropertyChanged(nameof(ExtractionReviewContext));
        OnPropertyChanged(nameof(ExtractionBasisMatchesReviewedDatabase));
        PrepareForExportCommand.NotifyCanExecuteChanged();
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

    private static string GetMetadataValue(DatabaseReviewRow row, DatabaseMetadataField field)
    {
        var values = row.Cells.SelectMany(cell => cell.Values).ToArray();
        return field switch
        {
            DatabaseMetadataField.SourceSet => row.Source.SourceSetName,
            DatabaseMetadataField.SourceFile => row.Source.SourceFileName,
            DatabaseMetadataField.FullSourcePath => row.Source.FullSourcePath,
            DatabaseMetadataField.SourceId => row.Source.SourceId.ToString(),
            DatabaseMetadataField.FileModified => row.Source.FileModifiedUtc?.ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss") ?? "Unavailable",
            DatabaseMetadataField.SourceKind => row.Source.SourceKind.ToString(),
            DatabaseMetadataField.ContainerProvenance => string.Join(
                " | ",
                new[] { row.Source.ArchivePath, row.Source.ArchiveMemberPath }
                    .Where(value => !string.IsNullOrWhiteSpace(value))),
            DatabaseMetadataField.RecordHierarchy => row.RecordHierarchy,
            DatabaseMetadataField.ValuePath => JoinMetadata(values.Select(value => value.Lineage.StructuralPath)),
            DatabaseMetadataField.TraversalOrdinal => JoinMetadata(values.Select(value =>
                value.Lineage.TraversalOrder.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            DatabaseMetadataField.CandidateKind => JoinMetadata(values.Select(value =>
                value.DetailedIdentity.CandidateKind.ToString())),
            DatabaseMetadataField.StructuralIdentity => JoinMetadata(values.Select(value =>
                value.DetailedIdentity.StructuralIdentity)),
            _ => string.Empty
        };
    }

    private static string JoinMetadata(IEnumerable<string> values) => string.Join(
        " | ",
        values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));

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
        SetRowsIncludedAsync(_records.ToArray(), included);

    private async Task SetRowsIncludedAsync(
        IReadOnlyList<DatabaseReviewRowPresentation> rows,
        bool included)
    {
        var rowsToChange = rows.Where(row => row.IsIncluded != included).ToArray();
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
            await LoadReviewPageAsync(generationId, _reviewPage?.StartRowOrdinal ?? 1)
                .ConfigureAwait(false);
        }
    }

    private bool CanSetRowIncluded(DatabaseReviewRowPresentation? row) =>
        row is not null && CanChangeRowInclusion();

    private bool CanIncludeVisibleRows() =>
        CanChangeRowInclusion() && _records.Any(row => !row.IsIncluded);

    private bool CanExcludeVisibleRows() =>
        CanChangeRowInclusion() && _records.Any(row => row.IsIncluded);

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
    DatabaseSourceMetadata Source,
    bool HasConflict,
    IReadOnlyList<DatabaseMetadataCellPresentation> MetadataCells,
    IReadOnlyList<DatabaseReviewCellPresentation> Cells);

public sealed record DatabaseMetadataCellPresentation(
    DatabaseMetadataField Field,
    string DisplayName,
    string Value);

public sealed class DatabaseMetadataFieldPresentation : ObservableObject
{
    private bool _isVisible;

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
    private const double MinimumWidth = 60;
    private const double MaximumWidth = 500;
    private string _databaseField;
    private bool _isVisible = true;
    private double _width = DefaultWidth;
    private int _position;

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

    internal void ResetPresentation()
    {
        IsVisible = true;
        Width = DefaultWidth;
    }

}
