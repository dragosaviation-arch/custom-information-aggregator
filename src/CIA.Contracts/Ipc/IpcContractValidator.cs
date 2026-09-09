namespace CIA.Contracts.Ipc;

using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Export;
using CIA.Contracts.Extraction;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;

public static class IpcContractValidator
{
    private const int MaximumFailureCodeLength = 100;
    private const int MaximumFailureDescriptionLength = 1024;
    private const int MaximumPathLength = 32767;

    public static void Validate(IpcMessage message)
    {
        if (message is null)
        {
            throw InvalidContract("An IPC message is required.");
        }

        ValidateVersionSevenId(message.MessageId, nameof(message.MessageId));

        if (message.TimestampUtc.Offset != TimeSpan.Zero)
        {
            throw InvalidContract("IPC message timestamps must use UTC DateTimeOffset values.");
        }

        switch (message)
        {
            case EstablishConnectionCommand command:
                ValidateEstablishConnectionCommand(command);
                break;
            case ProcessingHostLivenessCommand:
            case StopProcessingHostCommand:
                break;
            case CancelOperationCommand command:
                ValidateCancelOperationCommand(command);
                break;
            case LoadSourcesCommand command:
                ValidateLoadSourcesCommand(command);
                break;
            case RefreshSourceCommand command:
                ValidateRefreshSourceCommand(command);
                break;
            case RunDiscoveryCommand command:
                ValidateRunDiscoveryCommand(command);
                break;
            case GetDiscoveryOccurrenceCommand command:
                ValidateGetDiscoveryOccurrenceCommand(command);
                break;
            case BuildDatabaseCommand command:
                ValidateBuildDatabaseCommand(command);
                break;
            case RunExtractionCommand command:
                ValidateRunExtractionCommand(command);
                break;
            case RunWorkbookExportCommand command:
                ValidateRunWorkbookExportCommand(command);
                break;
            case GetDatabaseReviewPageCommand command:
                ValidateGetDatabaseReviewPageCommand(command);
                break;
            case CommandAcknowledgement acknowledgement:
                ValidateCommandAcknowledgement(acknowledgement);
                break;
            case LoadSourcesResponse response:
                ValidateLoadSourcesResponse(response);
                break;
            case RefreshSourceResponse response:
                ValidateRefreshSourceResponse(response);
                break;
            case RunDiscoveryResponse response:
                ValidateRunDiscoveryResponse(response);
                break;
            case GetDiscoveryOccurrenceResponse response:
                ValidateGetDiscoveryOccurrenceResponse(response);
                break;
            case BuildDatabaseResponse response:
                ValidateBuildDatabaseResponse(response);
                break;
            case RunExtractionResponse response:
                ValidateRunExtractionResponse(response);
                break;
            case RunWorkbookExportResponse response:
                ValidateRunWorkbookExportResponse(response);
                break;
            case GetDatabaseReviewPageResponse response:
                ValidateGetDatabaseReviewPageResponse(response);
                break;
            case ProcessingHostAvailabilityEvent availabilityEvent:
                ValidateProcessingHostAvailabilityEvent(availabilityEvent);
                break;
            case SourceIntakeProgressEvent progressEvent:
                ValidateSourceIntakeProgressEvent(progressEvent);
                break;
            default:
                throw InvalidContract($"Unsupported IPC contract type '{message.GetType().FullName}'.");
        }
    }

    private static void ValidateEstablishConnectionCommand(EstablishConnectionCommand command)
    {
        ValidateVersionSevenId(command.ClientInstanceId, nameof(command.ClientInstanceId));

        if (command.ProtocolVersion != IpcProtocol.CurrentVersion)
        {
            throw InvalidContract(
                $"Protocol version {command.ProtocolVersion} is not supported. Expected {IpcProtocol.CurrentVersion}.");
        }
    }

    private static void ValidateCommandAcknowledgement(CommandAcknowledgement acknowledgement)
    {
        ValidateVersionSevenId(acknowledgement.CommandMessageId, nameof(acknowledgement.CommandMessageId));

        if (!Enum.IsDefined(acknowledgement.Acceptance))
        {
            throw InvalidContract("The command acknowledgement has an unsupported acceptance value.");
        }

        if (acknowledgement.Acceptance == CommandAcceptance.Accepted)
        {
            if (acknowledgement.Failure is not null)
            {
                throw InvalidContract("An accepted command acknowledgement cannot include failure information.");
            }

            return;
        }

        if (acknowledgement.Failure is null)
        {
            throw InvalidContract("A rejected command acknowledgement must include controlled failure information.");
        }

        ValidateFailure(acknowledgement.Failure);
    }

    private static void ValidateCancelOperationCommand(CancelOperationCommand command)
    {
        if (!CIA.Contracts.Operations.OperationId.IsValid(command.OperationId.Value))
        {
            throw InvalidContract(
                "A cancellation command requires a non-empty UUIDv7 Operation ID.");
        }
    }

