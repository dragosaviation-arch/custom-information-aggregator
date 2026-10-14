using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Extraction;
using CIA.Contracts.Hierarchy;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Export;

[JsonConverter(typeof(WorkbookDefinitionIdJsonConverter))]
public readonly record struct WorkbookDefinitionId
{
    private WorkbookDefinitionId(Guid value) => Value = value;

    public Guid Value { get; }

    public static WorkbookDefinitionId CreateNew() => new(Guid.CreateVersion7());

    public static WorkbookDefinitionId From(Guid value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                "Workbook definition IDs must be non-empty UUIDv7 values.",
                nameof(value));
        }

        return new WorkbookDefinitionId(value);
    }

    public override string ToString() => Value.ToString("D");

    internal static bool IsValid(Guid value) => value != Guid.Empty && value.Version == 7;
}

[JsonConverter(typeof(WorksheetDefinitionIdJsonConverter))]
public readonly record struct WorksheetDefinitionId
{
    private WorksheetDefinitionId(Guid value) => Value = value;

    public Guid Value { get; }

    public static WorksheetDefinitionId CreateNew() => new(Guid.CreateVersion7());

    public static WorksheetDefinitionId From(Guid value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                "Worksheet definition IDs must be non-empty UUIDv7 values.",
                nameof(value));
        }

        return new WorksheetDefinitionId(value);
    }

    public override string ToString() => Value.ToString("D");

    internal static bool IsValid(Guid value) => value != Guid.Empty && value.Version == 7;
}

public enum ExportOutputColumnKind
{
    Value = 1,
    SourceId = 2,
    Metadata = 3
}

public sealed record ExportFieldConfiguration
{
    [JsonConstructor]
    public ExportFieldConfiguration(
        DatabaseColumnIdentity databaseColumnIdentity,
        bool isValueIncluded,
        string excelHeader,
        bool isSourceIdCompanionIncluded)
    {
        ArgumentNullException.ThrowIfNull(databaseColumnIdentity);
        ArgumentNullException.ThrowIfNull(excelHeader);
        if (isSourceIdCompanionIncluded && !isValueIncluded)
        {
            throw new ArgumentException(
                "A SourceId companion cannot be included without its owning value field.",
                nameof(isSourceIdCompanionIncluded));
        }

        DatabaseColumnIdentity = databaseColumnIdentity;
        IsValueIncluded = isValueIncluded;
        ExcelHeader = excelHeader;
        IsSourceIdCompanionIncluded = isSourceIdCompanionIncluded;
    }

    public DatabaseColumnIdentity DatabaseColumnIdentity { get; }

    public bool IsValueIncluded { get; }

    public string ExcelHeader { get; }

    public bool IsSourceIdCompanionIncluded { get; }

    public string SourceIdCompanionHeader => $"{ExcelHeader} SourceId";
}

public sealed record ExportMetadataFieldConfiguration
{
    [JsonConstructor]
    public ExportMetadataFieldConfiguration(
        DatabaseMetadataField metadataField,
        bool isIncluded,
        string excelHeader)
    {
        if (!Enum.IsDefined(metadataField))
        {
            throw new ArgumentOutOfRangeException(nameof(metadataField));
        }

        ArgumentNullException.ThrowIfNull(excelHeader);
        MetadataField = metadataField;
        IsIncluded = isIncluded;
        ExcelHeader = excelHeader;
    }

    public DatabaseMetadataField MetadataField { get; }

    public bool IsIncluded { get; }

    public string ExcelHeader { get; }
}

public sealed record SourceSetExportConfiguration
{
    public SourceSetExportConfiguration(
        OutputSetId outputSetId,
        SourceSetId parentSourceSetId,
        RowFamilyId rowFamilyId,
        bool isEnabled,
        WorksheetDefinitionId? worksheetDefinitionId,
        IReadOnlyList<ExportFieldConfiguration> fields,
        IReadOnlyList<ExportMetadataFieldConfiguration> metadataFields)
        : this(
            parentSourceSetId,
            rowFamilyId,
            isEnabled,
            worksheetDefinitionId,
            fields,
            metadataFields,
            outputSetId)
    {
    }

