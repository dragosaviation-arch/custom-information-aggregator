using System.Text.Json;
using System.Text.Json.Serialization;
using CIA.Contracts.Operations;
using CIA.Desktop.Ipc;
using CIA.ProcessingHost.Ipc;

namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class OperationCorrelationTests
{
    [TestMethod]
    public void NewOperationsReceiveUniqueUuidVersionSevenIdentifiers()
    {
        var correlations = Enumerable
            .Range(0, 100)
            .Select(_ => OperationCorrelation.CreateNew())
            .ToArray();

        Assert.IsTrue(correlations.All(correlation => correlation.OperationId.Value != Guid.Empty));
        Assert.IsTrue(correlations.All(correlation => correlation.OperationId.Value.Version == 7));
        Assert.AreEqual(
            correlations.Length,
            correlations.Select(correlation => correlation.OperationId).Distinct().Count());
    }

    [TestMethod]
    public void OperationCorrelationIsImmutableAndUsesUtcDateTimeOffset()
    {
        var initiatedAtUtc = new DateTimeOffset(2026, 9, 3, 10, 30, 0, TimeSpan.Zero);
        var correlation = OperationCorrelation.CreateNew(initiatedAtUtc);
        var capturedOperationId = correlation.OperationId;

        Assert.AreEqual(capturedOperationId, correlation.OperationId);
        Assert.AreEqual(initiatedAtUtc, correlation.InitiatedAtUtc);
        Assert.AreEqual(TimeSpan.Zero, correlation.InitiatedAtUtc.Offset);
        Assert.AreEqual(
            typeof(DateTimeOffset),
            typeof(OperationCorrelation).GetProperty(nameof(OperationCorrelation.InitiatedAtUtc))?.PropertyType);
        Assert.IsFalse(
            typeof(OperationCorrelation).GetProperties().Any(property => property.SetMethod is not null));
    }

    [TestMethod]
    public void OperationCorrelationRejectsNonUtcTechnicalTimestamp()
    {
        var nonUtcTimestamp = new DateTimeOffset(2026, 9, 3, 10, 30, 0, TimeSpan.FromHours(3));

        Assert.ThrowsExactly<ArgumentException>(
            () => OperationCorrelation.CreateNew(nonUtcTimestamp));
    }

    [TestMethod]
    public void ReinitiatedAttemptReceivesNewIdentityWithoutChangingPriorAttempt()
    {
        var priorAttempt = OperationCorrelation.CreateNew(
            new DateTimeOffset(2026, 9, 3, 10, 30, 0, TimeSpan.Zero));
        var priorOperationId = priorAttempt.OperationId;

        var reinitiatedAttempt = priorAttempt.CreateReinitiatedAttempt(
            new DateTimeOffset(2026, 9, 3, 10, 31, 0, TimeSpan.Zero));

        Assert.AreEqual(priorOperationId, priorAttempt.OperationId);
        Assert.AreNotEqual(priorAttempt.OperationId, reinitiatedAttempt.OperationId);
        Assert.AreEqual(7, reinitiatedAttempt.OperationId.Value.Version);
    }

    [TestMethod]
    public void CorrelationAndOutcomeRoundTripAcrossSharedJsonBoundary()
    {
        var correlation = OperationCorrelation.CreateNew(
            new DateTimeOffset(2026, 9, 3, 10, 30, 0, TimeSpan.Zero));
        var command = new CorrelatedCommand(correlation);
        var commandJson = JsonSerializer.Serialize(command, SerializerOptions);
        var hostCommand = JsonSerializer.Deserialize<CorrelatedCommand>(commandJson, SerializerOptions);

        Assert.IsNotNull(hostCommand);
        Assert.AreEqual(correlation, hostCommand.Correlation);

        var hostEvent = new CorrelatedOutcomeEvent(
            hostCommand.Correlation.OperationId,
            new DateTimeOffset(2026, 9, 3, 10, 31, 0, TimeSpan.Zero),
            OperationOutcome.CompletedWithIssues);
        var eventJson = JsonSerializer.Serialize(hostEvent, SerializerOptions);
        var uiEvent = JsonSerializer.Deserialize<CorrelatedOutcomeEvent>(eventJson, SerializerOptions);

        Assert.IsNotNull(uiEvent);
        Assert.AreEqual(correlation.OperationId, uiEvent.OperationId);
        Assert.AreEqual(TimeSpan.Zero, uiEvent.TimestampUtc.Offset);
        Assert.AreEqual(OperationOutcome.CompletedWithIssues, uiEvent.Outcome);
        StringAssert.Contains(commandJson, $"\"operationId\":\"{correlation.OperationId}\"");
        StringAssert.Contains(eventJson, "\"outcome\":\"completedWithIssues\"");
    }

    [TestMethod]
    public void OutcomesMatchApprovedTerminalOperationModel()
    {
        OperationOutcome[] expectedOutcomes =
        [
            OperationOutcome.CompletedSuccessfully,
            OperationOutcome.CompletedWithIssues,
            OperationOutcome.Failed,
            OperationOutcome.Cancelled,
            OperationOutcome.InterruptedIncomplete
        ];

        CollectionAssert.AreEqual(expectedOutcomes, Enum.GetValues<OperationOutcome>());
        Assert.IsFalse(Enum.IsDefined(default(OperationOutcome)));
    }

    [TestMethod]
    public void SharedPrimitivesPreserveProductionProjectBoundaries()
    {
        var contractsAssemblyName = typeof(OperationCorrelation).Assembly.GetName().Name;
        var desktopReferences = typeof(ProcessingHostIpcClient).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        var hostReferences = typeof(ProcessingHostIpcServer).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        var contractsReferences = typeof(OperationCorrelation).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        CollectionAssert.Contains(desktopReferences, contractsAssemblyName);
        CollectionAssert.Contains(hostReferences, contractsAssemblyName);
        CollectionAssert.DoesNotContain(contractsReferences, "CIA.Desktop");
        CollectionAssert.DoesNotContain(contractsReferences, "CIA.ProcessingHost");
        CollectionAssert.DoesNotContain(contractsReferences, "CIA.Core");
        CollectionAssert.DoesNotContain(contractsReferences, "PresentationFramework");
    }

    [TestMethod]
    public void InvalidOperationIdsCannotBecomeSharedCorrelationIdentity()
    {
        Assert.ThrowsExactly<ArgumentException>(() => OperationId.From(Guid.Empty));
        Assert.ThrowsExactly<ArgumentException>(() => OperationId.From(Guid.NewGuid()));
        Assert.ThrowsExactly<ArgumentException>(
            () => new OperationCorrelation(
                default,
                new DateTimeOffset(2026, 9, 3, 10, 30, 0, TimeSpan.Zero)));

        Assert.ThrowsExactly<JsonException>(
            () => JsonSerializer.Deserialize<OperationId>(
                $"\"{Guid.NewGuid()}\"",
                SerializerOptions));
    }

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private sealed record CorrelatedCommand(OperationCorrelation Correlation);

    private sealed record CorrelatedOutcomeEvent(
        OperationId OperationId,
        DateTimeOffset TimestampUtc,
        OperationOutcome Outcome);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