    private static void ValidateLoadSourcesCommand(LoadSourcesCommand command)
    {
        if (!SourceSetId.IsValid(command.SourceSetId.Value))
        {
            throw InvalidContract("A source-load command requires a non-empty Source Set ID.");
        }

        if (!Enum.IsDefined(command.SelectionKind))
        {
            throw InvalidContract("The source-selection kind is not supported.");
        }

        ValidatePath(command.Path);

        if (command.Settings is null)
        {
            throw InvalidContract("Source-load settings are required.");
        }

        if (!ArchiveNestingDepth.IsValid(command.Settings.MaximumArchiveNestingDepth.Value))
        {
            throw InvalidContract("The maximum archive nesting depth must be at least 1.");
        }

        ValidatePersistentExtractionSettings(command.Settings);
    }

    private static void ValidateLoadSourcesResponse(LoadSourcesResponse response)
    {
        ValidateVersionSevenId(response.CommandMessageId, nameof(response.CommandMessageId));

        if (!Enum.IsDefined(response.Acceptance))
        {
            throw InvalidContract("The source-load response has an unsupported acceptance value.");
        }

        if (response.Sources is null)
        {
            throw InvalidContract("A source-load response requires a source collection.");
        }

        foreach (var source in response.Sources)
        {
            if (source is null)
            {
                throw InvalidContract("A source-load response cannot contain null source items.");
            }

            ValidateLoadedSource(source);
        }

        if (response.Issues is null)
        {
            throw InvalidContract("A source-load response requires an issue collection.");
        }

        foreach (var issue in response.Issues)
        {
            if (issue is null)
            {
                throw InvalidContract("A source-load response cannot contain null issue items.");
            }

            ValidateSourceIntakeIssue(issue);
        }

        if (response.Acceptance == CommandAcceptance.Accepted)
        {
            if (response.Failure is not null)
            {
                throw InvalidContract("An accepted source-load response cannot include failure information.");
            }

            return;
        }

        if (response.Sources.Count != 0 || response.Failure is null)
        {
            throw InvalidContract(
                "A rejected source-load response must contain no sources and include controlled failure information.");
        }

        ValidateFailure(response.Failure);
    }

    private static void ValidateRefreshSourceCommand(RefreshSourceCommand command)
    {
        if (command.Source is null)
        {
            throw InvalidContract("A source-refresh command requires a loaded source.");
        }

        ValidateLoadedSource(command.Source);
    }

    private static void ValidateRunDiscoveryCommand(RunDiscoveryCommand command)
    {
        ValidateOperationCorrelation(command.Correlation);

        if (command.Sources is null || command.Sources.Count == 0)
        {
            throw InvalidContract("A Discovery command requires an active source set.");
        }

        var sourceIds = new HashSet<SourceId>();

        foreach (var source in command.Sources)
        {
            if (source is null)
            {
                throw InvalidContract("A Discovery command cannot contain null sources.");
            }

            ValidateLoadedSource(source);

            if (!source.IsIncluded || source.Status != LoadedSourceStatus.Ready)
            {
                throw InvalidContract(
                    "Discovery command sources must be included and ready for processing.");
            }

            if (!sourceIds.Add(source.SourceId))
            {
                throw InvalidContract("Discovery command Source IDs must be unique.");
            }
        }
    }

    private static void ValidateRefreshSourceResponse(RefreshSourceResponse response)
    {
        ValidateVersionSevenId(response.CommandMessageId, nameof(response.CommandMessageId));

        if (!Enum.IsDefined(response.Acceptance))
        {
            throw InvalidContract("The source-refresh response has an unsupported acceptance value.");
        }

        if (response.Source is null)
        {
            throw InvalidContract("A source-refresh response requires the retained loaded source.");
        }

        ValidateLoadedSource(response.Source);

        if (response.Acceptance == CommandAcceptance.Accepted)
        {
            if (response.Source.Status != LoadedSourceStatus.Ready || response.Failure is not null)
            {
                throw InvalidContract(
                    "An accepted source-refresh response requires ready status and no failure information.");
            }

            return;
        }

        if (response.Source.Status == LoadedSourceStatus.Ready || response.Failure is null)
        {
            throw InvalidContract(
                "An unsuccessful source-refresh response requires a non-ready status and controlled failure information.");
        }

        ValidateFailure(response.Failure);
    }