    public SourceSetExportConfiguration(
        SourceSetId sourceSetId,
        bool isEnabled,
        WorksheetDefinitionId? worksheetDefinitionId,
        IReadOnlyList<ExportFieldConfiguration> fields,
        IReadOnlyList<ExportMetadataFieldConfiguration> metadataFields)
        : this(
            sourceSetId,
            new RowFamilyId(sourceSetId, RowFamilyKind.Root, "/legacy"),
            isEnabled,
            worksheetDefinitionId,
            fields,
            metadataFields)
    {
    }

    [JsonConstructor]
    public SourceSetExportConfiguration(
        SourceSetId sourceSetId,
        RowFamilyId rowFamilyId,
        bool isEnabled,
        WorksheetDefinitionId? worksheetDefinitionId,
        IReadOnlyList<ExportFieldConfiguration> fields,
        IReadOnlyList<ExportMetadataFieldConfiguration> metadataFields,
        OutputSetId? outputSetId = null)
    {
        if (!SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException(
                "A Source Set export configuration requires a Source Set ID.",
                nameof(sourceSetId));
        }

        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(metadataFields);
        var fieldArray = fields.ToArray();
        var metadataArray = metadataFields.ToArray();
        if (fieldArray.Any(field => field is null)
            || fieldArray.Select(field => field.DatabaseColumnIdentity).Distinct().Count()
                != fieldArray.Length
            || metadataArray.Any(field => field is null)
            || metadataArray.Select(field => field.MetadataField).Distinct().Count()
                != metadataArray.Length)
        {
            throw new ArgumentException(
                "A Source Set export configuration contains duplicate or null fields.");
        }

        if (rowFamilyId.SourceSetId != sourceSetId)
        {
            throw new ArgumentException(
                "A Row Family export configuration must belong to its Source Set.",
                nameof(rowFamilyId));
        }

        if (outputSetId is { } id && id.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "An Output Set export configuration requires an identity.",
                nameof(outputSetId));
        }

        SourceSetId = sourceSetId;
        OutputSetId = outputSetId;
        RowFamilyId = rowFamilyId;
        IsEnabled = isEnabled;
        WorksheetDefinitionId = worksheetDefinitionId;
        Fields = new ReadOnlyCollection<ExportFieldConfiguration>(fieldArray);
        MetadataFields = new ReadOnlyCollection<ExportMetadataFieldConfiguration>(metadataArray);
    }

    public SourceSetId SourceSetId { get; }

    public OutputSetId? OutputSetId { get; }

    [JsonIgnore]
    public SourceSetId ParentSourceSetId => SourceSetId;

    public RowFamilyId RowFamilyId { get; }

    public bool IsEnabled { get; }

    public WorksheetDefinitionId? WorksheetDefinitionId { get; }

    public IReadOnlyList<ExportFieldConfiguration> Fields { get; }

    public IReadOnlyList<ExportMetadataFieldConfiguration> MetadataFields { get; }
}

public sealed record WorksheetDefinition
{
    public WorksheetDefinition(
        WorksheetDefinitionId worksheetDefinitionId,
        WorkbookDefinitionId workbookDefinitionId,
        OutputSetId outputSetId,
        SourceSetId parentSourceSetId,
        RowFamilyId rowFamilyId,
        string name,
        int order)
        : this(
            worksheetDefinitionId,
            workbookDefinitionId,
            parentSourceSetId,
            rowFamilyId,
            name,
            order,
            outputSetId)
    {
    }

    public WorksheetDefinition(
        WorksheetDefinitionId worksheetDefinitionId,
        WorkbookDefinitionId workbookDefinitionId,
        SourceSetId sourceSetId,
        string name,
        int order)
        : this(
            worksheetDefinitionId,
            workbookDefinitionId,
            sourceSetId,
            new RowFamilyId(sourceSetId, RowFamilyKind.Root, "/legacy"),
            name,
            order)
    {
    }

