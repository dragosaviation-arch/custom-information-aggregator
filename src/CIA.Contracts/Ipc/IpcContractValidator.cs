namespace CIA.Contracts.Ipc;

using CIA.Contracts.Database;
using CIA.Contracts.Discovery;
using CIA.Contracts.Export;
using CIA.Contracts.Extraction;
using CIA.Contracts.Operations;
using CIA.Contracts.Sources;
using CIA.Contracts.WorkingState;

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
            case SetDatabaseRowsIncludedCommand command:
                ValidateDatabaseRowInclusionChange(command.Change);
                break;
            case SaveWorkingStateCommand command:
                ValidateWorkingStateCommand(command.Correlation, command.TargetPath, command.Snapshot);
                break;
            case RestoreWorkingStateCommand command:
                ValidateOperationCorrelation(command.Correlation);
                ValidatePath(command.PackagePath);
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
            case SetDatabaseRowsIncludedResponse response:
                ValidateVersionSevenId(response.CommandMessageId, nameof(response.CommandMessageId));
                ValidateOperationId(response.GenerationId, "Database row inclusion responses");
                if (!Enum.IsDefined(response.Acceptance)
                    || response.ChangedRowCount < 0
                    || (response.Acceptance == CommandAcceptance.Accepted) == (response.Failure is not null))
                {
                    throw InvalidContract("The Database row inclusion response is invalid.");
                }
                if (response.Failure is not null)
                {
                    ValidateFailure(response.Failure);
                }
                break;
            case SaveWorkingStateResponse response:
                ValidateWorkingStateResponse(
                    response.CommandMessageId,
                    response.Acceptance,
                    response.Completion,
                    response.Manifest,
                    response.Failure);
                break;
            case RestoreWorkingStateResponse response:
                ValidateWorkingStateResponse(
                    response.CommandMessageId,
                    response.Acceptance,
                    response.Completion,
                    response.Manifest,
                    response.Failure);
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

        if (!SourceIntakeActivityId.IsValid(command.IntakeActivityId.Value))
        {
            throw InvalidContract(
                "A source-load command requires a non-empty UUIDv7 intake activity ID.");
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
        ValidateDiscoveryInformationIdentity(lookup.Identity);

        if (lookup.Identity.SourceSetId != lookup.Source.SourceSetId
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
        if (command.Specification?.Datasets is null
            || command.Specification.Datasets.Count == 0)
        {
            throw InvalidContract("A Database build command requires Source Set datasets.");
        }

        var sourceIds = new HashSet<SourceId>();
        foreach (var dataset in command.Specification.Datasets)
        {
            if (dataset is null || dataset.Fields.Count == 0 || dataset.Sources.Count == 0)
            {
                throw InvalidContract("A Database dataset requires fields and sources.");
            }
            foreach (var source in dataset.Sources)
            {
                ValidateLoadedSource(source);
                if (!source.IsIncluded
                    || source.Status != LoadedSourceStatus.Ready
                    || source.Kind != LoadedSourceKind.XmlFile
                    || source.SourceSetId != dataset.SourceSetId
                    || !sourceIds.Add(source.SourceId))
                {
                    throw InvalidContract(
                        "Database build sources must be unique, included, ready XML sources in their dataset.");
                }
            }
        }
    }

    private static void ValidateGetDatabaseReviewPageCommand(
        GetDatabaseReviewPageCommand command)
    {
        if (command.Query is null)
        {
            throw InvalidContract("A Database review request requires a query.");
        }
        ValidateOperationId(command.Query.GenerationId, "Database review requests");
        ValidateDatabaseReviewRange(command.Query.StartRowOrdinal, command.Query.RowCount);
    }

    private static void ValidateDatabaseRowInclusionChange(DatabaseRowInclusionChange? change)
    {
        if (change is null || change.RowOrdinals is null || change.RowOrdinals.Count == 0
            || change.RowOrdinals.Any(ordinal => ordinal < 1)
            || change.RowOrdinals.Distinct().Count() != change.RowOrdinals.Count)
        {
            throw InvalidContract("A Database row inclusion request requires unique positive row ordinals.");
        }
        ValidateOperationId(change.GenerationId, "Database row inclusion requests");
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
        if (command.ExtractionResult is null
            || command.Configuration is null
            || command.PublicationPlan is null)
        {
            throw InvalidContract(
                "A workbook export requires an Extraction Result, export configuration, and publication plan.");
        }

        ValidateOperationCorrelation(command.Correlation);
        ValidateExtractionResult(command.ExtractionResult);
        ValidateExportConfiguration(command.Configuration, command.ExtractionResult);
        ValidateWorkbookPublicationPlan(
            command.PublicationPlan,
            command.Configuration,
            command.ExtractionResult);

        if (command.Correlation.OperationId == command.ExtractionResult.OperationId)
        {
            throw InvalidContract(
                "A workbook export requires a distinct operation and a fully qualified output directory.");
        }
    }

    private static void ValidateWorkbookPublicationPlan(
        WorkbookPublicationPlan plan,
        ExportConfigurationSnapshot configuration,
        ExtractionResultSummary extractionResult)
    {
        ValidatePath(plan.OutputDirectory);
        var validation = ExportConfigurationValidator.Validate(configuration, extractionResult);
        if (!validation.IsValid
            || plan.Targets is null
            || plan.Targets.Count != validation.RunnableWorkbooks.Count)
        {
            throw InvalidContract(
                "A workbook publication plan must resolve every runnable workbook exactly once.");
        }

        var targetsById = plan.Targets.ToDictionary(target => target.WorkbookDefinitionId);
        foreach (var runnable in validation.RunnableWorkbooks)
        {
            if (!targetsById.TryGetValue(
                    runnable.Workbook.WorkbookDefinitionId,
                    out var target)
                || !Enum.IsDefined(target.Disposition)
                || !string.Equals(
                    Path.GetDirectoryName(target.FinalPath),
                    plan.OutputDirectory,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetFileName(target.FinalPath),
                    runnable.Workbook.FileName,
                    StringComparison.Ordinal))
            {
                throw InvalidContract(
                    "A workbook publication target does not match its runnable workbook definition.");
            }

            ValidatePath(target.FinalPath);
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

        var informationIdentities = new HashSet<DiscoveryInformationIdentity>();
        foreach (var information in response.Information)
        {
            if (information is null
                || information.SampleValue is null
                || information.TotalOccurrenceCount < 1
                || information.ContributingSources is null
                || information.ContributingSources.Count == 0)
            {
                throw InvalidContract("A discovered information item is invalid.");
            }

            ValidateDiscoveryInformationIdentity(information.Identity);
            if (!informationIdentities.Add(information.Identity))
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
                || response.Batch is null
                || response.Batch.OperationId
                    != response.Completion.Correlation.OperationId
                || response.Completion.Outcome is not (
                    OperationOutcome.CompletedSuccessfully
                    or OperationOutcome.CompletedWithIssues))
            {
                throw InvalidContract(
                    "An accepted workbook export response requires a matching workbook and completed outcome.");
            }

            ValidateWorkbookExportBatchSummary(response.Batch);
            return;
        }

        if (response.Batch is not null
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
        if (configuration is null)
        {
            throw InvalidContract("A workbook export requires an export configuration.");
        }

        var validation = ExportConfigurationValidator.Validate(configuration, extractionResult);
        if (!validation.IsValid)
        {
            throw InvalidContract(
                $"The export configuration is invalid: {validation.Failures[0].Description}");
        }
    }

    private static void ValidateWorkbookExportBatchSummary(WorkbookExportBatchSummary batch)
    {
        ValidateOperationId(batch.OperationId, "Workbook exports");
        ValidateOperationId(batch.ExtractionResultId, "Workbook exports");
        ValidatePath(batch.OutputDirectory);
        if (batch.OperationId == batch.ExtractionResultId
            || batch.Workbooks is null
            || batch.Workbooks.Count == 0
            || batch.Workbooks.Select(workbook => workbook.WorkbookDefinitionId).Distinct().Count()
                != batch.Workbooks.Count
            || batch.Workbooks.Select(workbook => workbook.FinalPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != batch.Workbooks.Count)
        {
            throw InvalidContract("A workbook export batch summary is invalid.");
        }

        foreach (var workbook in batch.Workbooks)
        {
            ValidatePath(workbook.FinalPath);
            if (!WorkbookDefinitionId.IsValid(workbook.WorkbookDefinitionId.Value)
                || workbook.Order < 1
                || !string.Equals(
                    Path.GetExtension(workbook.FinalPath),
                    ".xlsx",
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetDirectoryName(workbook.FinalPath),
                    batch.OutputDirectory,
                    StringComparison.OrdinalIgnoreCase)
                || workbook.Worksheets is null
                || workbook.Worksheets.Count == 0)
            {
                throw InvalidContract("An exported workbook summary is invalid.");
            }

            foreach (var worksheet in workbook.Worksheets)
            {
                if (!WorksheetDefinitionId.IsValid(worksheet.WorksheetDefinitionId.Value)
                    || !SourceSetId.IsValid(worksheet.SourceSetId.Value)
                    || !ExportConfigurationValidator.IsValidWorksheetName(worksheet.Name)
                    || worksheet.Order < 1
                    || worksheet.RowCount is < 0 or > ExcelWorkbookLimits.MaximumDataRows
                    || worksheet.ColumnCount is < 1 or > ExcelWorkbookLimits.MaximumColumns)
                {
                    throw InvalidContract("An exported worksheet summary is invalid.");
                }
            }
        }
    }

    private static void ValidateDatabaseReviewPage(DatabaseReviewPage page)
    {
        ValidateOperationId(page.GenerationId, "Database review pages");
        ValidateDatabaseReviewRange(page.StartRowOrdinal, page.RequestedRowCount);

        if (page.Dataset is null || page.Columns.Count == 0 || page.TotalRowCount < 0
            || page.Rows is null || page.Rows.Count > page.RequestedRowCount)
        {
            throw InvalidContract(
                "A Database review page requires published values and dynamic columns.");
        }

        var columnIdentities = page.Columns.Select(column => column.Identity).ToHashSet();
        var previousOrdinal = 0;
        foreach (var row in page.Rows)
        {
            if (row is null || row.Ordinal <= previousOrdinal
                || row.Source.SourceSetId != page.Dataset.SourceSetId)
            {
                throw InvalidContract("A Database review row is invalid or unordered.");
            }
            previousOrdinal = row.Ordinal;
            foreach (var cell in row.Cells)
            {
                if (!columnIdentities.Contains(cell.ColumnIdentity)
                    || cell.Values.Any(value => value.SourceId != row.Source.SourceId))
                {
                    throw InvalidContract(
                        "A Database review cell is outside the dataset schema or source boundary.");
                }
            }
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
        if (generation.ValueCount < 1)
        {
            throw InvalidContract(
                "A published Database generation requires at least one mapped value.");
        }
        if (generation.IsHierarchyAware)
        {
            if (generation.Datasets is null || generation.Datasets.Count == 0
                || generation.RowCount < 1)
            {
                throw InvalidContract("A hierarchy-aware Database generation requires datasets and rows.");
            }
        }
        else
        {
            ValidateDatabaseMapping(generation.Mapping);
        }
    }

    private static void ValidateExtractionResult(ExtractionResultSummary result)
    {
        ValidateOperationId(result.OperationId, "Extraction Results");
        ValidateDatabaseGeneration(result.DatabaseGeneration);

        if (result.OperationId == result.DatabaseGeneration.OperationId
            || !result.IsHierarchyAware
            || !result.DatabaseGeneration.IsHierarchyAware
            || result.Datasets is null
            || result.Datasets.Count != result.DatabaseGeneration.Datasets.Count
            || result.RowCount > result.DatabaseGeneration.RowCount
            || result.ValueCount > result.DatabaseGeneration.ValueCount)
        {
            throw InvalidContract(
                "An Extraction Result requires a distinct identity and hierarchy-aware Database snapshot.");
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
        ValidateDiscoveryInformationIdentity(occurrence.Identity);
        if (occurrence.Ordinal < 1
            || occurrence.TotalOccurrenceCount < occurrence.Ordinal
            || !SourceId.IsValid(occurrence.SourceId.Value)
            || occurrence.Value is null)
        {
            throw InvalidContract("A discovered occurrence is invalid.");
        }
    }

    private static void ValidateDiscoveryInformationIdentity(
        DiscoveryInformationIdentity identity)
    {
        if (identity is null
            || !SourceSetId.IsValid(identity.SourceSetId.Value)
            || string.IsNullOrWhiteSpace(identity.StructuralPath)
            || string.IsNullOrWhiteSpace(identity.InformationType)
            || !Enum.IsDefined(identity.CandidateKind)
            || string.IsNullOrWhiteSpace(identity.StructuralIdentity))
        {
            throw InvalidContract("A Discovery information identity is invalid.");
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

    private static void ValidateWorkingStateCommand(
        OperationCorrelation correlation,
        string path,
        WorkingStateSnapshot snapshot)
    {
        ValidateOperationCorrelation(correlation);
        ValidatePath(path);
        if (!string.Equals(Path.GetExtension(path), ".cia", StringComparison.OrdinalIgnoreCase)
            || snapshot is null)
        {
            throw InvalidContract("A working-state command requires a .cia path and typed snapshot.");
        }

        try
        {
            WorkingStateContractValidator.Validate(snapshot);
        }
        catch (ArgumentException exception)
        {
            throw InvalidContract(exception.Message);
        }
    }

    private static void ValidateWorkingStateResponse(
        Guid commandMessageId,
        CommandAcceptance acceptance,
        OperationCompletion completion,
        WorkingStateManifest? manifest,
        IpcFailure? failure)
    {
        ValidateVersionSevenId(commandMessageId, nameof(commandMessageId));
        if (!Enum.IsDefined(acceptance) || completion is null)
        {
            throw InvalidContract("The working-state response is incomplete or unsupported.");
        }

        ValidateOperationCorrelation(completion.Correlation);
        if (acceptance == CommandAcceptance.Accepted)
        {
            if (manifest is null
                || failure is not null
                || completion.Outcome is not (
                    OperationOutcome.CompletedSuccessfully or OperationOutcome.CompletedWithIssues))
            {
                throw InvalidContract("An accepted working-state response requires a manifest and completed outcome.");
            }

            WorkingStateContractValidator.Validate(manifest.Snapshot);
            return;
        }

        if (manifest is not null
            || failure is null
            || completion.Outcome is not (
                OperationOutcome.Failed
                or OperationOutcome.Cancelled
                or OperationOutcome.InterruptedIncomplete))
        {
            throw InvalidContract("An unsuccessful working-state response requires controlled failure context.");
        }

        ValidateFailure(failure);
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