    private static void ValidateGetDiscoveryOccurrenceCommand(
        GetDiscoveryOccurrenceCommand command)
    {
        var lookup = command.Lookup;
        if (lookup is null || lookup.Source is null)
        {
            throw InvalidContract("A Discovery occurrence request requires lookup metadata.");
        }

        ValidateOperationId(lookup.DiscoveryOperationId, "Discovery occurrence requests");
        ValidateLoadedSource(lookup.Source);

        if (string.IsNullOrWhiteSpace(lookup.InformationType)
            || lookup.GlobalOrdinal < 1
            || lookup.TotalOccurrenceCount < lookup.GlobalOrdinal
            || lookup.LocalOrdinal < 1
            || lookup.ExpectedSourceOccurrenceCount < lookup.LocalOrdinal
            || lookup.ExpectedSourceOccurrenceCount > lookup.TotalOccurrenceCount
            || !lookup.Source.IsIncluded
            || lookup.Source.Kind != LoadedSourceKind.XmlFile
            || lookup.Source.Status != LoadedSourceStatus.Ready)
        {
            throw InvalidContract(
                "A Discovery occurrence request contains invalid source or ordinal metadata.");
        }
    }

    private static void ValidateBuildDatabaseCommand(BuildDatabaseCommand command)
    {
        ValidateOperationCorrelation(command.Correlation);
        ValidateDatabaseMapping(command.Mapping);

        if (command.Sources is null || command.Sources.Count == 0)
        {
            throw InvalidContract("A Database build command requires an active source set.");
        }

        var sourceIds = new HashSet<SourceId>();
        foreach (var source in command.Sources)
        {
            if (source is null)
            {
                throw InvalidContract("A Database build command cannot contain null sources.");
            }

            ValidateLoadedSource(source);
            if (!source.IsIncluded
                || source.Status != LoadedSourceStatus.Ready
                || source.Kind != LoadedSourceKind.XmlFile
                || !sourceIds.Add(source.SourceId))
            {
                throw InvalidContract(
                    "Database build sources must be unique, included, ready XML sources.");
            }
        }
    }

    private static void ValidateGetDatabaseReviewPageCommand(
        GetDatabaseReviewPageCommand command)
    {
        ValidateOperationId(command.GenerationId, "Database review requests");
        ValidateDatabaseReviewRange(command.StartRowOrdinal, command.RowCount);
    }

    private static void ValidateRunExtractionCommand(RunExtractionCommand command)
    {
        ValidateOperationCorrelation(command.Correlation);
        ValidateDatabaseGeneration(command.DatabaseGeneration);

        if (command.DatabaseGeneration.OperationId == command.Correlation.OperationId)
        {
            throw InvalidContract(
                "Extraction and Database generation operations require distinct identities.");
        }
    }

    private static void ValidateRunWorkbookExportCommand(RunWorkbookExportCommand command)
    {
        if (command.ExtractionResult is null || command.Configuration is null)
        {
            throw InvalidContract(
                "A workbook export requires an Extraction Result and export configuration.");
        }

        ValidateOperationCorrelation(command.Correlation);
        ValidateExtractionResult(command.ExtractionResult);
        ValidateExportConfiguration(command.Configuration, command.ExtractionResult);
        ValidatePath(command.TargetPath);

        if (!string.Equals(
                Path.GetExtension(command.TargetPath),
                ".xlsx",
                StringComparison.OrdinalIgnoreCase)
            || command.Correlation.OperationId == command.ExtractionResult.OperationId)
        {
            throw InvalidContract(
                "A workbook export requires a distinct operation and a fully qualified .xlsx target.");
        }
    }

    private static void ValidateRunDiscoveryResponse(RunDiscoveryResponse response)
    {
        ValidateVersionSevenId(response.CommandMessageId, nameof(response.CommandMessageId));

        if (!Enum.IsDefined(response.Acceptance)
            || response.Completion is null
            || response.Information is null
            || response.Issues is null)
        {
            throw InvalidContract("The Discovery response is incomplete or unsupported.");
        }

        ValidateOperationCorrelation(response.Completion.Correlation);

        var informationTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var information in response.Information)
        {
            if (information is null
                || string.IsNullOrWhiteSpace(information.InformationType)
                || information.SampleValue is null
                || information.TotalOccurrenceCount < 1
                || information.ContributingSources is null
                || information.ContributingSources.Count == 0)
            {
                throw InvalidContract("A discovered information item is invalid.");
            }

            if (!informationTypes.Add(information.InformationType))
            {
                throw InvalidContract("Discovered information identities must be unique.");
            }

            var sourceIds = new HashSet<SourceId>();
            long sourceOccurrenceTotal = 0;

            foreach (var contribution in information.ContributingSources)
            {
                if (contribution is null
                    || !SourceId.IsValid(contribution.SourceId.Value)
                    || string.IsNullOrWhiteSpace(contribution.SourceName)
                    || contribution.OccurrenceCount < 1
                    || !sourceIds.Add(contribution.SourceId))
                {
                    throw InvalidContract("A Discovery source contribution is invalid.");
                }

                sourceOccurrenceTotal += contribution.OccurrenceCount;
            }

            if (sourceOccurrenceTotal != information.TotalOccurrenceCount)
            {
                throw InvalidContract(
                    "Discovery per-source occurrence counts must match the aggregate count.");
            }
        }