    [JsonConstructor]
    public WorksheetDefinition(
        WorksheetDefinitionId worksheetDefinitionId,
        WorkbookDefinitionId workbookDefinitionId,
        SourceSetId sourceSetId,
        RowFamilyId rowFamilyId,
        string name,
        int order,
        OutputSetId? outputSetId = null)
    {
        if (!WorksheetDefinitionId.IsValid(worksheetDefinitionId.Value)
            || !WorkbookDefinitionId.IsValid(workbookDefinitionId.Value)
            || !SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException(
                "A worksheet definition requires stable worksheet, workbook, and Source Set identities.");
        }

        if (rowFamilyId.SourceSetId != sourceSetId)
        {
            throw new ArgumentException(
                "A worksheet Row Family must belong to its Source Set.",
                nameof(rowFamilyId));
        }

        if (outputSetId is { } id && id.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "An Output Set worksheet requires an identity.",
                nameof(outputSetId));
        }

        ArgumentNullException.ThrowIfNull(name);
        WorksheetDefinitionId = worksheetDefinitionId;
        WorkbookDefinitionId = workbookDefinitionId;
        SourceSetId = sourceSetId;
        OutputSetId = outputSetId;
        RowFamilyId = rowFamilyId;
        Name = name;
        Order = order;
    }

    public WorksheetDefinitionId WorksheetDefinitionId { get; }

    public WorkbookDefinitionId WorkbookDefinitionId { get; }

    public SourceSetId SourceSetId { get; }

    public OutputSetId? OutputSetId { get; }

    [JsonIgnore]
    public SourceSetId ParentSourceSetId => SourceSetId;

    public RowFamilyId RowFamilyId { get; }

    public string Name { get; }

    public int Order { get; }
}

public sealed record WorkbookDefinition
{
    [JsonConstructor]
    public WorkbookDefinition(
        WorkbookDefinitionId workbookDefinitionId,
        string fileName,
        int order,
        IReadOnlyList<WorksheetDefinition> worksheets)
    {
        if (!WorkbookDefinitionId.IsValid(workbookDefinitionId.Value))
        {
            throw new ArgumentException(
                "A workbook definition requires a stable identity.",
                nameof(workbookDefinitionId));
        }

        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(worksheets);
        var worksheetArray = worksheets.ToArray();
        if (worksheetArray.Any(worksheet => worksheet is null)
            || worksheetArray.Select(worksheet => worksheet.WorksheetDefinitionId)
                .Distinct().Count() != worksheetArray.Length)
        {
            throw new ArgumentException(
                "A workbook definition contains duplicate or null worksheets.",
                nameof(worksheets));
        }

        WorkbookDefinitionId = workbookDefinitionId;
        FileName = fileName;
        Order = order;
        Worksheets = new ReadOnlyCollection<WorksheetDefinition>(worksheetArray);
    }

    public WorkbookDefinitionId WorkbookDefinitionId { get; }

    public string FileName { get; }

    public int Order { get; }

    public IReadOnlyList<WorksheetDefinition> Worksheets { get; }
}

public sealed record ExportOutputColumn
{
    internal ExportOutputColumn(
        DatabaseColumnIdentity? owningDatabaseColumnIdentity,
        DatabaseMetadataField? metadataField,
        string header,
        ExportOutputColumnKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(header);
        if (!Enum.IsDefined(kind)
            || kind == ExportOutputColumnKind.Metadata && metadataField is null
            || kind != ExportOutputColumnKind.Metadata && owningDatabaseColumnIdentity is null)
        {
            throw new ArgumentException("An export output column identity is invalid.");
        }

        OwningDatabaseColumnIdentity = owningDatabaseColumnIdentity;
        MetadataField = metadataField;
        Header = header;
        Kind = kind;
    }

    public DatabaseColumnIdentity? OwningDatabaseColumnIdentity { get; }

    public DatabaseMetadataField? MetadataField { get; }

    public string Header { get; }

    public ExportOutputColumnKind Kind { get; }
}

