using Habita.Application.Abstractions;
using Habita.Application.Notifications;
using Habita.Domain.Organizations;
using Habita.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Habita.Infrastructure.Services;

/// <summary>
/// The two jobs of the background worker, kept out of the worker project so tests can run them directly. Each organization is handled in its own
/// scope with its own context, so every query and write goes through the normal organization filter. One organization failing never stops the others.
/// </summary>
public class NotificationJobs(IServiceScopeFactory scopes, IClock clock, ILogger<NotificationJobs> log)
{
    private const int MaxBatchesPerOrganization = 10;

    /// <summary>Produces the daily reminders of every active organization whose reminder hour has arrived. Returns how many notifications were created.</summary>
    public async Task<int> RunRemindersAsync(CancellationToken ct)
    {
        List<Guid> ids;
        await using (var scope = scopes.CreateAsyncScope())
            ids = await scope.ServiceProvider.GetRequiredService<HabitaDbContext>().Organizations.AsNoTracking()
                .Where(o => o.Status == OrganizationStatus.Active).Select(o => o.Id).ToListAsync(ct);

        var created = 0;
        foreach (var id in ids)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ICurrentOrganization>().Set(id, Role.Owner, OrganizationStatus.Active);
                var run = await scope.ServiceProvider.GetRequiredService<ReminderService>().RunIfDueAsync(ct);
                if (run.Ran) { created += run.Created; log.LogInformation("Recordatorios de la organización {Org}: {Created} nuevos.", id, run.Created); }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Falló la corrida de recordatorios de la organización {Org}.", id); }
        }
        return created;
    }

    /// <summary>Sends the email and push copies that are due. Returns how many notifications were handled.</summary>
    public async Task<int> DispatchAsync(CancellationToken ct)
    {
        List<Guid> ids;
        await using (var scope = scopes.CreateAsyncScope())
            ids = await scope.ServiceProvider.GetRequiredService<INotificationStore>().OrganizationsWithPendingAsync(clock.UtcNow, ct);

        var handled = 0;
        foreach (var id in ids)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ICurrentOrganization>().Set(id, Role.Owner, OrganizationStatus.Active);
                var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
                for (var i = 0; i < MaxBatchesPerOrganization; i++)
                {
                    var n = await dispatcher.DispatchBatchAsync(ct);
                    handled += n;
                    if (n < NotificationDispatcher.BatchSize) break;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Falló la entrega de avisos de la organización {Org}.", id); }
        }
        return handled;
    }
}
