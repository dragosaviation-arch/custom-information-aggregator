using CIA.Contracts.Database;
using CIA.Contracts.Extraction;
using CIA.Contracts.Operations;

namespace CIA.Desktop.Extraction;

public interface IExtractionClient
{
    Task<ExtractionClientResult> ExtractAsync(
        OperationCorrelation correlation,
        DatabaseGenerationSummary databaseGeneration,
        CancellationToken cancellationToken = default);
}

public sealed record ExtractionClientResult(
    bool Accepted,
    OperationCompletion Completion,
    ExtractionResultSummary? PublishedResult,
    string? FailureCode,
    string? FailureDescription);
