using System.Threading.Channels;

namespace RSS_API.Services;

public sealed class SubstackRefreshQueue
{
    private readonly Lock _lock = new();
    private readonly Channel<SubstackRefreshJob> _jobs = Channel.CreateBounded<SubstackRefreshJob>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false
        });

    private SubstackRefreshJobStatus _status = SubstackRefreshJobStatus.Idle;

    public bool TryQueue(
        Func<CancellationToken, Task<SubstackCacheDocument>> work,
        out SubstackRefreshJobStatus status)
    {
        lock (_lock)
        {
            if (_status.State is "queued" or "running")
            {
                status = _status;
                return false;
            }

            var job = new SubstackRefreshJob(Guid.NewGuid(), work);
            _status = new SubstackRefreshJobStatus(
                job.Id,
                "queued",
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                null,
                null,
                null);

            if (!_jobs.Writer.TryWrite(job))
            {
                throw new InvalidOperationException("The Substack refresh queue could not accept the job.");
            }

            status = _status;
            return true;
        }
    }

    public ValueTask<SubstackRefreshJob> DequeueAsync(CancellationToken cancellationToken) =>
        _jobs.Reader.ReadAsync(cancellationToken);

    public SubstackRefreshJobStatus GetStatus()
    {
        lock (_lock)
        {
            return _status;
        }
    }

    public void MarkRunning(Guid jobId)
    {
        lock (_lock)
        {
            if (_status.JobId == jobId)
            {
                _status = _status with { State = "running", StartedAt = DateTimeOffset.UtcNow };
            }
        }
    }

    public void MarkCompleted(Guid jobId, SubstackCacheDocument cache)
    {
        lock (_lock)
        {
            if (_status.JobId == jobId)
            {
                _status = _status with
                {
                    State = "completed",
                    CompletedAt = DateTimeOffset.UtcNow,
                    FeedCount = cache.Response.FeedCount,
                    PostCount = cache.Response.PostCount,
                    ErrorCount = cache.Response.Errors.Count,
                    Error = null
                };
            }
        }
    }

    public void MarkFailed(Guid jobId, Exception exception)
    {
        lock (_lock)
        {
            if (_status.JobId == jobId)
            {
                _status = _status with
                {
                    State = "failed",
                    CompletedAt = DateTimeOffset.UtcNow,
                    Error = exception.Message
                };
            }
        }
    }
}

public sealed record SubstackRefreshJob(
    Guid Id,
    Func<CancellationToken, Task<SubstackCacheDocument>> Work);

public sealed record SubstackRefreshJobStatus(
    Guid? JobId,
    string State,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? FeedCount,
    int? PostCount,
    int? ErrorCount,
    string? Error)
{
    public static SubstackRefreshJobStatus Idle { get; } =
        new(null, "idle", null, null, null, null, null, null, null);
}

public sealed class SubstackRefreshWorker(
    SubstackRefreshQueue queue,
    ILogger<SubstackRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            SubstackRefreshJob job;
            try
            {
                job = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            queue.MarkRunning(job.Id);
            try
            {
                var cache = await job.Work(stoppingToken);
                queue.MarkCompleted(job.Id, cache);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                queue.MarkFailed(job.Id, new InvalidOperationException("The application stopped before the refresh completed."));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Substack refresh job {JobId} failed.", job.Id);
                queue.MarkFailed(job.Id, exception);
            }
        }
    }
}
