namespace MyTranslator.Api.TaskOperations;

public sealed class TaskOperationLock
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, LockEntry> locks = [];

    public async ValueTask<IAsyncDisposable> AcquireAsync(Guid taskId, CancellationToken cancellationToken)
    {
        LockEntry entry;
        lock (sync)
        {
            if (!locks.TryGetValue(taskId, out entry!))
            {
                entry = new LockEntry();
                locks.Add(taskId, entry);
            }

            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
            return new Releaser(this, taskId, entry);
        }
        catch
        {
            ReleaseReference(taskId, entry);
            throw;
        }
    }

    private void Release(Guid taskId, LockEntry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(taskId, entry);
    }

    private void ReleaseReference(Guid taskId, LockEntry entry)
    {
        lock (sync)
        {
            entry.References--;
            if (entry.References == 0)
            {
                locks.Remove(taskId);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class LockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class Releaser(TaskOperationLock owner, Guid taskId, LockEntry entry) : IAsyncDisposable
    {
        private bool disposed;

        public ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                disposed = true;
                owner.Release(taskId, entry);
            }

            return ValueTask.CompletedTask;
        }
    }
}
