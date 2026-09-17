using System.Text.Json.Serialization;
using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Export;
using CIA.Contracts.Extraction;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

namespace CIA.Contracts.Ipc;

[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "messageType",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(EstablishConnectionCommand), "establishConnectionCommand")]
[JsonDerivedType(typeof(ProcessingHostLivenessCommand), "processingHostLivenessCommand")]
[JsonDerivedType(typeof(CancelOperationCommand), "cancelOperationCommand")]
[JsonDerivedType(typeof(LoadSourcesCommand), "loadSourcesCommand")]
[JsonDerivedType(typeof(RefreshSourceCommand), "refreshSourceCommand")]
[JsonDerivedType(typeof(RunDiscoveryCommand), "runDiscoveryCommand")]
[JsonDerivedType(typeof(GetDiscoveryOccurrenceCommand), "getDiscoveryOccurrenceCommand")]
[JsonDerivedType(typeof(BuildDatabaseCommand), "buildDatabaseCommand")]
[JsonDerivedType(typeof(RunExtractionCommand), "runExtractionCommand")]
[JsonDerivedType(typeof(RunWorkbookExportCommand), "runWorkbookExportCommand")]
[JsonDerivedType(typeof(GetDatabaseReviewPageCommand), "getDatabaseReviewPageCommand")]
[JsonDerivedType(typeof(SetDatabaseRowsIncludedCommand), "setDatabaseRowsIncludedCommand")]
[JsonDerivedType(typeof(StopProcessingHostCommand), "stopProcessingHostCommand")]
[JsonDerivedType(typeof(CommandAcknowledgement), "commandAcknowledgement")]
[JsonDerivedType(typeof(LoadSourcesResponse), "loadSourcesResponse")]
[JsonDerivedType(typeof(RefreshSourceResponse), "refreshSourceResponse")]
[JsonDerivedType(typeof(RunDiscoveryResponse), "runDiscoveryResponse")]
[JsonDerivedType(typeof(GetDiscoveryOccurrenceResponse), "getDiscoveryOccurrenceResponse")]
[JsonDerivedType(typeof(BuildDatabaseResponse), "buildDatabaseResponse")]
[JsonDerivedType(typeof(RunExtractionResponse), "runExtractionResponse")]
[JsonDerivedType(typeof(RunWorkbookExportResponse), "runWorkbookExportResponse")]
[JsonDerivedType(typeof(GetDatabaseReviewPageResponse), "getDatabaseReviewPageResponse")]
[JsonDerivedType(typeof(SetDatabaseRowsIncludedResponse), "setDatabaseRowsIncludedResponse")]
[JsonDerivedType(typeof(ProcessingHostAvailabilityEvent), "processingHostAvailabilityEvent")]
[JsonDerivedType(typeof(SourceIntakeProgressEvent), "sourceIntakeProgressEvent")]
public abstract record IpcMessage(Guid MessageId, DateTimeOffset TimestampUtc);

public abstract record IpcCommand(Guid MessageId, DateTimeOffset TimestampUtc)
    : IpcMessage(MessageId, TimestampUtc);

public abstract record IpcResponse(Guid MessageId, DateTimeOffset TimestampUtc)
    : IpcMessage(MessageId, TimestampUtc);

public abstract record IpcEvent(Guid MessageId, DateTimeOffset TimestampUtc)
    : IpcMessage(MessageId, TimestampUtc);

public sealed record EstablishConnectionCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid ClientInstanceId,
    int ProtocolVersion)
    : IpcCommand(MessageId, TimestampUtc);

public sealed record ProcessingHostLivenessCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc)
    : IpcCommand(MessageId, TimestampUtc);

public sealed record CancelOperationCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    OperationId OperationId)
    : IpcCommand(MessageId, TimestampUtc);

[method: JsonConstructor]
public sealed record LoadSourcesCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    SourceSetId SourceSetId,
    SourceSelectionKind SelectionKind,
    string Path,
    SourceLoadSettings Settings)
    : IpcCommand(MessageId, TimestampUtc)
{
    public LoadSourcesCommand(
        Guid messageId,
        DateTimeOffset timestampUtc,
        SourceSelectionKind selectionKind,
        string path,
        SourceLoadSettings settings)
        : this(
            messageId,
            timestampUtc,
            SourceSetId.CreateNew(),
            selectionKind,
            path,
            settings)
    {
    }
}

