using System.Xml;
using System.Xml.Linq;
using CIA.Contracts.Sources;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.SourceInterpretation;

public interface ISourceOccurrenceReader
{
    Task<SourceOccurrenceReadResult> ReadAsync(
        LoadedSourceContract source,
        string informationType,
        string structuralPath,
        int localOrdinal,
        CancellationToken cancellationToken = default);
}

public sealed class SourceOccurrenceReader : ISourceOccurrenceReader
{
    private readonly IReadOnlyDictionary<XName, ISourceAdapter> _adaptersByRoot;
    private readonly IGenericXmlSourceAdapter _genericAdapter;
    private readonly ILogger<SourceOccurrenceReader> _logger;

    public SourceOccurrenceReader(
        IEnumerable<ISourceAdapter> adapters,
        ILogger<SourceOccurrenceReader> logger)
        : this(adapters, new GenericXmlElementValueSourceAdapter(), logger)
    {
    }

    public SourceOccurrenceReader(
        IEnumerable<ISourceAdapter> adapters,
        IGenericXmlSourceAdapter genericAdapter,
        ILogger<SourceOccurrenceReader> logger)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(genericAdapter);
        ArgumentNullException.ThrowIfNull(logger);

        _adaptersByRoot = adapters
            .ToDictionary(
                adapter => adapter.Declaration.RootElementName,
                adapter => adapter);
        _genericAdapter = genericAdapter;
        _logger = logger;
    }

    public async Task<SourceOccurrenceReadResult> ReadAsync(
        LoadedSourceContract source,
        string informationType,
        string structuralPath,
        int localOrdinal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(informationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(structuralPath);

        if (localOrdinal < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localOrdinal),
                "A source occurrence ordinal must be positive.");
        }

        if (source.Kind != LoadedSourceKind.XmlFile)
        {
            return SourceOccurrenceReadResult.Reject(
                "unsupported-input",
                "The loaded source is not an XML source.");
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(source.Path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return SourceOccurrenceReadResult.Reject(
                "invalid-source-path",
                "The source path is not valid for XML occurrence retrieval.");
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            return SourceOccurrenceReadResult.Reject(
                "unsupported-input",
                "The selected input is not an XML source.");
        }

        if (!File.Exists(fullPath))
        {
            return SourceOccurrenceReadResult.Reject(
                "source-not-found",
                "The selected XML source does not exist.");
        }

        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = XmlReader.Create(stream, SourceInterpreter.CreateReaderSettings());
            var nodeType = await reader.MoveToContentAsync().ConfigureAwait(false);

            if (nodeType != XmlNodeType.Element)
            {
                return SourceOccurrenceReadResult.Reject(
                    "missing-document-element",
                    "The XML source does not contain a document element.");
            }

            var rootName = XName.Get(reader.LocalName, reader.NamespaceURI);
            SourceOccurrenceRead occurrence;

            if (_adaptersByRoot.TryGetValue(rootName, out var specializedAdapter))
            {
                if (specializedAdapter is not ISourceOccurrenceAdapter occurrenceAdapter)
                {
                    return SourceOccurrenceReadResult.Reject(
                        "specialized-preview-unsupported",
                        "The specialized XML source adapter does not support occurrence preview.");
                }

                occurrence = await occurrenceAdapter
                    .ReadOccurrenceAsync(
                        source.SourceId,
                        reader,
                        informationType,
                        structuralPath,
                        localOrdinal,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                occurrence = await _genericAdapter
                    .ReadOccurrenceAsync(
                        source.SourceId,
                        reader,
                        informationType,
                        structuralPath,
                        localOrdinal,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return SourceOccurrenceReadResult.Accept(
                occurrence.Value,
                occurrence.OccurrenceCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (XmlException exception)
        {
            _logger.LogWarning(
                exception,
                "XML occurrence retrieval failed validation for {SourcePath}",
                fullPath);
            return SourceOccurrenceReadResult.Reject(
                "malformed-xml",
                "The XML source is malformed or contains prohibited XML constructs.");
        }
        catch (SourceAdapterValidationException exception)
        {
            _logger.LogWarning(
                exception,
                "XML occurrence retrieval failed for {SourcePath}",
                fullPath);
            return SourceOccurrenceReadResult.Reject(
                exception.FailureCode,
                exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "XML source could not be read for occurrence retrieval from {SourcePath}",
                fullPath);
            return SourceOccurrenceReadResult.Reject(
                "source-unreadable",
                "The XML source could not be read.");
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "XML occurrence retrieval failed for {SourcePath}",
                fullPath);
            return SourceOccurrenceReadResult.Reject(
                "source-occurrence-read-failed",
                "The XML source occurrence could not be retrieved.");
        }
    }
}

public sealed record SourceOccurrenceReadResult(
    bool Accepted,
    string? Value,
    int ActualOccurrenceCount,
    SourceInterpretationFailure? Failure)
{
    internal static SourceOccurrenceReadResult Accept(
        string? value,
        int actualOccurrenceCount)
    {
        return new SourceOccurrenceReadResult(
            true,
            value,
            actualOccurrenceCount,
            Failure: null);
    }

    internal static SourceOccurrenceReadResult Reject(string code, string description)
    {
        return new SourceOccurrenceReadResult(
            false,
            Value: null,
            ActualOccurrenceCount: 0,
            new SourceInterpretationFailure(code, description));
    }
}
