using System.Xml;
using System.Xml.Linq;
using CIA.Contracts.Sources;
using CIA.Core.Sources;
using Microsoft.Extensions.Logging;

namespace CIA.ProcessingHost.SourceInterpretation;

public interface ISourceInterpreter
{
    Task<SourceInterpretationResult> InterpretAsync(
        LoadedSourceContract source,
        CancellationToken cancellationToken = default);
}

public sealed class SourceInterpreter : ISourceInterpreter
{
    private readonly IReadOnlyDictionary<XName, ISourceAdapter> _adaptersByRoot;
    private readonly ILogger<SourceInterpreter> _logger;

    public SourceInterpreter(
        IEnumerable<ISourceAdapter> adapters,
        ILogger<SourceInterpreter> logger)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(logger);

        var adapterArray = adapters.ToArray();
        ValidateDeclarations(adapterArray);
        _adaptersByRoot = adapterArray.ToDictionary(
            adapter => adapter.Declaration.RootElementName);
        _logger = logger;
    }

    public async Task<SourceInterpretationResult> InterpretAsync(
        LoadedSourceContract source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Kind != LoadedSourceKind.XmlFile)
        {
            return SourceInterpretationResult.Unsupported(
                "unsupported-input",
                "The loaded source is not an XML source.");
        }

        var sourcePath = source.Path;

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return SourceInterpretationResult.Unsupported(
                "invalid-source-path",
                "A source path is required for XML interpretation.");
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(sourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return SourceInterpretationResult.Unsupported(
                "invalid-source-path",
                "The source path is not valid for XML interpretation.");
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            return SourceInterpretationResult.Unsupported(
                "unsupported-input",
                "The selected input is not an XML source.");
        }

        if (!File.Exists(fullPath))
        {
            return SourceInterpretationResult.FailedValidation(
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
            using var reader = XmlReader.Create(stream, CreateReaderSettings());
            var nodeType = await reader.MoveToContentAsync().ConfigureAwait(false);

            if (nodeType != XmlNodeType.Element)
            {
                return SourceInterpretationResult.FailedValidation(
                    "missing-document-element",
                    "The XML source does not contain a document element.");
            }

            var rootName = XName.Get(reader.LocalName, reader.NamespaceURI);

            if (!_adaptersByRoot.TryGetValue(rootName, out var adapter))
            {
                return SourceInterpretationResult.Unsupported(
                    "unsupported-xml-structure",
                    "The XML document structure is not declared supported by this release.");
            }

            var interpretedSource = await adapter
                .InterpretAsync(source.SourceId, reader, cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!string.Equals(
                    interpretedSource.StructureId,
                    adapter.Declaration.StructureId,
                    StringComparison.Ordinal)
                || interpretedSource.OriginatingSourceId != source.SourceId)
            {
                return SourceInterpretationResult.FailedValidation(
                    "invalid-adapter-result",
                    "The source adapter returned content for a different source or declared structure.");
            }

            return SourceInterpretationResult.Usable(
                new InterpretedSourceDocument(
                    interpretedSource.OriginatingSourceId,
                    interpretedSource.StructureId,
                    interpretedSource.Values,
                    source.ArchiveProvenance));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (XmlException exception)
        {
            _logger.LogWarning(
                exception,
                "XML source validation failed for {SourcePath}",
                fullPath);
            return SourceInterpretationResult.FailedValidation(
                "malformed-xml",
                "The XML source is malformed or contains prohibited XML constructs.");
        }
        catch (SourceAdapterValidationException exception)
        {
            _logger.LogWarning(
                exception,
                "Declared XML source validation failed for {SourcePath}",
                fullPath);
            return SourceInterpretationResult.FailedValidation(
                exception.FailureCode,
                exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "XML source could not be read from {SourcePath}", fullPath);
            return SourceInterpretationResult.FailedValidation(
                "source-unreadable",
                "The XML source could not be read.");
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "XML source adapter failed for {SourcePath}",
                fullPath);
            return SourceInterpretationResult.FailedValidation(
                "source-interpretation-failed",
                "The XML source could not be interpreted by its declared adapter.");
        }
    }

    private static XmlReaderSettings CreateReaderSettings()
    {
        return new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = true,
            CloseInput = false,
            ConformanceLevel = ConformanceLevel.Document,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = false,
            IgnoreWhitespace = false,
            XmlResolver = null
        };
    }

    private static void ValidateDeclarations(IReadOnlyList<ISourceAdapter> adapters)
    {
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            ArgumentNullException.ThrowIfNull(adapter.Declaration);
        }

        var duplicateStructure = adapters
            .GroupBy(adapter => adapter.Declaration.StructureId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateStructure is not null)
        {
            throw new ArgumentException(
                $"Source structure '{duplicateStructure.Key}' is declared by more than one adapter.",
                nameof(adapters));
        }

        var duplicateRoot = adapters
            .GroupBy(adapter => adapter.Declaration.RootElementName)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateRoot is not null)
        {
            throw new ArgumentException(
                $"XML root '{duplicateRoot.Key}' is declared by more than one source adapter.",
                nameof(adapters));
        }
    }
}
