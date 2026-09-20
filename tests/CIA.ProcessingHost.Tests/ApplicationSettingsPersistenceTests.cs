using System.Text.Json;
using CIA.Contracts.Sources;
using CIA.Core.Runtime;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class ApplicationSettingsPersistenceTests
{
    [TestMethod]
    public void MissingSettingsUsesControlledDefaultsWithoutCreatingAFile()
    {
        using var environment = new SettingsTestEnvironment();
        var store = environment.CreateStore();

        var result = store.Load();

        Assert.AreEqual(
            ApplicationSettingsReadState.DefaultsBecauseFileMissing,
            result.State);
        Assert.AreEqual(3, result.Settings.MaximumArchiveNestingDepth.Value);
        Assert.IsTrue(result.Settings.TraverseSubfolders);
        Assert.IsFalse(result.Settings.PersistentArchiveExtractionEnabled);
        Assert.AreEqual(PostExportBehavior.StatusOnly, result.Settings.PostExportBehavior);
        Assert.IsFalse(File.Exists(result.SettingsFilePath));
        Assert.IsFalse(File.Exists(store.BootstrapFilePath));
    }

    [TestMethod]
    public void VersionedSettingsRoundTripAcrossServiceInstancesAndResolveConfiguredPaths()
    {
        using var environment = new SettingsTestEnvironment();
        var first = environment.CreateService();
        var configured = first.Current with
        {
            TemporaryDirectory = environment.PathFor("Configured", "Temp"),
            WorkingDirectory = environment.PathFor("Configured", "Working"),
            ProfilesDirectory = environment.PathFor("Configured", "Profiles"),
            SettingsDirectory = environment.PathFor("Configured", "Settings"),
            TraverseSubfolders = false,
            MaximumArchiveNestingDepth = ArchiveNestingDepth.From(7),
            PersistentArchiveExtractionEnabled = true,
            PersistentArchiveExtractionDirectory = environment.PathFor("ArchiveOutput"),
            LastUsedOutputDirectory = environment.CreateDirectory("Exports"),
            PostExportBehavior = PostExportBehavior.OpenContainingFolder
        };

        Assert.IsTrue(first.Save(configured).Succeeded);
        var reopened = environment.CreateService();

        Assert.AreEqual(ApplicationSettings.CurrentSchemaVersion, reopened.Current.SchemaVersion);
        Assert.AreEqual(configured, reopened.Current);
        Assert.AreEqual(configured.TemporaryDirectory, reopened.RuntimePaths.TempDirectory);
        Assert.AreEqual(configured.WorkingDirectory, reopened.RuntimePaths.WorkingDirectory);
        Assert.AreEqual(configured.ProfilesDirectory, reopened.RuntimePaths.ProfilesDirectory);
        Assert.AreEqual(configured.SettingsDirectory, reopened.RuntimePaths.SettingsDirectory);
        Assert.AreEqual(ApplicationSettingsReadState.Loaded, reopened.Startup.State);

        using var json = JsonDocument.Parse(File.ReadAllText(reopened.Startup.SettingsFilePath));
        Assert.AreEqual(
            ApplicationSettings.CurrentSchemaVersion,
            json.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [TestMethod]
    public void AtomicWriteFailurePreservesPreviouslyPublishedSettings()
    {
        using var environment = new SettingsTestEnvironment();
        var initialStore = environment.CreateStore();
        var initial = initialStore.Load().Settings with
        {
            MaximumArchiveNestingDepth = ArchiveNestingDepth.From(4)
        };
        Assert.IsTrue(initialStore.Save(initial).Succeeded);
        var previousContent = File.ReadAllText(
            Path.Combine(initial.SettingsDirectory, ApplicationSettingsStore.SettingsFileName));
        var failingStore = environment.CreateStore(new AlwaysFailingWriter());

        var result = failingStore.Save(initial with
        {
            MaximumArchiveNestingDepth = ArchiveNestingDepth.From(9)
        });

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(
            previousContent,
            File.ReadAllText(Path.Combine(
                initial.SettingsDirectory,
                ApplicationSettingsStore.SettingsFileName)));
        Assert.AreEqual(4, initialStore.Load().Settings.MaximumArchiveNestingDepth.Value);
    }

    [TestMethod]
    public void MalformedAndFutureSettingsFallBackWithoutOverwritingTheirSource()
    {
        using var environment = new SettingsTestEnvironment();
        var store = environment.CreateStore();
        var defaultSettings = store.Load().Settings;
        Directory.CreateDirectory(defaultSettings.SettingsDirectory);
        var settingsPath = Path.Combine(
            defaultSettings.SettingsDirectory,
            ApplicationSettingsStore.SettingsFileName);
        File.WriteAllText(settingsPath, "{not-json");

        var malformed = store.Load();

        Assert.AreEqual(
            ApplicationSettingsReadState.DefaultsBecauseSettingsInvalid,
            malformed.State);
        Assert.IsNotNull(malformed.Diagnostic);
        Assert.AreEqual("{not-json", File.ReadAllText(settingsPath));

        var futureJson = JsonSerializer.Serialize(defaultSettings with
        {
            SchemaVersion = ApplicationSettings.CurrentSchemaVersion + 1
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(settingsPath, futureJson);
        var future = store.Load();

        Assert.AreEqual(
            ApplicationSettingsReadState.DefaultsBecauseVersionUnsupported,
            future.State);
        StringAssert.Contains(future.Diagnostic, "newer");
        Assert.AreEqual(futureJson, File.ReadAllText(settingsPath));
    }

    [TestMethod]
    public void SettingsDirectoryChangePublishesDocumentBeforeBootstrapAndSurvivesRestart()
    {
        using var environment = new SettingsTestEnvironment();
        var service = environment.CreateService();
        var newSettingsDirectory = environment.PathFor("RelocatedSettings");

        Assert.IsTrue(service.Save(service.Current with
        {
            SettingsDirectory = newSettingsDirectory
        }).Succeeded);

        Assert.IsTrue(File.Exists(Path.Combine(
            newSettingsDirectory,
            ApplicationSettingsStore.SettingsFileName)));
        Assert.IsTrue(File.Exists(environment.CreateStore().BootstrapFilePath));
        var reopened = environment.CreateService();
        Assert.AreEqual(newSettingsDirectory, reopened.Current.SettingsDirectory);
        Assert.AreEqual(newSettingsDirectory, reopened.RuntimePaths.SettingsDirectory);
    }

    [TestMethod]
    public void FailedSettingsDirectoryBootstrapDoesNotRepointRestart()
    {
        using var environment = new SettingsTestEnvironment();
        var initial = environment.CreateService();
        Assert.IsTrue(initial.Save(initial.Current with
        {
            MaximumArchiveNestingDepth = ArchiveNestingDepth.From(4)
        }).Succeeded);
        var target = environment.PathFor("UnpublishedSettingsLocation");
        var writer = new BootstrapFailingWriter(
            new AtomicSettingsFileWriter(),
            environment.CreateStore().BootstrapFilePath);
        var failingService = new ApplicationSettingsService(environment.CreateStore(writer));

        var save = failingService.Save(failingService.Current with
        {
            SettingsDirectory = target,
            MaximumArchiveNestingDepth = ArchiveNestingDepth.From(8)
        });

        Assert.IsFalse(save.Succeeded);
        var reopened = environment.CreateService();
        Assert.AreEqual(4, reopened.Current.MaximumArchiveNestingDepth.Value);
        Assert.AreNotEqual(target, reopened.Current.SettingsDirectory);
    }

    [TestMethod]
    public void IndependentDesktopAndHostResolversReadTheSamePersistentState()
    {
        using var environment = new SettingsTestEnvironment();
        var desktop = environment.CreateService();
        var extractionDirectory = environment.PathFor("PersistentArchive");
        Assert.IsTrue(desktop.Save(desktop.Current with
        {
            TraverseSubfolders = false,
            MaximumArchiveNestingDepth = ArchiveNestingDepth.From(5),
            PersistentArchiveExtractionEnabled = true,
            PersistentArchiveExtractionDirectory = extractionDirectory,
            PostExportBehavior = PostExportBehavior.AskEachTime
        }).Succeeded);

        var processingHost = environment.CreateService();

        Assert.AreEqual(desktop.Current, processingHost.Current);
        CollectionAssert.AreEqual(
            desktop.RuntimePaths.WritableDirectories.ToArray(),
            processingHost.RuntimePaths.WritableDirectories.ToArray());
        Assert.IsFalse(processingHost.Current.TraverseSubfolders);
        Assert.AreEqual(5, processingHost.Current.MaximumArchiveNestingDepth.Value);
        Assert.AreEqual(extractionDirectory, processingHost.Current.PersistentArchiveExtractionDirectory);
        Assert.AreEqual(PostExportBehavior.AskEachTime, processingHost.Current.PostExportBehavior);
    }

    [TestMethod]
    public void PersistentDocumentContainsNoKnownTransientSessionState()
    {
        using var environment = new SettingsTestEnvironment();
        var service = environment.CreateService();
        Assert.IsTrue(service.Save(service.Current).Succeeded);
        var json = File.ReadAllText(service.Startup.SettingsFilePath);

        foreach (var transientName in new[]
                 {
                     "sourceSet", "selectedRow", "searchText", "filter", "collision",
                     "alwaysAskWhereToExport", "renamedFile"
                 })
        {
            Assert.IsFalse(json.Contains(transientName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class AlwaysFailingWriter : IAtomicSettingsFileWriter
    {
        public void Write(string finalPath, ReadOnlyMemory<byte> content) =>
            throw new IOException("Injected settings publication failure.");
    }

    private sealed class BootstrapFailingWriter(
        IAtomicSettingsFileWriter inner,
        string bootstrapPath) : IAtomicSettingsFileWriter
    {
        public void Write(string finalPath, ReadOnlyMemory<byte> content)
        {
            if (string.Equals(finalPath, bootstrapPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Injected bootstrap publication failure.");
            }

            inner.Write(finalPath, content);
        }
    }

    private sealed class SettingsTestEnvironment : IDisposable
    {
        private static readonly string TestRoot = Path.Combine(
            Path.GetTempPath(),
            "CIA.SPR96.Tests");

        public SettingsTestEnvironment()
        {
            Root = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
            LocalApplicationData = Path.Combine(Root, "LocalAppData");
            Directory.CreateDirectory(LocalApplicationData);
        }

        public string Root { get; }

        public string LocalApplicationData { get; }

        public string PathFor(params string[] parts) =>
            parts.Aggregate(Root, Path.Combine);

        public string CreateDirectory(params string[] parts)
        {
            var path = PathFor(parts);
            Directory.CreateDirectory(path);
            return path;
        }

        public ApplicationSettingsStore CreateStore(IAtomicSettingsFileWriter? writer = null) =>
            new(LocalApplicationData, writer);

        public ApplicationSettingsService CreateService() => new(CreateStore());

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            var resolvedRoot = Path.GetFullPath(Root);
            var allowedRoot = Path.GetFullPath(TestRoot)
                .TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!resolvedRoot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to remove a path outside the test root.");
            }

            Directory.Delete(resolvedRoot, recursive: true);
        }
    }
}