public sealed record RefreshSourceCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    LoadedSourceContract Source)
    : IpcCommand(MessageId, TimestampUtc);

public sealed record RunDiscoveryCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    OperationCorrelation Correlation,
    IReadOnlyList<LoadedSourceContract> Sources)
    : IpcCommand(MessageId, TimestampUtc);

public sealed record GetDiscoveryOccurrenceCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    DiscoveryOccurrenceLookup Lookup)
    : IpcCommand(MessageId, TimestampUtc);

[method: JsonConstructor]
public sealed record BuildDatabaseCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    OperationCorrelation Correlation,
    DatabaseBuildSpecification Specification)
    : IpcCommand(MessageId, TimestampUtc)
{
    public BuildDatabaseCommand(
        Guid messageId,
        DateTimeOffset timestampUtc,
        OperationCorrelation correlation,
        IReadOnlyList<LoadedSourceContract> sources,
        DatabaseMappingSnapshot mapping)
        : this(messageId, timestampUtc, correlation, CreateLegacySpecification(sources, mapping))
    {
        Sources = sources;
        Mapping = mapping;
    }

    [JsonIgnore]
    public IReadOnlyList<LoadedSourceContract> Sources { get; } =
        Specification.Datasets.SelectMany(dataset => dataset.Sources).ToArray();

    [JsonIgnore]
    public DatabaseMappingSnapshot Mapping { get; } = new(
        Specification.Datasets.SelectMany(dataset => dataset.Fields)
            .GroupBy(field => field.EffectiveName, StringComparer.Ordinal)
            .Select(group => new DatabaseColumnMapping(
                group.Key,
                group.SelectMany(field => field.DetailedIdentities)
                    .Select(identity => identity.InformationType)
                    .Distinct(StringComparer.Ordinal).ToArray())).ToArray());

    private static DatabaseBuildSpecification CreateLegacySpecification(
        IReadOnlyList<LoadedSourceContract> sources,
        DatabaseMappingSnapshot mapping)
    {
        var datasets = sources.GroupBy(source => source.SourceSetId).Select((group, index) =>
        {
            var fields = mapping.Columns.Select(column =>
            {
                var details = column.SourceInformationTypes.Select(type =>
                    new DiscoveryInformationIdentity(
                        group.Key, $"/{type}", type,
                        SourceValueCandidateKind.Element, $"/{type}")).ToArray();
                return new DatabaseFieldMapping(
                    DatabaseLogicalFieldIdentity.Create(details[0]),
                    column.DatabaseTagName,
                    !string.Equals(column.DatabaseTagName, details[0].InformationType, StringComparison.Ordinal),
                    details);
            }).ToArray();
            return new DatabaseDatasetBuildSpecification(
                group.Key, $"Set {index + 1}", index + 1,
                RepeatedDataLayout.AlignRepeatedGroupsByPosition,
                group.ToArray(), fields);
        }).ToArray();
        return new DatabaseBuildSpecification(datasets);
    }
}

public sealed record RunExtractionCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    OperationCorrelation Correlation,
    DatabaseGenerationSummary DatabaseGeneration)
    : IpcCommand(MessageId, TimestampUtc);

public sealed record RunWorkbookExportCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    OperationCorrelation Correlation,
    ExtractionResultSummary ExtractionResult,
    ExportConfigurationSnapshot Configuration,
    string TargetPath)
    : IpcCommand(MessageId, TimestampUtc);