public sealed record ExportConfigurationSnapshot
{
    [JsonConstructor]
    public ExportConfigurationSnapshot(
        IReadOnlyList<WorkbookDefinition> workbooks,
        IReadOnlyList<SourceSetExportConfiguration> sourceSets)
    {
        ArgumentNullException.ThrowIfNull(workbooks);
        ArgumentNullException.ThrowIfNull(sourceSets);
        var workbookArray = workbooks.ToArray();
        var sourceSetArray = sourceSets.ToArray();
        if (workbookArray.Any(workbook => workbook is null)
            || workbookArray.Select(workbook => workbook.WorkbookDefinitionId)
                .Distinct().Count() != workbookArray.Length
            || sourceSetArray.Any(sourceSet => sourceSet is null)
            || sourceSetArray.Select(sourceSet => sourceSet.OutputSetId is { } outputSetId
                    ? $"output:{outputSetId}"
                    : $"family:{sourceSet.RowFamilyId}")
                .Distinct().Count() != sourceSetArray.Length)
        {
            throw new ArgumentException(
                "Export configuration identities must be unique and non-null.");
        }

        Workbooks = new ReadOnlyCollection<WorkbookDefinition>(workbookArray);
        SourceSets = new ReadOnlyCollection<SourceSetExportConfiguration>(sourceSetArray);
    }

    public IReadOnlyList<WorkbookDefinition> Workbooks { get; }

    public IReadOnlyList<SourceSetExportConfiguration> SourceSets { get; }

    public IReadOnlyList<ExportOutputColumn> CreateIncludedOutputColumns(OutputSetId outputSetId)
    {
        var configuration = SourceSets.Single(set => set.OutputSetId == outputSetId);
        return CreateIncludedOutputColumns(configuration);
    }

    public IReadOnlyList<ExportOutputColumn> CreateIncludedOutputColumns(RowFamilyId rowFamilyId)
    {
        var configuration = SourceSets.Single(set => set.RowFamilyId == rowFamilyId);
        return CreateIncludedOutputColumns(configuration);
    }

    private static IReadOnlyList<ExportOutputColumn> CreateIncludedOutputColumns(
        SourceSetExportConfiguration configuration)
    {
        var columns = new List<ExportOutputColumn>();
        foreach (var field in configuration.Fields.Where(field => field.IsValueIncluded))
        {
            columns.Add(new ExportOutputColumn(
                field.DatabaseColumnIdentity,
                metadataField: null,
                field.ExcelHeader,
                ExportOutputColumnKind.Value));
            if (field.IsSourceIdCompanionIncluded)
            {
                columns.Add(new ExportOutputColumn(
                    field.DatabaseColumnIdentity,
                    metadataField: null,
                    field.SourceIdCompanionHeader,
                    ExportOutputColumnKind.SourceId));
            }
        }

        columns.AddRange(configuration.MetadataFields
            .Where(field => field.IsIncluded)
            .Select(field => new ExportOutputColumn(
                owningDatabaseColumnIdentity: null,
                field.MetadataField,
                field.ExcelHeader,
                ExportOutputColumnKind.Metadata)));
        return new ReadOnlyCollection<ExportOutputColumn>(columns);
    }

    public int IncludedOutputColumnCount => SourceSets
        .Where(sourceSet => sourceSet.IsEnabled)
        .Sum(sourceSet => CreateIncludedOutputColumns(sourceSet).Count);
}

public sealed record ExportConfigurationValidationFailure(
    string Code,
    string Description,
    SourceSetId? SourceSetId = null,
    WorkbookDefinitionId? WorkbookDefinitionId = null,
    WorksheetDefinitionId? WorksheetDefinitionId = null,
    RowFamilyId? RowFamilyId = null,
    OutputSetId? OutputSetId = null);

public sealed record RunnableWorksheetDefinition(
    WorksheetDefinition Worksheet,
    SourceSetExportConfiguration SourceSetConfiguration);

public sealed record RunnableWorkbookDefinition(
    WorkbookDefinition Workbook,
    IReadOnlyList<RunnableWorksheetDefinition> Worksheets);

public sealed record ExportConfigurationValidationResult(
    IReadOnlyList<ExportConfigurationValidationFailure> Failures,
    IReadOnlyList<RunnableWorkbookDefinition> RunnableWorkbooks)
{
    public bool IsValid => Failures.Count == 0;
}

