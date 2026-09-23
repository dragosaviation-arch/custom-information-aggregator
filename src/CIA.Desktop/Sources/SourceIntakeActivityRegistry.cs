using CIA.Contracts.Sources;

namespace CIA.Desktop.Sources;

public sealed class SourceIntakeActivityRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<SourceIntakeActivityId> _active = [];

    public IDisposable Begin(SourceIntakeActivityId activityId)
    {
        _ = SourceIntakeActivityId.From(activityId.Value);
        lock (_gate)
        {
            if (!_active.Add(activityId))
            {
                throw new InvalidOperationException(
                    "The source-intake activity is already active.");
            }
        }

        return new Registration(this, activityId);
    }

    public IReadOnlyList<SourceIntakeActivityId> CreateSnapshot()
    {
        lock (_gate)
        {
            return _active.OrderBy(activity => activity.Value).ToArray();
        }
    }

    private void End(SourceIntakeActivityId activityId)
    {
        lock (_gate)
        {
            _active.Remove(activityId);
        }
    }

    private sealed class Registration(
        SourceIntakeActivityRegistry owner,
        SourceIntakeActivityId activityId) : IDisposable
    {
        private SourceIntakeActivityRegistry? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.End(activityId);
        }
    }
}
