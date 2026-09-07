using System.Xml;
using System.Xml.Linq;
using CIA.Contracts.Sources;
using CIA.Core.Runtime;
using CIA.Core.Sources;
using CIA.ProcessingHost.SourceIntake;
using CIA.ProcessingHost.SourceInterpretation;
using Microsoft.Extensions.Logging.Abstractions;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class SourceInterpreterTests
{
    private static readonly XNamespace CatalogNamespace = "urn:cia:test:catalog";

    [TestMethod]
    public async Task DeclaredSupportedNonAircraftXmlIsInterpretedWithoutChangingValues()
    {
        using var files = new TemporaryXmlDirectory();
        var path = files.WriteFile(
            "catalog.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <catalog xmlns="urn:cia:test:catalog">
              <product>
                <sku>  MiXeD-Case-01  </sku>
                <description>  Exact &amp; represented content  </description>
              </product>
            </catalog>
            """);
        var intakeResult = await files.CreateIntakeService().LoadAsync(
            SourceSelectionKind.XmlFile,
            path,
            SourceLoadSettings.Default);
        var interpreter = CreateInterpreter(new CatalogSourceAdapter());

        Assert.IsTrue(intakeResult.Accepted);
        Assert.HasCount(1, intakeResult.Sources);

        var result = await interpreter.InterpretAsync(intakeResult.Sources[0]);

        Assert.AreEqual(SourceInterpretationStatus.Usable, result.Status);
        Assert.IsNull(result.Failure);
        Assert.IsNotNull(result.Source);
        Assert.AreEqual(intakeResult.Sources[0].SourceId, result.Source.OriginatingSourceId);
        Assert.AreEqual(CatalogSourceAdapter.StructureId, result.Source.StructureId);
        Assert.HasCount(2, result.Source.Values);
        Assert.AreEqual("ProductSku", result.Source.Values[0].InformationType);
        Assert.AreEqual("  MiXeD-Case-01  ", result.Source.Values[0].Content);
        Assert.AreEqual("ProductDescription", result.Source.Values[1].InformationType);
        Assert.AreEqual("  Exact & represented content  ", result.Source.Values[1].Content);
    }

    [TestMethod]
    public async Task MalformedDeclaredXmlReturnsControlledValidationFailureWithoutTrustedContent()
    {
        using var files = new TemporaryXmlDirectory();
        var path = files.WriteFile(
            "malformed.xml",
            "<catalog xmlns=\"urn:cia:test:catalog\"><product><sku>A</sku></catalog>");
        var interpreter = CreateInterpreter(new CatalogSourceAdapter());

        var result = await interpreter.InterpretAsync(CreateLoadedXml(path));

        Assert.AreEqual(SourceInterpretationStatus.FailedValidation, result.Status);
        Assert.AreEqual("malformed-xml", result.Failure?.Code);
        Assert.IsNull(result.Source);
    }

    [TestMethod]
    public async Task InvalidDeclaredContentReturnsAdapterValidationFailure()
    {
        using var files = new TemporaryXmlDirectory();
        var path = files.WriteFile(
            "missing-required-content.xml",
            """
            <catalog xmlns="urn:cia:test:catalog">
              <product><description>Missing the required SKU</description></product>
            </catalog>
            """);
        var interpreter = CreateInterpreter(new CatalogSourceAdapter());

        var result = await interpreter.InterpretAsync(CreateLoadedXml(path));

        Assert.AreEqual(SourceInterpretationStatus.FailedValidation, result.Status);
        Assert.AreEqual("missing-required-content", result.Failure?.Code);
        Assert.IsNull(result.Source);
    }

    [TestMethod]
    public async Task UndeclaredXmlUsesGenericFallbackWhileNonXmlRemainsUnsupported()
    {
        using var files = new TemporaryXmlDirectory();
        var undeclaredXml = files.WriteFile(
            "unknown.xml",
            "<unknown><value>generic content</value></unknown>");
        var nonXml = files.WriteFile("notes.txt", "not an XML source");
        var interpreter = CreateInterpreter(new CatalogSourceAdapter());

        var undeclaredResult = await interpreter.InterpretAsync(CreateLoadedXml(undeclaredXml));
        var nonXmlResult = await interpreter.InterpretAsync(CreateLoadedXml(nonXml));

        Assert.AreEqual(SourceInterpretationStatus.Usable, undeclaredResult.Status);
        Assert.AreEqual(
            GenericXmlElementValueSourceAdapter.GenericStructureId,
            undeclaredResult.Source?.StructureId);
        Assert.AreEqual("generic content", undeclaredResult.Source?.Values.Single().Content);
        Assert.AreEqual(SourceInterpretationStatus.Unsupported, nonXmlResult.Status);
        Assert.AreEqual("unsupported-input", nonXmlResult.Failure?.Code);
        Assert.IsNull(nonXmlResult.Source);
    }

    [TestMethod]
    public async Task ProhibitedDtdReturnsControlledValidationFailure()
    {
        using var files = new TemporaryXmlDirectory();
        var path = files.WriteFile(
            "dtd.xml",
            """
            <!DOCTYPE catalog [<!ENTITY hidden "not allowed">]>
            <catalog xmlns="urn:cia:test:catalog">
              <product><sku>&hidden;</sku><description>description</description></product>
            </catalog>
            """);
        var interpreter = CreateInterpreter(new CatalogSourceAdapter());

        var result = await interpreter.InterpretAsync(CreateLoadedXml(path));

        Assert.AreEqual(SourceInterpretationStatus.FailedValidation, result.Status);
        Assert.AreEqual("malformed-xml", result.Failure?.Code);
        Assert.IsNull(result.Source);
    }

    [TestMethod]
    public void GenericCoreSourceModelContainsNoXmlOrProcessingHostBoundaryTypes()
    {
        var publicPropertyTypes = typeof(InterpretedSourceDocument)
            .Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(InterpretedSourceDocument).Namespace)
            .SelectMany(type => type.GetProperties())
            .Select(property => property.PropertyType)
            .ToArray();

        Assert.IsFalse(publicPropertyTypes.Any(type => type.Namespace?.StartsWith("System.Xml", StringComparison.Ordinal) == true));
        Assert.IsFalse(publicPropertyTypes.Any(type => type.Namespace?.StartsWith("CIA.ProcessingHost", StringComparison.Ordinal) == true));
        Assert.IsFalse(
            typeof(InterpretedSourceDocument).Assembly.GetReferencedAssemblies().Any(
                assembly => assembly.Name == "CIA.ProcessingHost"));
    }

    [TestMethod]
    public void DuplicateDeclaredStructuresAreRejectedBeforeInterpretation()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(
            () => CreateInterpreter(new CatalogSourceAdapter(), new CatalogSourceAdapter()));

        StringAssert.Contains(exception.Message, CatalogSourceAdapter.StructureId);
    }

    [TestMethod]
    public async Task SupportedInterpretationLeavesOriginalSourceUnchangedAndCreatesNoSiblingData()
    {
        using var files = new TemporaryXmlDirectory();
        var path = files.WriteFile(
            "immutable.xml",
            """
            <catalog xmlns="urn:cia:test:catalog">
              <product><sku>SKU-1</sku><description>Original content</description></product>
            </catalog>
            """);
        var originalBytes = await File.ReadAllBytesAsync(path);
        var originalWriteTime = File.GetLastWriteTimeUtc(path);
        var intakeResult = await files.CreateIntakeService().LoadAsync(
            SourceSelectionKind.XmlFile,
            path,
            SourceLoadSettings.Default);
        var interpreter = CreateInterpreter(new CatalogSourceAdapter());

        var result = await interpreter.InterpretAsync(intakeResult.Sources[0]);

        Assert.AreEqual(SourceInterpretationStatus.Usable, result.Status);
        CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(originalWriteTime, File.GetLastWriteTimeUtc(path));
        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(path) },
            Directory.GetFiles(files.Path).Select(Path.GetFullPath).ToArray());
    }

    [TestMethod]
    public async Task ArchiveProvenancePassesThroughTheGenericInterpreterBoundary()
    {
        using var files = new TemporaryXmlDirectory();
        var path = files.WriteFile(
            "catalog.xml",
            "<catalog xmlns=\"urn:cia:test:catalog\"><product><sku>A</sku><description>B</description></product></catalog>");
        var archiveId = SourceId.CreateNew();
        var provenance = new ArchiveSourceProvenance(
            archiveId,
            Path.GetFullPath(Path.Combine(files.Path, "original.zip")),
            [new ArchiveLineageItem(archiveId, Path.GetFullPath(Path.Combine(files.Path, "original.zip")), 1)],
            ArchiveNestingLevel: 1,
            ArchiveMemberPath: "catalog.xml",
            ExtractionRoot: files.Path,
            ArchiveExtractionRetention.ManagedTemporary,
            ArchiveNestingDepth.Default,
            PersistentExtractionDirectory: null);
        var source = CreateLoadedXml(path) with { ArchiveProvenance = provenance };
        var interpreter = CreateInterpreter(new CatalogSourceAdapter());

        var result = await interpreter.InterpretAsync(source);

        Assert.AreEqual(SourceInterpretationStatus.Usable, result.Status);
        Assert.AreEqual(source.SourceId, result.Source?.OriginatingSourceId);
        Assert.AreEqual(provenance, result.Source?.ArchiveProvenance);
    }

    [TestMethod]
    public async Task AdapterCannotReplaceOriginatingSourceIdentity()
    {
        using var files = new TemporaryXmlDirectory();
        var path = files.WriteFile("catalog.xml", "<catalog xmlns=\"urn:cia:test:catalog\" />");
        var loadedSource = CreateLoadedXml(path);
        var interpreter = CreateInterpreter(new ReplacingIdentitySourceAdapter());

        var result = await interpreter.InterpretAsync(loadedSource);

        Assert.AreEqual(SourceInterpretationStatus.FailedValidation, result.Status);
        Assert.AreEqual("invalid-adapter-result", result.Failure?.Code);
        Assert.IsNull(result.Source);
    }

    private static SourceInterpreter CreateInterpreter(params ISourceAdapter[] adapters)
    {
        return new SourceInterpreter(adapters, NullLogger<SourceInterpreter>.Instance);
    }

    private static LoadedSourceContract CreateLoadedXml(string path)
    {
        return new LoadedSourceContract(
            SourceId.CreateNew(),
            path,
            IsIncluded: true,
            LoadedSourceStatus.Ready,
            LoadedSourceKind.XmlFile);
    }

    private sealed class CatalogSourceAdapter : ISourceAdapter
    {
        public const string StructureId = "test.catalog.v1";

        public SourceStructureDeclaration Declaration { get; } = new(
            StructureId,
            CatalogNamespace + "catalog");

        public async ValueTask<InterpretedSourceDocument> InterpretAsync(
            SourceId originatingSourceId,
            XmlReader reader,
            CancellationToken cancellationToken = default)
        {
            var root = await XElement
                .LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken)
                .ConfigureAwait(false);
            var values = new List<InterpretedSourceValue>();

            foreach (var product in root.Elements(CatalogNamespace + "product"))
            {
                var sku = product.Element(CatalogNamespace + "sku");
                var description = product.Element(CatalogNamespace + "description");

                if (sku is null || description is null)
                {
                    throw new SourceAdapterValidationException(
                        "missing-required-content",
                        "A catalog product requires both SKU and description content.");
                }

                values.Add(new InterpretedSourceValue("ProductSku", sku.Value));
                values.Add(new InterpretedSourceValue("ProductDescription", description.Value));
            }

            return new InterpretedSourceDocument(originatingSourceId, StructureId, values);
        }
    }

    private sealed class ReplacingIdentitySourceAdapter : ISourceAdapter
    {
        public SourceStructureDeclaration Declaration { get; } = new(
            CatalogSourceAdapter.StructureId,
            CatalogNamespace + "catalog");

        public async ValueTask<InterpretedSourceDocument> InterpretAsync(
            SourceId originatingSourceId,
            XmlReader reader,
            CancellationToken cancellationToken = default)
        {
            await XElement.LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken);
            return new InterpretedSourceDocument(
                SourceId.CreateNew(),
                CatalogSourceAdapter.StructureId,
                Array.Empty<InterpretedSourceValue>());
        }
    }

    private sealed class TemporaryXmlDirectory : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CIA.SPR65.Tests");

        public TemporaryXmlDirectory()
        {
            Path = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public SourceIntakeService CreateIntakeService()
        {
            var applicationPaths = ApplicationPaths.FromLocalApplicationData(
                System.IO.Path.Combine(Path, "LocalAppData"));
            return new SourceIntakeService(new ArchiveExtractionService(applicationPaths));
        }

        public string WriteFile(string fileName, string content)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            var resolvedRoot = System.IO.Path.GetFullPath(_root)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var resolvedTarget = System.IO.Path.GetFullPath(Path);

            if (!resolvedTarget.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing to delete an XML-interpretation test directory outside its root.");
            }

            Directory.Delete(resolvedTarget, recursive: true);
        }
    }
}