public static class ExportConfigurationValidator
{
    private static readonly char[] InvalidWorkbookFileNameCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly char[] InvalidWorksheetNameCharacters =
        [':', '\\', '/', '?', '*', '[', ']'];

    public static ExportConfigurationValidationResult Validate(
        ExportConfigurationSnapshot configuration,
        ExtractionResultSummary extractionResult)
    {
        ArgumentNullException.ThrowIfNull(extractionResult);
        return Validate(
            configuration,
            extractionResult.Datasets.SelectMany(dataset => dataset.RowFamilies
                .Select(family => new DatasetShape(
                    dataset.OutputSetId,
                    dataset.ParentSourceSetId,
                    family.Id,
                    family.RowCount,
                    family.Columns))).ToArray());
    }

    public static ExportConfigurationValidationResult Validate(
        ExportConfigurationSnapshot configuration,
        DatabaseGenerationSummary databaseGeneration)
    {
        ArgumentNullException.ThrowIfNull(databaseGeneration);
        return Validate(
            configuration,
            databaseGeneration.Datasets.SelectMany(dataset => dataset.RowFamilies
                .Select(family => new DatasetShape(
                    dataset.OutputSetId,
                    dataset.ParentSourceSetId,
                    family.Id,
                    family.RowCount,
                    family.Columns))).ToArray());
    }

