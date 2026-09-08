using System.Globalization;
using System.Text.Json;
using CIA.Contracts.Diagnostics;
using CIA.Contracts.Operations;

namespace CIA.Core.Diagnostics;

public sealed class ClefProcessingHistoryReader : IProcessingHistoryReader
{
    public const int DefaultMaximumRecordsPerCategory = 200;
    private const int MaximumClefLineLength = 1_048_576;
    private readonly string _logDirectory;
    private readonly int _maximumRecordsPerCategory;

    public ClefProcessingHistoryReader(
        string logDirectory,
        int maximumRecordsPerCategory = DefaultMaximumRecordsPerCategory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        if (!Path.IsPathFullyQualified(logDirectory))
        {
            throw new ArgumentException("The history log directory must be an absolute path.", nameof(logDirectory));
        }

        if (maximumRecordsPerCategory <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRecordsPerCategory),
                maximumRecordsPerCategory,
                "The history result limit must be positive.");
        }

        _logDirectory = Path.GetFullPath(logDirectory);
        _maximumRecordsPerCategory = maximumRecordsPerCategory;
    }

    public ProcessingHistorySnapshot Read()
    {
        if (!Directory.Exists(_logDirectory))
        {
            return EmptySnapshot();
        }

        var attempts = new BoundedNewestSet<ProcessingAttemptRecord>(
            _maximumRecordsPerCategory,
            record => record.RecordedAtUtc,
            CreateAttemptKey);
        var diagnostics = new BoundedNewestSet<ProcessingDiagnosticRecord>(
            _maximumRecordsPerCategory,
            record => record.RecordedAtUtc,
            record => record.DiagnosticId.ToString());
        var readProblem = false;

        try
        {
            foreach (var filePath in EnumerateHistoryFiles())
            {
                try
                {
                    ReadFile(filePath, attempts, diagnostics);
                }
                catch (Exception exception) when (IsContainedReadFailure(exception))
                {
                    readProblem = true;
                }
            }
        }
        catch (Exception exception) when (IsContainedReadFailure(exception))
        {
            readProblem = true;
        }

        return new ProcessingHistorySnapshot(
            attempts.Items,
            diagnostics.Items,
            readProblem ? "Some processing history could not be read." : null);
    }

    private IEnumerable<string> EnumerateHistoryFiles()
    {
        return Directory.EnumerateFiles(
                _logDirectory,
                "cia-ui-*.clef",
                SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(
                _logDirectory,
                "cia-processing-host-*.clef",
                SearchOption.TopDirectoryOnly));
    }

    private static void ReadFile(
        string filePath,
        BoundedNewestSet<ProcessingAttemptRecord> attempts,
        BoundedNewestSet<ProcessingDiagnosticRecord> diagnostics)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.Length > MaximumClefLineLength)
            {
                continue;
            }

            TryReadLine(line, attempts, diagnostics);
        }
    }

    private static void TryReadLine(
        string line,
        BoundedNewestSet<ProcessingAttemptRecord> attempts,
        BoundedNewestSet<ProcessingDiagnosticRecord> diagnostics)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryGetString(root, "RecordType", out var recordType))
            {
                return;
            }

            if (recordType == "ProcessingAttempt"
                && TryReadAttempt(root, out var attempt))
            {
                attempts.Add(attempt);
            }
            else if (recordType == "ProcessingDiagnostic"
                     && TryReadDiagnostic(root, out var diagnostic))
            {
                diagnostics.Add(diagnostic);
            }
        }
        catch (Exception exception) when (exception is JsonException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or OverflowException
                                          or FormatException)
        {
            // A malformed, partially written, unrelated, or obsolete CLEF event is contained.
        }
    }

    private static bool TryReadAttempt(
        JsonElement root,
        out ProcessingAttemptRecord record)
    {
        record = null!;
        if (!TryReadCorrelation(root, out var correlation)
            || !TryGetString(root, "OperationName", out var operationName)
            || !TryGetOptionalString(root, "ProcessingStage", out var finalStage)
            || !TryGetUtcTimestamp(root, "HistoryRecordedAtUtc", out var recordedAtUtc)
            || !TryGetEnum(root, "TerminalOutcome", out OperationOutcome outcome)
            || !TryReadItems(root, "CompletedItems", OperationItemState.ProcessedSuccessfully, out var completed)
            || !TryReadItems(root, "FailedItems", OperationItemState.Failed, out var failed)
            || !TryReadItems(root, "UnprocessedItems", OperationItemState.Unprocessed, out var unprocessed))
        {
            return false;
        }

        var items = completed.Concat(failed).Concat(unprocessed).ToArray();
        var completion = new OperationCompletion(correlation, outcome, items);
        record = ProcessingAttemptRecord.FromCompletion(
            operationName,
            finalStage,
            recordedAtUtc,
            completion);
        return true;
    }

    private static bool TryReadDiagnostic(
        JsonElement root,
        out ProcessingDiagnosticRecord record)
    {
        record = null!;
        if (!TryGetGuid(root, "DiagnosticId", out var diagnosticId)
            || !TryReadCorrelation(root, out var correlation)
            || !TryGetString(root, "OperationName", out var operationName)
            || !TryGetOptionalString(root, "ProcessingStage", out var processingStage)
            || !TryGetOptionalString(root, "SourceId", out var sourceId)
            || !TryGetOptionalString(root, "ItemId", out var itemId)
            || !TryGetOptionalEnum(root, "ItemState", out OperationItemState? itemState)
            || !TryGetUtcTimestamp(root, "DiagnosticRecordedAtUtc", out var recordedAtUtc)
            || !TryGetOptionalEnum(root, "TerminalOutcome", out OperationOutcome? outcome)
            || !TryGetString(root, "UserFacingDescription", out var description)
            || !TryGetOptionalString(root, "FailureCode", out var failureCode)
            || !TryGetOptionalString(root, "TechnicalDetail", out var technicalDetail))
        {
            return false;
        }

        record = new ProcessingDiagnosticRecord(
            DiagnosticRecordId.From(diagnosticId),
            correlation,
            operationName,
            processingStage,
            sourceId,
            itemId,
            itemState,
            recordedAtUtc,
            outcome,
            description,
            failureCode,
            technicalDetail);
        return true;
    }

    private static bool TryReadCorrelation(
        JsonElement root,
        out OperationCorrelation correlation)
    {
        correlation = null!;
        if (!TryGetGuid(root, "OperationId", out var operationId)
            || !TryGetUtcTimestamp(root, "OperationInitiatedAtUtc", out var initiatedAtUtc))
        {
            return false;
        }

        correlation = new OperationCorrelation(OperationId.From(operationId), initiatedAtUtc);
        return true;
    }

    private static bool TryReadItems(
        JsonElement root,
        string propertyName,
        OperationItemState state,
        out IReadOnlyList<OperationItemStatus> items)
    {
        items = [];
        if (!root.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<OperationItemStatus>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !TryGetString(element, "ItemId", out var itemId)
                || !TryGetOptionalString(element, "FailureCode", out var failureCode))
            {
                return false;
            }

            var dependencies = Array.Empty<string>();
            if (element.TryGetProperty("RequiredItemIds", out var requiredItemIds))
            {
                if (requiredItemIds.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                dependencies = requiredItemIds.EnumerateArray()
                    .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                    .Where(value => value is not null)
                    .Cast<string>()
                    .ToArray();
                if (dependencies.Length != requiredItemIds.GetArrayLength())
                {
                    return false;
                }
            }

            parsed.Add(new OperationItemStatus(itemId, state, dependencies, failureCode));
        }

        items = parsed;
        return true;
    }

    private static bool TryGetString(
        JsonElement root,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString()!);
    }

    private static bool TryGetOptionalString(
        JsonElement root,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is null || !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetGuid(
        JsonElement root,
        string propertyName,
        out Guid value)
    {
        value = Guid.Empty;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.TryGetGuid(out value);
    }

    private static bool TryGetUtcTimestamp(
        JsonElement root,
        string propertyName,
        out DateTimeOffset value)
    {
        value = default;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                property.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out value)
            && value.Offset == TimeSpan.Zero;
    }

    private static bool TryGetEnum<TEnum>(
        JsonElement root,
        string propertyName,
        out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && Enum.TryParse(property.GetString(), ignoreCase: false, out value)
            && Enum.IsDefined(value);
    }

    private static bool TryGetOptionalEnum<TEnum>(
        JsonElement root,
        string propertyName,
        out TEnum? value)
        where TEnum : struct, Enum
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String
            || !Enum.TryParse(property.GetString(), ignoreCase: false, out TEnum parsed)
            || !Enum.IsDefined(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string CreateAttemptKey(ProcessingAttemptRecord record)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{record.Correlation.OperationId}|{record.RecordedAtUtc:O}|{record.OperationName}");
    }

    private static ProcessingHistorySnapshot EmptySnapshot()
    {
        return new ProcessingHistorySnapshot([], [], ReadProblem: null);
    }

    private static bool IsContainedReadFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException
            or PathTooLongException;
    }

    private sealed class BoundedNewestSet<T>(
        int maximumCount,
        Func<T, DateTimeOffset> timestampSelector,
        Func<T, string> keySelector)
    {
        private readonly List<T> _items = [];
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        public IReadOnlyList<T> Items => _items;

        public void Add(T item)
        {
            var key = keySelector(item);
            if (!_keys.Add(key))
            {
                return;
            }

            var timestamp = timestampSelector(item);
            var insertAt = _items.FindIndex(existing => timestampSelector(existing) < timestamp);
            if (insertAt < 0)
            {
                _items.Add(item);
            }
            else
            {
                _items.Insert(insertAt, item);
            }

            if (_items.Count <= maximumCount)
            {
                return;
            }

            var removed = _items[^1];
            _items.RemoveAt(_items.Count - 1);
            _keys.Remove(keySelector(removed));
        }
    }
}