        foreach (var issue in response.Issues)
        {
            if (issue is null || !SourceId.IsValid(issue.SourceId.Value))
            {
                throw InvalidContract("A Discovery source issue requires a Source ID.");
            }

            ValidateFailure(new IpcFailure(issue.Code, issue.Description));
        }

        if (response.Acceptance == CommandAcceptance.Accepted)
        {
            if (response.Failure is not null
                || response.Completion.Outcome is not (
                    OperationOutcome.CompletedSuccessfully
                    or OperationOutcome.CompletedWithIssues))
            {
                throw InvalidContract(
                    "An accepted Discovery response requires a completed outcome and no failure.");
            }

            return;
        }

        if (response.Information.Count != 0
            || response.Failure is null
            || response.Completion.Outcome is not (
                OperationOutcome.Failed
                or OperationOutcome.Cancelled
                or OperationOutcome.InterruptedIncomplete))
        {
            throw InvalidContract(
                "An unsuccessful Discovery response requires no result and controlled failure context.");
        }

        ValidateFailure(response.Failure);
    }

    private static void ValidateGetDiscoveryOccurrenceResponse(
        GetDiscoveryOccurrenceResponse response)
    {
        ValidateVersionSevenId(response.CommandMessageId, nameof(response.CommandMessageId));
        ValidateOperationId(response.DiscoveryOperationId, "Discovery occurrence responses");

        if (!Enum.IsDefined(response.Acceptance))
        {
            throw InvalidContract(
                "The Discovery occurrence response has an unsupported acceptance value.");
        }

        if (response.Acceptance == CommandAcceptance.Accepted)
        {
            if (response.Occurrence is null || response.Failure is not null)
            {
                throw InvalidContract(
                    "An accepted Discovery occurrence response requires one occurrence and no failure.");
            }

            ValidateDiscoveredOccurrence(response.Occurrence);
            return;
        }

        if (response.Occurrence is not null || response.Failure is null)
        {
            throw InvalidContract(
                "A rejected Discovery occurrence response requires no occurrence and controlled failure information.");
        }

        ValidateFailure(response.Failure);
    }

    private static void ValidateBuildDatabaseResponse(BuildDatabaseResponse response)
    {
        ValidateVersionSevenId(response.CommandMessageId, nameof(response.CommandMessageId));

        if (!Enum.IsDefined(response.Acceptance) || response.Completion is null)
        {
            throw InvalidContract("The Database build response is incomplete or unsupported.");
        }

        ValidateOperationCorrelation(response.Completion.Correlation);

        if (response.Acceptance == CommandAcceptance.Accepted)
        {
            if (response.Failure is not null
                || response.PublishedGeneration is null
                || response.PublishedGeneration.OperationId
                    != response.Completion.Correlation.OperationId
                || response.Completion.Outcome is not (
                    OperationOutcome.CompletedSuccessfully
                    or OperationOutcome.CompletedWithIssues))
            {
                throw InvalidContract(
                    "An accepted Database build response requires a matching published generation and completed outcome.");
            }

            ValidateDatabaseGeneration(response.PublishedGeneration);
            return;
        }

        if (response.PublishedGeneration is not null
            || response.Failure is null
            || response.Completion.Outcome is not (
                OperationOutcome.Failed
                or OperationOutcome.Cancelled
                or OperationOutcome.InterruptedIncomplete))
        {
            throw InvalidContract(
                "An unsuccessful Database build response requires no published generation and controlled failure context.");
        }

        ValidateFailure(response.Failure);
    }

    private static void ValidateGetDatabaseReviewPageResponse(
        GetDatabaseReviewPageResponse response)
    {
        ValidateVersionSevenId(response.CommandMessageId, nameof(response.CommandMessageId));
        ValidateOperationId(response.GenerationId, "Database review responses");

        if (!Enum.IsDefined(response.Acceptance))
        {
            throw InvalidContract(
                "The Database review response has an unsupported acceptance value.");
        }

        if (response.Acceptance == CommandAcceptance.Accepted)
        {
            if (response.Page is null
                || response.Failure is not null
                || response.Page.GenerationId != response.GenerationId)
            {
                throw InvalidContract(
                    "An accepted Database review response requires a matching page and no failure.");
            }

            ValidateDatabaseReviewPage(response.Page);
            return;
        }

        if (response.Page is not null || response.Failure is null)
        {
            throw InvalidContract(
                "A rejected Database review response requires no page and controlled failure information.");
        }

        ValidateFailure(response.Failure);
    }

    private static void ValidateRunExtractionResponse(RunExtractionResponse response)
    {
        ValidateVersionSevenId(response.CommandMessageId, nameof(response.CommandMessageId));

        if (!Enum.IsDefined(response.Acceptance) || response.Completion is null)
        {
            throw InvalidContract("The Extraction response is incomplete or unsupported.");
        }

        ValidateOperationCorrelation(response.Completion.Correlation);

        if (response.Acceptance == CommandAcceptance.Accepted)
        {
            if (response.Failure is not null
                || response.PublishedResult is null
                || response.PublishedResult.OperationId
                    != response.Completion.Correlation.OperationId
                || response.Completion.Outcome is not (
                    OperationOutcome.CompletedSuccessfully
                    or OperationOutcome.CompletedWithIssues))
            {
                throw InvalidContract(
                    "An accepted Extraction response requires a matching published result and completed outcome.");
            }

            ValidateExtractionResult(response.PublishedResult);
            return;
        }

        if (response.PublishedResult is not null
            || response.Failure is null
            || response.Completion.Outcome is not (
                OperationOutcome.Failed
                or OperationOutcome.Cancelled
                or OperationOutcome.InterruptedIncomplete))
        {
            throw InvalidContract(
                "An unsuccessful Extraction response requires no published result and controlled failure context.");
        }

        ValidateFailure(response.Failure);
    }

    private static void ValidateRunWorkbookExportResponse(RunWorkbookExportResponse response)
    {
        ValidateVersionSevenId(response.CommandMessageId, nameof(response.CommandMessageId));

        if (!Enum.IsDefined(response.Acceptance) || response.Completion is null)
        {
            throw InvalidContract("The workbook export response is incomplete or unsupported.");
        }

        ValidateOperationCorrelation(response.Completion.Correlation);

        if (response.Acceptance == CommandAcceptance.Accepted)
        {
            if (response.Failure is not null
                || response.Workbook is null
                || response.Workbook.OperationId
                    != response.Completion.Correlation.OperationId
                || response.Completion.Outcome is not (
                    OperationOutcome.CompletedSuccessfully
                    or OperationOutcome.CompletedWithIssues))
            {
                throw InvalidContract(
                    "An accepted workbook export response requires a matching workbook and completed outcome.");
            }

            ValidateWorkbookExportSummary(response.Workbook);
            return;
        }

        if (response.Workbook is not null
            || response.Failure is null
            || response.Completion.Outcome is not (
                OperationOutcome.Failed
                or OperationOutcome.Cancelled
                or OperationOutcome.InterruptedIncomplete))
        {
            throw InvalidContract(
                "An unsuccessful workbook export response requires no workbook and controlled failure context.");
        }

        ValidateFailure(response.Failure);
    }

    private static void ValidateExportConfiguration(
        ExportConfigurationSnapshot configuration,
        ExtractionResultSummary extractionResult)
    {
        if (configuration is null
            || configuration.Fields is null
            || configuration.Fields.Count == 0)
        {
            throw InvalidContract("A workbook export requires an export configuration.");
        }

        var configuredFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in configuration.Fields)
        {
            if (field is null
                || string.IsNullOrWhiteSpace(field.DatabaseFieldIdentity)
                || string.IsNullOrWhiteSpace(field.ExcelHeader)
                || field.ExcelHeader.Length > ExcelWorkbookLimits.MaximumCellTextLength
                || field.IsSourceIdCompanionIncluded && !field.IsValueIncluded
                || !configuredFields.Add(field.DatabaseFieldIdentity))
            {
                throw InvalidContract("A workbook export field is invalid or duplicated.");
            }
        }

        var extractionFields = extractionResult.DatabaseGeneration.Mapping.Columns
            .Select(column => column.DatabaseTagName)
            .ToHashSet(StringComparer.Ordinal);
        var outputColumns = configuration.CreateIncludedOutputColumns();
        if (!configuredFields.SetEquals(extractionFields)
            || outputColumns.Count is < 1 or > ExcelWorkbookLimits.MaximumColumns)
        {
            throw InvalidContract(
                "The export configuration must match the Extraction Result and Excel column limits.");
        }
    }

    private static void ValidateWorkbookExportSummary(WorkbookExportSummary workbook)
    {
        ValidateOperationId(workbook.OperationId, "Workbook exports");
        ValidateOperationId(workbook.ExtractionResultId, "Workbook exports");
        ValidatePath(workbook.TargetPath);

        if (workbook.OperationId == workbook.ExtractionResultId
            || !string.Equals(
                Path.GetExtension(workbook.TargetPath),
                ".xlsx",
                StringComparison.OrdinalIgnoreCase)
            || workbook.ColumnCount is < 1 or > ExcelWorkbookLimits.MaximumColumns
            || workbook.DataRowCount is < 0 or > ExcelWorkbookLimits.MaximumDataRows)
        {
            throw InvalidContract("A workbook export summary is invalid.");
        }
    }

    private static void ValidateDatabaseReviewPage(DatabaseReviewPage page)
    {
        ValidateOperationId(page.GenerationId, "Database review pages");
        ValidateDatabaseReviewRange(page.StartRowOrdinal, page.RequestedRowCount);

        if (page.TotalMappedValueCount < 1
            || page.Columns is null
            || page.Columns.Count == 0)
        {
            throw InvalidContract(
                "A Database review page requires published values and dynamic columns.");
        }

        var databaseTagNames = new HashSet<string>(StringComparer.Ordinal);
        long totalMappedValueCount = 0;
        foreach (var column in page.Columns)
        {
            if (column is null
                || string.IsNullOrWhiteSpace(column.DatabaseTagName)
                || column.TotalValueCount < 0
                || column.Values is null
                || column.Values.Count > page.RequestedRowCount
                || !databaseTagNames.Add(column.DatabaseTagName))
            {
                throw InvalidContract("A Database review column is invalid or duplicated.");
            }

            totalMappedValueCount += column.TotalValueCount;
            var previousOrdinal = page.StartRowOrdinal - 1;
            foreach (var value in column.Values)
            {
                if (value is null
                    || value.ColumnOrdinal <= previousOrdinal
                    || value.ColumnOrdinal > column.TotalValueCount
                    || value.ColumnOrdinal >= page.StartRowOrdinal + page.RequestedRowCount
                    || value.Value is null
                    || string.IsNullOrWhiteSpace(value.SourceInformationType)
                    || !SourceId.IsValid(value.SourceId.Value))
                {
                    throw InvalidContract(
                        "A Database review value is invalid, unordered, or outside its requested page.");
                }

                previousOrdinal = value.ColumnOrdinal;
            }
        }

        if (totalMappedValueCount != page.TotalMappedValueCount)
        {
            throw InvalidContract(
                "The Database review mapped-value count does not reconcile with its columns.");
        }
    }

    private static void ValidateDatabaseReviewRange(int startRowOrdinal, int rowCount)
    {
        if (startRowOrdinal < 1
            || startRowOrdinal > int.MaxValue - DatabaseReviewLimits.MaximumRowsPerPage
            || rowCount is < 1 or > DatabaseReviewLimits.MaximumRowsPerPage)
        {
            throw InvalidContract(
                "A Database review request is outside the supported bounded page range.");
        }
    }

    private static void ValidateDatabaseGeneration(DatabaseGenerationSummary generation)
    {
        ValidateOperationId(generation.OperationId, "Database generations");
        ValidateDatabaseMapping(generation.Mapping);

        if (generation.ValueCount < 1)
        {
            throw InvalidContract(
                "A published Database generation requires at least one mapped value.");
        }
    }

    private static void ValidateExtractionResult(ExtractionResultSummary result)
    {
        ValidateOperationId(result.OperationId, "Extraction Results");
        ValidateDatabaseGeneration(result.DatabaseGeneration);

        if (result.OperationId == result.DatabaseGeneration.OperationId
            || result.ValueCount != result.DatabaseGeneration.ValueCount)
        {
            throw InvalidContract(
                "An Extraction Result requires a distinct identity and matching Database basis.");
        }
    }

    private static void ValidateDatabaseMapping(DatabaseMappingSnapshot mapping)
    {
        if (mapping is null || mapping.Columns is null || mapping.Columns.Count == 0)
        {
            throw InvalidContract("A Database mapping requires at least one column.");
        }

        var databaseTagNames = new HashSet<string>(StringComparer.Ordinal);
        var sourceInformationTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var column in mapping.Columns)
        {
            if (column is null
                || string.IsNullOrWhiteSpace(column.DatabaseTagName)
                || column.SourceInformationTypes is null
                || column.SourceInformationTypes.Count == 0
                || !databaseTagNames.Add(column.DatabaseTagName))
            {
                throw InvalidContract("A Database column mapping is invalid or duplicated.");
            }

            foreach (var informationType in column.SourceInformationTypes)
            {
                if (string.IsNullOrWhiteSpace(informationType)
                    || !sourceInformationTypes.Add(informationType))
                {
                    throw InvalidContract(
                        "Database source information identities must be valid and map once.");
                }
            }
        }
    }

    private static void ValidateDiscoveredOccurrence(DiscoveredOccurrence occurrence)
    {
        if (string.IsNullOrWhiteSpace(occurrence.InformationType)
            || occurrence.Ordinal < 1
            || occurrence.TotalOccurrenceCount < occurrence.Ordinal
            || !SourceId.IsValid(occurrence.SourceId.Value)
            || occurrence.Value is null)
        {
            throw InvalidContract("A discovered occurrence is invalid.");
        }
    }

    private static void ValidateLoadedSource(LoadedSourceContract source)
    {
        if (!SourceId.IsValid(source.SourceId.Value))
        {
            throw InvalidContract("A loaded source requires a non-empty Source ID.");
        }

        if (!SourceSetId.IsValid(source.SourceSetId.Value))
        {
            throw InvalidContract("A loaded source requires a non-empty Source Set ID.");
        }

        ValidatePath(source.Path);

        if (!Enum.IsDefined(source.Status) || !Enum.IsDefined(source.Kind))
        {
            throw InvalidContract("A loaded source has an unsupported kind or status.");
        }

        if (source.ArchiveProvenance is not null)
        {
            if (source.Kind != LoadedSourceKind.XmlFile)
            {
                throw InvalidContract("Only extracted XML working sources can contain archive provenance.");
            }

            ValidateArchiveProvenance(source, source.ArchiveProvenance);
        }
    }

    private static void ValidatePersistentExtractionSettings(SourceLoadSettings settings)
    {
        var directory = settings.PersistentArchiveExtractionDirectory;

        if (settings.PersistentArchiveExtractionEnabled && string.IsNullOrWhiteSpace(directory))
        {
            throw InvalidContract(
                "Persistent archive extraction requires an explicitly configured destination.");
        }

        if (!string.IsNullOrWhiteSpace(directory))
        {
            ValidatePath(directory);
        }
    }

    private static void ValidateArchiveProvenance(
        LoadedSourceContract source,
        ArchiveSourceProvenance provenance)
    {
        if (!SourceId.IsValid(provenance.OriginalArchiveSourceId.Value))
        {
            throw InvalidContract("Archive provenance requires an original archive Source ID.");
        }

        ValidatePath(provenance.OriginalArchivePath);
        ValidatePath(provenance.ExtractionRoot);

        if (!IsWithinDirectory(source.Path, provenance.ExtractionRoot))
        {
            throw InvalidContract("An extracted source path must remain inside its extraction root.");
        }

        if (!Enum.IsDefined(provenance.Retention))
        {
            throw InvalidContract("Archive provenance has an unsupported extraction-retention value.");
        }

        if (!ArchiveNestingDepth.IsValid(provenance.MaximumArchiveNestingDepth.Value)
            || provenance.ArchiveNestingLevel < 1
            || !provenance.MaximumArchiveNestingDepth.AllowsLevel(provenance.ArchiveNestingLevel))
        {
            throw InvalidContract("Archive provenance contains an invalid nesting level or maximum depth.");
        }

        if (string.IsNullOrWhiteSpace(provenance.ArchiveMemberPath))
        {
            throw InvalidContract("Archive provenance requires an archive-member path.");
        }

        ValidateRelativeArchivePath(provenance.ArchiveMemberPath);

        if (provenance.ArchiveLineage is null || provenance.ArchiveLineage.Count == 0)
        {
            throw InvalidContract("Archive provenance requires archive lineage.");
        }

        for (var index = 0; index < provenance.ArchiveLineage.Count; index++)
        {
            var lineage = provenance.ArchiveLineage[index];

            if (lineage is null
                || !SourceId.IsValid(lineage.ArchiveSourceId.Value)
                || string.IsNullOrWhiteSpace(lineage.Path)
                || lineage.NestingLevel != index + 1)
            {
                throw InvalidContract("Archive provenance contains invalid archive lineage.");
            }

            if (index == 0)
            {
                ValidatePath(lineage.Path);
            }
            else
            {
                ValidateRelativeArchivePath(lineage.Path);
            }
        }

        if (provenance.ArchiveLineage[0].ArchiveSourceId != provenance.OriginalArchiveSourceId
            || !string.Equals(
                provenance.ArchiveLineage[0].Path,
                provenance.OriginalArchivePath,
                StringComparison.OrdinalIgnoreCase)
            || provenance.ArchiveLineage.Count != provenance.ArchiveNestingLevel)
        {
            throw InvalidContract("Archive provenance lineage does not match its origin or nesting level.");
        }

        if (provenance.Retention == ArchiveExtractionRetention.Persistent)
        {
            if (string.IsNullOrWhiteSpace(provenance.PersistentExtractionDirectory))
            {
                throw InvalidContract(
                    "Persistent archive provenance requires its configured extraction destination.");
            }

            ValidatePath(provenance.PersistentExtractionDirectory);

            if (!IsWithinDirectory(
                    provenance.ExtractionRoot,
                    provenance.PersistentExtractionDirectory))
            {
                throw InvalidContract(
                    "Persistent archive extraction must remain inside its configured destination.");
            }
        }
        else if (provenance.PersistentExtractionDirectory is not null)
        {
            throw InvalidContract(
                "Temporary archive provenance cannot contain a persistent extraction destination.");
        }
    }

    private static void ValidateSourceIntakeIssue(SourceIntakeIssue issue)
    {
        if (string.IsNullOrWhiteSpace(issue.Code) || issue.Code.Length > MaximumFailureCodeLength)
        {
            throw InvalidContract(
                $"Source-intake issue codes must contain 1 to {MaximumFailureCodeLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(issue.Description)
            || issue.Description.Length > MaximumFailureDescriptionLength)
        {
            throw InvalidContract(
                $"Source-intake issue descriptions must contain 1 to {MaximumFailureDescriptionLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(issue.ArchivePath) || issue.ArchiveNestingLevel < 1)
        {
            throw InvalidContract("A source-intake issue requires archive context and a positive nesting level.");
        }

        ValidatePath(issue.ArchivePath);

        if (issue.EntryPath is { Length: > MaximumPathLength })
        {
            throw InvalidContract(
                $"Source-intake issue entry paths cannot exceed {MaximumPathLength} characters.");
        }
    }

    private static void ValidateProcessingHostAvailabilityEvent(
        ProcessingHostAvailabilityEvent availabilityEvent)
    {
        if (!Enum.IsDefined(availabilityEvent.Availability))
        {
            throw InvalidContract("The Processing Host availability event has an unsupported availability value.");
        }
    }

    private static void ValidateSourceIntakeProgressEvent(SourceIntakeProgressEvent progressEvent)
    {
        ValidateVersionSevenId(progressEvent.CommandMessageId, nameof(progressEvent.CommandMessageId));

        var progress = progressEvent.Progress;
        if (progress is null)
        {
            throw InvalidContract("A source-intake progress event requires a progress snapshot.");
        }

        if (progress.CurrentArchivePath is null)
        {
            if (progress.CurrentArchiveNestingLevel != 0)
            {
                throw InvalidContract(
                    "Source-intake progress without archive context must use nesting level zero.");
            }
        }
        else
        {
            if (Path.IsPathFullyQualified(progress.CurrentArchivePath))
            {
                ValidatePath(progress.CurrentArchivePath);
            }
            else
            {
                ValidateRelativeArchivePath(progress.CurrentArchivePath);
            }

            if (progress.CurrentArchiveNestingLevel < 1)
            {
                throw InvalidContract(
                    "Source-intake archive progress requires a positive nesting level.");
            }
        }

        if (progress.EncounteredItemCount < 0
            || progress.LoadedSourceCount < 0
            || progress.LoadedSourceCount > progress.EncounteredItemCount
            || progress.IssueCount < 0
            || progress.FailureCount < 0
            || progress.TotalItemCount is < 0
            || progress.TotalItemCount is { } total
            && total < progress.EncounteredItemCount)
        {
            throw InvalidContract("Source-intake progress counters are inconsistent.");
        }
    }

    private static void ValidateFailure(IpcFailure failure)
    {
        if (string.IsNullOrWhiteSpace(failure.Code) || failure.Code.Length > MaximumFailureCodeLength)
        {
            throw InvalidContract(
                $"Failure codes must contain 1 to {MaximumFailureCodeLength} non-whitespace characters.");
        }

        if (string.IsNullOrWhiteSpace(failure.Description)
            || failure.Description.Length > MaximumFailureDescriptionLength)
        {
            throw InvalidContract(
                $"Failure descriptions must contain 1 to {MaximumFailureDescriptionLength} non-whitespace characters.");
        }
    }

    private static void ValidateOperationCorrelation(OperationCorrelation correlation)
    {
        if (correlation is null
            || !OperationId.IsValid(correlation.OperationId.Value)
            || correlation.InitiatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw InvalidContract(
                "Operation correlation requires a UUIDv7 Operation ID and UTC initiation time.");
        }
    }

    private static void ValidateOperationId(OperationId operationId, string contractName)
    {
        if (!OperationId.IsValid(operationId.Value))
        {
            throw InvalidContract($"{contractName} require a non-empty UUIDv7 Operation ID.");
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength)
        {
            throw InvalidContract($"Source paths must contain 1 to {MaximumPathLength} non-whitespace characters.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw InvalidContract("Source paths must be fully qualified.");
        }
    }

    private static void ValidateRelativeArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength)
        {
            throw InvalidContract(
                $"Archive member paths must contain 1 to {MaximumPathLength} non-whitespace characters.");
        }

        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalized)
            || normalized.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
        {
            throw InvalidContract("Archive member paths must remain relative and cannot escape their archive.");
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var resolvedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolvedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directoryPrefix = resolvedDirectory + Path.DirectorySeparatorChar;

        return resolvedPath.Equals(resolvedDirectory, StringComparison.OrdinalIgnoreCase)
            || resolvedPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateVersionSevenId(Guid id, string fieldName)
    {
        if (id == Guid.Empty || id.Version != 7)
        {
            throw InvalidContract($"{fieldName} must be a non-empty UUIDv7 value.");
        }
    }

    private static IpcProtocolException InvalidContract(string message)
    {
        return new IpcProtocolException(IpcProtocolError.InvalidContract, message);
    }
}