    private static ExportConfigurationValidationResult Validate(
        ExportConfigurationSnapshot configuration,
        IReadOnlyList<DatasetShape> datasets)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var failures = new List<ExportConfigurationValidationFailure>();
        var workbookById = configuration.Workbooks.ToDictionary(
            workbook => workbook.WorkbookDefinitionId);
        var worksheetOwners = configuration.Workbooks
            .SelectMany(workbook => workbook.Worksheets.Select(worksheet =>
                (Workbook: workbook, Worksheet: worksheet)))
            .ToArray();
        var allWorksheets = worksheetOwners.Select(owner => owner.Worksheet).ToArray();
        var configuredDatasetKeys = configuration.SourceSets
            .Select(CreateRouteKey)
            .ToHashSet();
        var datasetKeys = datasets.Select(CreateRouteKey).ToHashSet();
        var worksheetById = allWorksheets
            .GroupBy(worksheet => worksheet.WorksheetDefinitionId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        if (!configuredDatasetKeys.SetEquals(datasetKeys))
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "dataset-configuration-mismatch",
                "Export configuration must explicitly include every published Output Set or legacy Row Family."));
        }

        foreach (var outputSet in datasets.Where(dataset => dataset.OutputSetId is not null)
                     .GroupBy(dataset => dataset.OutputSetId!.Value)
                     .Where(group => group.Count() != 1))
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "output-set-family-count",
                "An exportable Output Set must provide exactly one tabular Row Family.",
                OutputSetId: outputSet.Key));
        }

        foreach (var owner in worksheetOwners.Where(owner =>
                     owner.Worksheet.WorkbookDefinitionId != owner.Workbook.WorkbookDefinitionId))
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "worksheet-workbook-mismatch",
                "A worksheet references a different owning workbook.",
                owner.Worksheet.SourceSetId,
                owner.Worksheet.WorkbookDefinitionId,
                owner.Worksheet.WorksheetDefinitionId));
        }

        foreach (var group in worksheetById.Where(group => group.Value.Length != 1))
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "duplicate-worksheet-identity",
                "A worksheet identity appears more than once.",
                WorksheetDefinitionId: group.Key));
        }

        foreach (var group in allWorksheets.GroupBy(CreateRouteKey)
                     .Where(group => group.Count() != 1))
        {
            var first = group.First();
            failures.Add(new ExportConfigurationValidationFailure(
                first.OutputSetId is null ? "row-family-route-count" : "output-set-route-count",
                "An Output Set or legacy Row Family must own exactly one worksheet route.",
                first.SourceSetId,
                RowFamilyId: first.RowFamilyId,
                OutputSetId: first.OutputSetId));
        }

        foreach (var worksheet in allWorksheets.Where(worksheet =>
                     !configuredDatasetKeys.Contains(CreateRouteKey(worksheet))))
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "orphaned-worksheet-route",
                "A worksheet route references an Output Set or Row Family without export configuration.",
                worksheet.SourceSetId,
                worksheet.WorkbookDefinitionId,
                worksheet.WorksheetDefinitionId,
                worksheet.RowFamilyId,
                worksheet.OutputSetId));
        }

        if (configuration.Workbooks.Any(workbook => workbook.Order < 1)
            || configuration.Workbooks.Select(workbook => workbook.Order).Distinct().Count()
                != configuration.Workbooks.Count)
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "invalid-workbook-order",
                "Workbook presentation order must be positive and unique."));
        }

        var enabledRoutes = new List<(SourceSetExportConfiguration Set, WorkbookDefinition Workbook, WorksheetDefinition Worksheet)>();
        foreach (var setConfiguration in configuration.SourceSets)
        {
            var matchingDatasets = datasets.Where(dataset =>
                CreateRouteKey(dataset) == CreateRouteKey(setConfiguration)).ToArray();
            var dataset = matchingDatasets.Length == 1 ? matchingDatasets[0] : null;
            if (!setConfiguration.IsEnabled)
            {
                continue;
            }

            if (dataset is null)
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "missing-extraction-dataset",
                    "The enabled Output Set does not exist in the Extraction Result.",
                    setConfiguration.SourceSetId,
                    RowFamilyId: setConfiguration.RowFamilyId,
                    OutputSetId: setConfiguration.OutputSetId));
            }

            else if (dataset.ParentSourceSetId != setConfiguration.ParentSourceSetId
                     || dataset.RowFamilyId != setConfiguration.RowFamilyId)
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "output-set-identity-mismatch",
                    "The Output Set route must retain its parent Source Set and Row Family identities.",
                    setConfiguration.SourceSetId,
                    RowFamilyId: setConfiguration.RowFamilyId,
                    OutputSetId: setConfiguration.OutputSetId));
            }

            else if (dataset.RowCount > ExcelWorkbookLimits.MaximumDataRows)
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "export-row-limit-exceeded",
                    "The enabled Output Set exceeds Excel's worksheet row limit.",
                    setConfiguration.SourceSetId,
                    RowFamilyId: setConfiguration.RowFamilyId,
                    OutputSetId: setConfiguration.OutputSetId));
            }

            if (setConfiguration.WorksheetDefinitionId is not { } worksheetId
                || !worksheetById.TryGetValue(worksheetId, out var matchingWorksheets)
                || matchingWorksheets.Length != 1)
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "missing-worksheet-route",
                    "The enabled Output Set requires exactly one worksheet route.",
                    setConfiguration.SourceSetId,
                    RowFamilyId: setConfiguration.RowFamilyId,
                    OutputSetId: setConfiguration.OutputSetId));
                continue;
            }

            var worksheet = matchingWorksheets[0];
            if (CreateRouteKey(worksheet) != CreateRouteKey(setConfiguration)
                || worksheet.SourceSetId != setConfiguration.SourceSetId
                || worksheet.RowFamilyId != setConfiguration.RowFamilyId)
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "shared-worksheet-route",
                    "A worksheet cannot be shared by multiple Output Sets.",
                    setConfiguration.SourceSetId,
                    worksheet.WorkbookDefinitionId,
                    worksheet.WorksheetDefinitionId,
                    setConfiguration.RowFamilyId,
                    setConfiguration.OutputSetId));
                continue;
            }

            if (!workbookById.TryGetValue(worksheet.WorkbookDefinitionId, out var workbook))
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "missing-workbook-route",
                    "The enabled Row Family worksheet requires an existing workbook.",
                    setConfiguration.SourceSetId,
                    worksheet.WorkbookDefinitionId,
                    worksheet.WorksheetDefinitionId,
                    setConfiguration.RowFamilyId,
                    setConfiguration.OutputSetId));
                continue;
            }

            enabledRoutes.Add((setConfiguration, workbook, worksheet));
            ValidateFields(setConfiguration, dataset, failures);
        }

        foreach (var workbook in configuration.Workbooks)
        {
            if (!IsValidWorkbookFileName(workbook.FileName))
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "invalid-workbook-filename",
                    $"Workbook '{workbook.FileName}' requires a valid Windows filename.",
                    WorkbookDefinitionId: workbook.WorkbookDefinitionId));
            }
        }

        foreach (var group in configuration.Workbooks
                     .GroupBy(workbook => workbook.FileName, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "duplicate-workbook-filename",
                $"Workbook filename '{group.Key}' is duplicated case-insensitively."));
        }

        foreach (var route in enabledRoutes)
        {
            if (!IsValidWorksheetName(route.Worksheet.Name))
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "invalid-worksheet-name",
                    $"Worksheet '{route.Worksheet.Name}' is not a valid Excel worksheet name.",
                    route.Set.SourceSetId,
                    route.Workbook.WorkbookDefinitionId,
                    route.Worksheet.WorksheetDefinitionId));
            }

            if (route.Worksheet.Order < 1)
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "invalid-worksheet-order",
                    "Worksheet order must be positive.",
                    route.Set.SourceSetId,
                    route.Workbook.WorkbookDefinitionId,
                    route.Worksheet.WorksheetDefinitionId));
            }
        }

        foreach (var workbookRoutes in enabledRoutes.GroupBy(route => route.Workbook.WorkbookDefinitionId))
        {
            foreach (var duplicateName in workbookRoutes
                         .GroupBy(route => route.Worksheet.Name, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "duplicate-worksheet-name",
                    $"Worksheet name '{duplicateName.Key}' is duplicated in one workbook.",
                    WorkbookDefinitionId: workbookRoutes.Key));
            }

            if (workbookRoutes.Select(route => route.Worksheet.Order).Distinct().Count()
                != workbookRoutes.Count())
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "duplicate-worksheet-order",
                    "Worksheet order must be unique within a workbook.",
                    WorkbookDefinitionId: workbookRoutes.Key));
            }
        }

        var runnableWorkbooks = enabledRoutes
            .GroupBy(route => route.Workbook)
            .OrderBy(group => group.Key.Order)
            .Select(group => new RunnableWorkbookDefinition(
                group.Key,
                group.OrderBy(route => route.Worksheet.Order)
                    .Select(route => new RunnableWorksheetDefinition(
                        route.Worksheet,
                        route.Set))
                    .ToArray()))
            .ToArray();
        return new ExportConfigurationValidationResult(
            new ReadOnlyCollection<ExportConfigurationValidationFailure>(failures),
            new ReadOnlyCollection<RunnableWorkbookDefinition>(runnableWorkbooks));
    }

    public static bool IsValidWorkbookFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(Path.GetExtension(fileName), ".xlsx", StringComparison.OrdinalIgnoreCase)
            || fileName.Any(character => character < 32
                || InvalidWorkbookFileNameCharacters.Contains(character))
            || fileName.EndsWith(' ')
            || fileName.EndsWith('.'))
        {
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return false;
        }

        var reserved = baseName.ToUpperInvariant();
        return reserved is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(reserved.Length == 4
                && (reserved.StartsWith("COM", StringComparison.Ordinal)
                    || reserved.StartsWith("LPT", StringComparison.Ordinal))
                && reserved[3] is >= '1' and <= '9');
    }

    public static bool IsValidWorksheetName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 31
        && !name.StartsWith('\'')
        && !name.EndsWith('\'')
        && !name.Any(InvalidWorksheetNameCharacters.Contains);

    private static void ValidateFields(
        SourceSetExportConfiguration setConfiguration,
        DatasetShape? dataset,
        ICollection<ExportConfigurationValidationFailure> failures)
    {
        if (dataset is null)
        {
            return;
        }

        var configuredIdentities = setConfiguration.Fields
            .Select(field => field.DatabaseColumnIdentity).ToHashSet();
        var datasetIdentities = dataset.Columns.Select(column => column.Identity).ToHashSet();
        if (!configuredIdentities.SetEquals(datasetIdentities)
            || setConfiguration.Fields.Any(field =>
                field.DatabaseColumnIdentity.RowFamilyId != setConfiguration.RowFamilyId))
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "database-field-mismatch",
                "Export fields must exactly match the typed Database columns for the Row Family.",
                setConfiguration.SourceSetId,
                RowFamilyId: setConfiguration.RowFamilyId,
                OutputSetId: setConfiguration.OutputSetId));
        }

        if (setConfiguration.Fields.Any(field => string.IsNullOrWhiteSpace(field.ExcelHeader)
                || field.ExcelHeader.Length > ExcelWorkbookLimits.MaximumCellTextLength)
            || setConfiguration.MetadataFields.Any(field =>
                string.IsNullOrWhiteSpace(field.ExcelHeader)
                || field.ExcelHeader.Length > ExcelWorkbookLimits.MaximumCellTextLength))
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "invalid-export-header",
                "Export field and metadata headers must be non-empty and within Excel limits.",
                setConfiguration.SourceSetId,
                OutputSetId: setConfiguration.OutputSetId));
        }

        var outputColumnCount = setConfiguration.Fields.Sum(field =>
                field.IsValueIncluded ? field.IsSourceIdCompanionIncluded ? 2 : 1 : 0)
            + setConfiguration.MetadataFields.Count(field => field.IsIncluded);
        if (outputColumnCount is < 1 or > ExcelWorkbookLimits.MaximumColumns)
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "invalid-output-column-count",
                "Each enabled Row Family requires at least one output column within Excel limits.",
                setConfiguration.SourceSetId,
                RowFamilyId: setConfiguration.RowFamilyId,
                OutputSetId: setConfiguration.OutputSetId));
        }
    }

    private static DatasetRouteKey CreateRouteKey(SourceSetExportConfiguration configuration) =>
        new(configuration.OutputSetId, configuration.OutputSetId is null
            ? configuration.RowFamilyId
            : null);

    private static DatasetRouteKey CreateRouteKey(WorksheetDefinition worksheet) =>
        new(worksheet.OutputSetId, worksheet.OutputSetId is null
            ? worksheet.RowFamilyId
            : null);

    private static DatasetRouteKey CreateRouteKey(DatasetShape dataset) =>
        new(dataset.OutputSetId, dataset.OutputSetId is null
            ? dataset.RowFamilyId
            : null);

    private readonly record struct DatasetRouteKey(
        OutputSetId? OutputSetId,
        RowFamilyId? LegacyRowFamilyId);

    private sealed record DatasetShape(
        OutputSetId? OutputSetId,
        SourceSetId ParentSourceSetId,
        RowFamilyId RowFamilyId,
        int RowCount,
        IReadOnlyList<DatabaseColumnDefinition> Columns);
}

