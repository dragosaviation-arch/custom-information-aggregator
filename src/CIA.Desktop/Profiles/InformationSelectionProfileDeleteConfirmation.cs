namespace CIA.Desktop.Profiles;

public interface IInformationSelectionProfileDeleteConfirmation
{
    bool IsOpen { get; }

    string? ProfileName { get; }

    event EventHandler? Changed;

    Task<bool> ConfirmAsync(
        string profileName,
        CancellationToken cancellationToken = default);

    void Accept();

    void Decline();
}

public sealed class InApplicationInformationSelectionProfileDeleteConfirmation :
    IInformationSelectionProfileDeleteConfirmation
{
    private TaskCompletionSource<bool>? _completion;

    public bool IsOpen => _completion is not null;

    public string? ProfileName { get; private set; }

    public event EventHandler? Changed;

    public Task<bool> ConfirmAsync(
        string profileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        if (_completion is not null)
        {
            throw new InvalidOperationException(
                "An information-selection profile deletion confirmation is already active.");
        }

        ProfileName = profileName;
        _completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Changed?.Invoke(this, EventArgs.Empty);
        return AwaitConfirmationAsync(_completion.Task, cancellationToken);
    }

    public void Accept() => Complete(accepted: true);

    public void Decline() => Complete(accepted: false);

    private void Complete(bool accepted)
    {
        var completion = Interlocked.Exchange(ref _completion, null);
        if (completion is null)
        {
            return;
        }

        ProfileName = null;
        Changed?.Invoke(this, EventArgs.Empty);
        completion.TrySetResult(accepted);
    }

    private async Task<bool> AwaitConfirmationAsync(
        Task<bool> confirmation,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state =>
        {
            ((InApplicationInformationSelectionProfileDeleteConfirmation)state!).Decline();
        }, this);
        return await confirmation.ConfigureAwait(false);
    }
}
