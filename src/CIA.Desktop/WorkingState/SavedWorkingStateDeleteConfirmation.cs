namespace CIA.Desktop.WorkingState;

public interface ISavedWorkingStateDeleteConfirmation
{
    bool IsOpen { get; }

    string? StateName { get; }

    event EventHandler? Changed;

    Task<bool> ConfirmAsync(string stateName, CancellationToken cancellationToken = default);

    void Accept();

    void Decline();
}

public sealed class InApplicationSavedWorkingStateDeleteConfirmation :
    ISavedWorkingStateDeleteConfirmation
{
    private TaskCompletionSource<bool>? _completion;

    public bool IsOpen => _completion is not null;

    public string? StateName { get; private set; }

    public event EventHandler? Changed;

    public Task<bool> ConfirmAsync(
        string stateName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateName);
        if (_completion is not null)
        {
            throw new InvalidOperationException("A saved-state deletion confirmation is already active.");
        }

        StateName = stateName;
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

        StateName = null;
        Changed?.Invoke(this, EventArgs.Empty);
        completion.TrySetResult(accepted);
    }

    private async Task<bool> AwaitConfirmationAsync(
        Task<bool> confirmation,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state =>
        {
            ((InApplicationSavedWorkingStateDeleteConfirmation)state!).Decline();
        }, this);
        return await confirmation.ConfigureAwait(false);
    }
}
