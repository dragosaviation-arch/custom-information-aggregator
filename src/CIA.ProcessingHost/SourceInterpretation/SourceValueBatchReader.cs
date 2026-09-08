using System.Xml;
using System.Xml.Linq;
using CIA.Contracts.Sources;
using CIA.Core.Sources;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.SourceInterpretation;

public interface ISourceValueBatchReader
{
    Task<SourceValueBatchReadResult> ReadSelectedAsync(
        LoadedSourceContract source,
        IReadOnlySet<string> selectedInformationTypes,
        Func<IReadOnlyList<InterpretedSourceValue>, ValueTask> onBatch,
        CancellationToken cancellationToken = default);
}

public sealed class SourceValueBatchReader : ISourceValueBatchReader
{
    private readonly IReadOnlyDictionary<XName, ISourceAdapter> _adaptersByRoot;
    private readonly IGenericXmlSourceAdapter _genericAdapter;
    private readonly ILogger<SourceValueBatchReader> _logger;

    public SourceValueBatchReader(
        IEnumerable<ISourceAdapter> adapters,
        IGenericXmlSourceAdapter genericAdapter,
        ILogger<SourceValueBatchReader> logger)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(genericAdapter);
        ArgumentNullException.ThrowIfNull(logger);

        _adaptersByRoot = adapters.ToDictionary(
            adapter => adapter.Declaration.RootElementName,
            adapter => adapter);
        _genericAdapter = genericAdapter;
        _logger = logger;
    }

    public async Task<SourceValueBatchReadResult> ReadSelectedAsync(
        LoadedSourceContract source,
        IReadOnlySet<string> selectedInformationTypes,
        Func<IReadOnlyList<InterpretedSourceValue>, ValueTask> onBatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selectedInformationTypes);
        ArgumentNullException.ThrowIfNull(onBatch);

        if (selectedInformationTypes.Count == 0)
        {
            throw new ArgumentException(
                "At least one selected information type is required.",
                nameof(selectedInformationTypes));
        }

        if (source.Kind != LoadedSourceKind.XmlFile)
        {
            return SourceValueBatchReadResult.Reject(
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
            return SourceValueBatchReadResult.Reject(
                "invalid-source-path",
                "The source path is not valid for Database generation.");
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            return SourceValueBatchReadResult.Reject(
                "unsupported-input",
                "The selected input is not an XML source.");
        }

        if (!File.Exists(fullPath))
        {
            return SourceValueBatchReadResult.Reject(
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
                return SourceValueBatchReadResult.Reject(
                    "missing-document-element",
                    "The XML source does not contain a document element.");
            }

            var rootName = XName.Get(reader.LocalName, reader.NamespaceURI);
            ISourceValueBatchAdapter batchAdapter;
            if (_adaptersByRoot.TryGetValue(rootName, out var specializedAdapter))
            {
                if (specializedAdapter is not ISourceValueBatchAdapter specializedBatchAdapter)
                {
                    return SourceValueBatchReadResult.Reject(
                        "specialized-database-generation-unsupported",
                        "The specialized XML source adapter does not support Database generation.");
                }

                batchAdapter = specializedBatchAdapter;
            }
            else if (_genericAdapter is ISourceValueBatchAdapter genericBatchAdapter)
            {
                batchAdapter = genericBatchAdapter;
            }
            else
            {
                return SourceValueBatchReadResult.Reject(
                    "generic-database-generation-unsupported",
                    "The generic XML source adapter does not support Database generation.");
            }

            var valueCount = await batchAdapter
                .ReadSelectedValuesAsync(
                    source.SourceId,
                    reader,
                    selectedInformationTypes,
                    onBatch,
                    cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return SourceValueBatchReadResult.Accept(valueCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (XmlException exception)
        {
            _logger.LogWarning(
                exception,
                "XML Database generation failed validation for {SourcePath}",
                fullPath);
            return SourceValueBatchReadResult.Reject(
                "malformed-xml",
                "The XML source is malformed or contains prohibited XML constructs.");
        }
        catch (SourceAdapterValidationException exception)
        {
            _logger.LogWarning(
                exception,
                "XML Database generation failed for {SourcePath}",
                fullPath);
            return SourceValueBatchReadResult.Reject(
                exception.FailureCode,
                exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "XML source could not be read for Database generation from {SourcePath}",
                fullPath);
            return SourceValueBatchReadResult.Reject(
                "source-unreadable",
                "The XML source could not be read.");
        }
    }
}

public sealed record SourceValueBatchReadResult(
    bool Accepted,
    int ValueCount,
    SourceInterpretationFailure? Failure)
{
    internal static SourceValueBatchReadResult Accept(int valueCount)
    {
        return new SourceValueBatchReadResult(true, valueCount, Failure: null);
    }

    internal static SourceValueBatchReadResult Reject(string code, string description)
    {
        return new SourceValueBatchReadResult(
            false,
            ValueCount: 0,
            new SourceInterpretationFailure(code, description));
    }
}
