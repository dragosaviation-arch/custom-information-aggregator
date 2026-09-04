using CIA.Core.Sources;

namespace CIA.ProcessingHost.SourceInterpretation;

public enum SourceInterpretationStatus
{
    Usable = 1,
    Unsupported = 2,
    FailedValidation = 3
}

public sealed record SourceInterpretationFailure(string Code, string Description);

public sealed class SourceInterpretationResult
{
    private SourceInterpretationResult(
        SourceInterpretationStatus status,
        InterpretedSourceDocument? source,
        SourceInterpretationFailure? failure)
    {
        Status = status;
        Source = source;
        Failure = failure;
        Validate();
    }

    public SourceInterpretationStatus Status { get; }

    public InterpretedSourceDocument? Source { get; }

    public SourceInterpretationFailure? Failure { get; }

    internal static SourceInterpretationResult Usable(InterpretedSourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new SourceInterpretationResult(
            SourceInterpretationStatus.Usable,
            source,
            failure: null);
    }

    internal static SourceInterpretationResult Unsupported(string code, string description)
    {
        return FailureResult(SourceInterpretationStatus.Unsupported, code, description);
    }

    internal static SourceInterpretationResult FailedValidation(string code, string description)
    {
        return FailureResult(SourceInterpretationStatus.FailedValidation, code, description);
    }

    private static SourceInterpretationResult FailureResult(
        SourceInterpretationStatus status,
        string code,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        return new SourceInterpretationResult(
            status,
            source: null,
            new SourceInterpretationFailure(code, description));
    }

    private void Validate()
    {
        if (!Enum.IsDefined(Status))
        {
            throw new ArgumentException("The source-interpretation status is not supported.");
        }

        if (Status == SourceInterpretationStatus.Usable)
        {
            if (Source is null || Failure is not null)
            {
                throw new ArgumentException(
                    "A usable source result requires interpreted content and cannot contain a failure.");
            }

            return;
        }

        if (Source is not null || Failure is null)
        {
            throw new ArgumentException(
                "An unsuccessful source result requires controlled failure information and no trusted content.");
        }
    }
}