internal sealed class WorkbookDefinitionIdJsonConverter : JsonConverter<WorkbookDefinitionId>
{
    public override WorkbookDefinitionId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var value))
        {
            throw new JsonException("A workbook definition ID must be a UUID string.");
        }

        try
        {
            return WorkbookDefinitionId.From(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("A workbook definition ID must be a UUIDv7 value.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        WorkbookDefinitionId value,
        JsonSerializerOptions options)
    {
        if (!WorkbookDefinitionId.IsValid(value.Value))
        {
            throw new JsonException("A workbook definition ID must be a UUIDv7 value.");
        }

        writer.WriteStringValue(value.Value);
    }
}

internal sealed class WorksheetDefinitionIdJsonConverter : JsonConverter<WorksheetDefinitionId>
{
    public override WorksheetDefinitionId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var value))
        {
            throw new JsonException("A worksheet definition ID must be a UUID string.");
        }

        try
        {
            return WorksheetDefinitionId.From(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("A worksheet definition ID must be a UUIDv7 value.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        WorksheetDefinitionId value,
        JsonSerializerOptions options)
    {
        if (!WorksheetDefinitionId.IsValid(value.Value))
        {
            throw new JsonException("A worksheet definition ID must be a UUIDv7 value.");
        }

        writer.WriteStringValue(value.Value);
    }
}