[method: JsonConstructor]
public sealed record GetDatabaseReviewPageCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    DatabaseReviewQuery Query)
    : IpcCommand(MessageId, TimestampUtc)
{
    public GetDatabaseReviewPageCommand(
        Guid MessageId,
        DateTimeOffset TimestampUtc,
        OperationId GenerationId,
        int StartRowOrdinal,
        int RowCount)
        : this(MessageId, TimestampUtc, new DatabaseReviewQuery(
            GenerationId, SourceSetId.From(GenerationId.Value), StartRowOrdinal, RowCount, null,
            DatabaseRowInclusionFilter.All))
    {
    }

    [JsonIgnore]
    public OperationId GenerationId => Query.GenerationId;

    [JsonIgnore]
    public int StartRowOrdinal => Query.StartRowOrdinal;

    [JsonIgnore]
    public int RowCount => Query.RowCount;
}

public sealed record SetDatabaseRowsIncludedCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    DatabaseRowInclusionChange Change)
    : IpcCommand(MessageId, TimestampUtc);

public sealed record StopProcessingHostCommand(
    Guid MessageId,
    DateTimeOffset TimestampUtc)
    : IpcCommand(MessageId, TimestampUtc);

public sealed record CommandAcknowledgement(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    CommandAcceptance Acceptance,
    IpcFailure? Failure)
    : IpcResponse(MessageId, TimestampUtc);

public sealed record LoadSourcesResponse(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    CommandAcceptance Acceptance,
    IReadOnlyList<LoadedSourceContract> Sources,
    IpcFailure? Failure)
    : IpcResponse(MessageId, TimestampUtc)
{
    public IReadOnlyList<SourceIntakeIssue> Issues { get; init; } = [];
}

public sealed record RefreshSourceResponse(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    CommandAcceptance Acceptance,
    LoadedSourceContract Source,
    IpcFailure? Failure)
    : IpcResponse(MessageId, TimestampUtc);

public sealed record RunDiscoveryResponse(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    CommandAcceptance Acceptance,
    OperationCompletion Completion,
    IReadOnlyList<DiscoveredInformation> Information,
    IReadOnlyList<DiscoverySourceIssue> Issues,
    IpcFailure? Failure)
    : IpcResponse(MessageId, TimestampUtc);

public sealed record GetDiscoveryOccurrenceResponse(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    OperationId DiscoveryOperationId,
    CommandAcceptance Acceptance,
    DiscoveredOccurrence? Occurrence,
    IpcFailure? Failure)
    : IpcResponse(MessageId, TimestampUtc);

public sealed record BuildDatabaseResponse(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    CommandAcceptance Acceptance,
    OperationCompletion Completion,
    DatabaseGenerationSummary? PublishedGeneration,
    IpcFailure? Failure)
    : IpcResponse(MessageId, TimestampUtc);

public sealed record RunExtractionResponse(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    CommandAcceptance Acceptance,
    OperationCompletion Completion,
    ExtractionResultSummary? PublishedResult,
    IpcFailure? Failure)
    : IpcResponse(MessageId, TimestampUtc);

public sealed record RunWorkbookExportResponse(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    CommandAcceptance Acceptance,
    OperationCompletion Completion,
    WorkbookExportSummary? Workbook,
    IpcFailure? Failure)
    : IpcResponse(MessageId, TimestampUtc);

public sealed record GetDatabaseReviewPageResponse(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    OperationId GenerationId,
    CommandAcceptance Acceptance,
    DatabaseReviewPage? Page,
    IpcFailure? Failure)
    : IpcResponse(MessageId, TimestampUtc);

public sealed record SetDatabaseRowsIncludedResponse(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    OperationId GenerationId,
    CommandAcceptance Acceptance,
    int ChangedRowCount,
    IpcFailure? Failure)
    : IpcResponse(MessageId, TimestampUtc);

public sealed record ProcessingHostAvailabilityEvent(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    ProcessingHostAvailability Availability)
    : IpcEvent(MessageId, TimestampUtc);

public sealed record SourceIntakeProgressEvent(
    Guid MessageId,
    DateTimeOffset TimestampUtc,
    Guid CommandMessageId,
    SourceIntakeProgressSnapshot Progress)
    : IpcEvent(MessageId, TimestampUtc);

public sealed record IpcFailure(string Code, string Description);

public enum CommandAcceptance
{
    Accepted,
    Rejected,
    UnableToStart
}

public enum ProcessingHostAvailability
{
    Ready,
    Unavailable
}
