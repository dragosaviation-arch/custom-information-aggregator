using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CIA.Contracts.Database;
using CIA.Contracts.Extraction;
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
    [JsonConstructor]
    public SourceSetExportConfiguration(
        SourceSetId sourceSetId,
        bool isEnabled,
        WorksheetDefinitionId? worksheetDefinitionId,
        IReadOnlyList<ExportFieldConfiguration> fields,
        IReadOnlyList<ExportMetadataFieldConfiguration> metadataFields)
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

        SourceSetId = sourceSetId;
        IsEnabled = isEnabled;
        WorksheetDefinitionId = worksheetDefinitionId;
        Fields = new ReadOnlyCollection<ExportFieldConfiguration>(fieldArray);
        MetadataFields = new ReadOnlyCollection<ExportMetadataFieldConfiguration>(metadataArray);
    }

    public SourceSetId SourceSetId { get; }

    public bool IsEnabled { get; }

    public WorksheetDefinitionId? WorksheetDefinitionId { get; }

    public IReadOnlyList<ExportFieldConfiguration> Fields { get; }

    public IReadOnlyList<ExportMetadataFieldConfiguration> MetadataFields { get; }
}

public sealed record WorksheetDefinition
{
    [JsonConstructor]
    public WorksheetDefinition(
        WorksheetDefinitionId worksheetDefinitionId,
        WorkbookDefinitionId workbookDefinitionId,
        SourceSetId sourceSetId,
        string name,
        int order)
    {
        if (!WorksheetDefinitionId.IsValid(worksheetDefinitionId.Value)
            || !WorkbookDefinitionId.IsValid(workbookDefinitionId.Value)
            || !SourceSetId.IsValid(sourceSetId.Value))
        {
            throw new ArgumentException(
                "A worksheet definition requires stable worksheet, workbook, and Source Set identities.");
        }

        ArgumentNullException.ThrowIfNull(name);
        WorksheetDefinitionId = worksheetDefinitionId;
        WorkbookDefinitionId = workbookDefinitionId;
        SourceSetId = sourceSetId;
        Name = name;
        Order = order;
    }

    public WorksheetDefinitionId WorksheetDefinitionId { get; }

    public WorkbookDefinitionId WorkbookDefinitionId { get; }

    public SourceSetId SourceSetId { get; }

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
            || sourceSetArray.Select(sourceSet => sourceSet.SourceSetId)
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

    public IReadOnlyList<ExportOutputColumn> CreateIncludedOutputColumns(SourceSetId sourceSetId)
    {
        var configuration = SourceSets.Single(set => set.SourceSetId == sourceSetId);
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
        .Sum(sourceSet => CreateIncludedOutputColumns(sourceSet.SourceSetId).Count);
}

public sealed record ExportConfigurationValidationFailure(
    string Code,
    string Description,
    SourceSetId? SourceSetId = null,
    WorkbookDefinitionId? WorkbookDefinitionId = null,
    WorksheetDefinitionId? WorksheetDefinitionId = null);

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
            extractionResult.Datasets.Select(dataset => new DatasetShape(
                dataset.SourceSetId,
                dataset.RowCount,
                dataset.Columns)).ToArray());
    }

    public static ExportConfigurationValidationResult Validate(
        ExportConfigurationSnapshot configuration,
        DatabaseGenerationSummary databaseGeneration)
    {
        ArgumentNullException.ThrowIfNull(databaseGeneration);
        return Validate(
            configuration,
            databaseGeneration.Datasets.Select(dataset => new DatasetShape(
                dataset.SourceSetId,
                dataset.RowCount,
                dataset.Columns)).ToArray());
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
        var configuredSourceSetIds = configuration.SourceSets
            .Select(sourceSet => sourceSet.SourceSetId)
            .ToHashSet();
        var worksheetById = allWorksheets
            .GroupBy(worksheet => worksheet.WorksheetDefinitionId)
            .ToDictionary(group => group.Key, group => group.ToArray());

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

        foreach (var group in allWorksheets.GroupBy(worksheet => worksheet.SourceSetId)
                     .Where(group => group.Count() != 1))
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "source-set-route-count",
                "A Source Set must own exactly one worksheet route.",
                group.Key));
        }

        foreach (var worksheet in allWorksheets.Where(worksheet =>
                     !configuredSourceSetIds.Contains(worksheet.SourceSetId)))
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "orphaned-worksheet-route",
                "A worksheet route references a Source Set without export configuration.",
                worksheet.SourceSetId,
                worksheet.WorkbookDefinitionId,
                worksheet.WorksheetDefinitionId));
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
            var dataset = datasets.SingleOrDefault(dataset =>
                dataset.SourceSetId == setConfiguration.SourceSetId);
            if (!setConfiguration.IsEnabled)
            {
                continue;
            }

            if (dataset is null)
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "missing-extraction-dataset",
                    "The enabled Source Set does not exist in the Extraction Result.",
                    setConfiguration.SourceSetId));
            }

            else if (dataset.RowCount > ExcelWorkbookLimits.MaximumDataRows)
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "export-row-limit-exceeded",
                    "The enabled Source Set exceeds Excel's worksheet row limit.",
                    setConfiguration.SourceSetId));
            }

            if (setConfiguration.WorksheetDefinitionId is not { } worksheetId
                || !worksheetById.TryGetValue(worksheetId, out var matchingWorksheets)
                || matchingWorksheets.Length != 1)
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "missing-worksheet-route",
                    "The enabled Source Set requires exactly one worksheet route.",
                    setConfiguration.SourceSetId));
                continue;
            }

            var worksheet = matchingWorksheets[0];
            if (worksheet.SourceSetId != setConfiguration.SourceSetId)
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "shared-worksheet-route",
                    "A worksheet cannot be shared by multiple Source Sets.",
                    setConfiguration.SourceSetId,
                    worksheet.WorkbookDefinitionId,
                    worksheet.WorksheetDefinitionId));
                continue;
            }

            if (!workbookById.TryGetValue(worksheet.WorkbookDefinitionId, out var workbook))
            {
                failures.Add(new ExportConfigurationValidationFailure(
                    "missing-workbook-route",
                    "The enabled Source Set worksheet requires an existing workbook.",
                    setConfiguration.SourceSetId,
                    worksheet.WorkbookDefinitionId,
                    worksheet.WorksheetDefinitionId));
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
                field.DatabaseColumnIdentity.SourceSetId != setConfiguration.SourceSetId))
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "database-field-mismatch",
                "Export fields must exactly match the typed Database columns for the Source Set.",
                setConfiguration.SourceSetId));
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
                setConfiguration.SourceSetId));
        }

        var outputColumnCount = setConfiguration.Fields.Sum(field =>
                field.IsValueIncluded ? field.IsSourceIdCompanionIncluded ? 2 : 1 : 0)
            + setConfiguration.MetadataFields.Count(field => field.IsIncluded);
        if (outputColumnCount is < 1 or > ExcelWorkbookLimits.MaximumColumns)
        {
            failures.Add(new ExportConfigurationValidationFailure(
                "invalid-output-column-count",
                "Each enabled Source Set requires at least one output column within Excel limits.",
                setConfiguration.SourceSetId));
        }
    }

    private sealed record DatasetShape(
        SourceSetId SourceSetId,
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
