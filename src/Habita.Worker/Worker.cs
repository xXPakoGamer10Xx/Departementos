using Habita.Infrastructure.Services;

namespace Habita.Worker;

/// <summary>
/// Two loops: the daily reminders (checked every few minutes; each organization runs once per local day, from its reminder hour) and the delivery of
/// email and push copies (every few seconds). The loops are safe to run in several copies at once: the database locks decide who does what.
/// </summary>
public class Worker(NotificationJobs jobs, IConfiguration config, ILogger<Worker> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stop) => Task.WhenAll(
        Loop("recordatorios", TimeSpan.FromMinutes(config.GetValue("Worker:ReminderCheckMinutes", 5.0)), jobs.RunRemindersAsync, stop),
        Loop("entrega de avisos", TimeSpan.FromSeconds(config.GetValue("Worker:DispatchSeconds", 15.0)), jobs.DispatchAsync, stop));

    private async Task Loop(string name, TimeSpan every, Func<CancellationToken, Task<int>> job, CancellationToken stop)
    {
        var heartbeat = config["Worker:HeartbeatFile"] ?? Path.Combine(Path.GetTempPath(), "habita-worker-alive");
        log.LogInformation("Worker: {Name} cada {Every}.", name, every);
        using var timer = new PeriodicTimer(every);
        try
        {
            do
            {
                try
                {
                    var n = await job(stop);
                    if (n > 0) log.LogInformation("Worker: {Name}: {N} elementos.", name, n);
                    await File.WriteAllTextAsync(heartbeat, DateTimeOffset.UtcNow.ToString("O"), stop); // lets the container health check see that the loop is alive
                }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Worker: falló {Name}; se reintenta en el siguiente ciclo.", name); }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
