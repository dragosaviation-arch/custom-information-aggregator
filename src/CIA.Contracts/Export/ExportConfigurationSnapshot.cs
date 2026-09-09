using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace CIA.Contracts.Export;

public enum ExportOutputColumnKind
{
    Value = 1,
    SourceId = 2
}

public sealed record ExportFieldConfiguration
{
    [JsonConstructor]
    public ExportFieldConfiguration(
        string databaseFieldIdentity,
        bool isValueIncluded,
        string excelHeader,
        bool isSourceIdCompanionIncluded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFieldIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(excelHeader);

        if (isSourceIdCompanionIncluded && !isValueIncluded)
        {
            throw new ArgumentException(
                "A SourceId companion cannot be included without its owning value field.",
                nameof(isSourceIdCompanionIncluded));
        }

        DatabaseFieldIdentity = databaseFieldIdentity;
        IsValueIncluded = isValueIncluded;
        ExcelHeader = excelHeader;
        IsSourceIdCompanionIncluded = isSourceIdCompanionIncluded;
    }

    public string DatabaseFieldIdentity { get; }

    public bool IsValueIncluded { get; }

    public string ExcelHeader { get; }

    public bool IsSourceIdCompanionIncluded { get; }

    public string SourceIdCompanionHeader => $"{ExcelHeader} SourceId";
}

public sealed record ExportOutputColumn
{
    internal ExportOutputColumn(
        string owningDatabaseFieldIdentity,
        string header,
        ExportOutputColumnKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owningDatabaseFieldIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(header);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        OwningDatabaseFieldIdentity = owningDatabaseFieldIdentity;
        Header = header;
        Kind = kind;
    }

    public string OwningDatabaseFieldIdentity { get; }

    public string Header { get; }

    public ExportOutputColumnKind Kind { get; }
}

public sealed record ExportConfigurationSnapshot
{
    [JsonConstructor]
    public ExportConfigurationSnapshot(IReadOnlyList<ExportFieldConfiguration> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var fieldArray = fields.ToArray();
        if (fieldArray.Any(field => field is null))
        {
            throw new ArgumentException(
                "Export fields cannot contain null values.",
                nameof(fields));
        }

        if (fieldArray
            .Select(field => field.DatabaseFieldIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count() != fieldArray.Length)
        {
            throw new ArgumentException(
                "Each Database field can appear only once in export order.",
                nameof(fields));
        }

        Fields = new ReadOnlyCollection<ExportFieldConfiguration>(fieldArray);
    }

    public IReadOnlyList<ExportFieldConfiguration> Fields { get; }

    public IReadOnlyList<ExportOutputColumn> CreateIncludedOutputColumns()
    {
        var columns = new List<ExportOutputColumn>();
        foreach (var field in Fields.Where(field => field.IsValueIncluded))
        {
            columns.Add(new ExportOutputColumn(
                field.DatabaseFieldIdentity,
                field.ExcelHeader,
                ExportOutputColumnKind.Value));
            if (field.IsSourceIdCompanionIncluded)
            {
                columns.Add(new ExportOutputColumn(
                    field.DatabaseFieldIdentity,
                    field.SourceIdCompanionHeader,
                    ExportOutputColumnKind.SourceId));
            }
        }

        return new ReadOnlyCollection<ExportOutputColumn>(columns);
    }
}
