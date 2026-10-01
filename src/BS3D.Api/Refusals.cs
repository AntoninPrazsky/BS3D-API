using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BS3D.Api;

/// <summary>
/// Every refusal, gathered in memory so the admin page (issue #5) can show them from the database and never from the
/// journal, whose read access is every unit's lines (#4 item 1). Per UTC day and reason they go to
/// <c>refusal_days</c>, all of them; the recent ones go to <c>refusal_log</c>, as many as
/// <see cref="ScoresOptions.RefusalQueueCapacity"/> per flush. Nothing is written on the request path:
/// <see cref="RefusalFlusher"/> takes what was gathered and writes it in one transaction.
/// </summary>
public sealed class Refusals(IOptions<ScoresOptions> options)
{
    /// <summary>What a refusal's log line says, and when. <c>Detail</c> never holds an address (#2, v0.1.2).</summary>
    public sealed record Entry(DateTimeOffset At, int Status, string Reason, string Method, string Path, string Detail);

    /// <summary>What one flush writes.</summary>
    public sealed record Batch(IReadOnlyDictionary<(string Day, string Reason), int> Counts, IReadOnlyList<Entry> Entries, int Dropped)
    {
        public bool IsEmpty => Counts.Count == 0;
    }

    /// <summary>A request path or a detail is attacker text; this much of it is kept.</summary>
    public const int TextLength = 200;

    private readonly object _gate = new();
    private Dictionary<(string Day, string Reason), int> _counts = new();
    private List<Entry> _entries = new();
    private int _dropped;

    public void Record(DateTimeOffset at, int status, string reason, string method, string path, string detail)
    {
        (string, string) key = (ScoreStore.DayOf(at), reason);
        lock (_gate)
        {
            _counts[key] = _counts.GetValueOrDefault(key) + 1;
            if (_entries.Count < options.Value.RefusalQueueCapacity)
                _entries.Add(new Entry(at, status, reason, Cut(method), Cut(path), Cut(detail)));
            else
                _dropped++;
        }
    }

    /// <summary>Everything gathered since the last call, which starts the next gathering empty.</summary>
    public Batch Take()
    {
        lock (_gate)
        {
            Batch batch = new(_counts, _entries, _dropped);
            _counts = new();
            _entries = new();
            _dropped = 0;
            return batch;
        }
    }

    private static string Cut(string text) => text.Length <= TextLength ? text : text[..TextLength] + "…";
}

/// <summary>
/// Writes what <see cref="Refusals"/> gathered every <see cref="ScoresOptions.RefusalFlushSeconds"/>, and once more
/// when the service stops, then prunes <c>refusal_log</c> to its age and size. A failed write is logged and its batch
/// lost: the refusals were also logged one by one, and the service keeps answering.
/// </summary>
public sealed class RefusalFlusher(Refusals refusals, ScoreStore store, TimeProvider clock, IOptions<ScoresOptions> options,
    ILogger<RefusalFlusher> log) : BackgroundService
{
    // Made with the service, not in ExecuteAsync, which .NET 10 starts on the thread pool: the period then counts from
    // the service's start, whenever the loop gets its thread
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(Math.Max(1, options.Value.RefusalFlushSeconds)), clock);

    public override void Dispose()
    {
        _timer.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(stoppingToken)) Flush();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Flush();
    }

    public void Flush()
    {
        Refusals.Batch batch = refusals.Take();
        if (batch.IsEmpty) return;
        ScoresOptions o = options.Value;
        try
        {
            using SqliteConnection c = store.Open();
            store.WriteRefusals(c, batch, clock.GetUtcNow(), TimeSpan.FromDays(o.RefusalLogDays), o.RefusalLogMaxRows);
        }
        catch (SqliteException e)
        {
            log.LogWarning("Could not write {Count} refusal(s) to the database: {Error}", batch.Counts.Values.Sum(), e.Message);
        }
    }
}
