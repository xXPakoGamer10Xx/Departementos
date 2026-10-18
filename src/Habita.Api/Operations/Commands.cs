using Habita.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Habita.Api.Operations;

public static class Commands
{
    /// <summary>Returns true when a one-off command ran (the process should exit instead of serving requests).</summary>
    public static async Task<bool> RunAsync(WebApplication app, string[] args)
    {
        if (args.Contains("--migrate"))
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HabitaDbContext>();
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            await db.Database.MigrateAsync();
            Console.WriteLine(pending.Count == 0 ? "La base de datos ya estaba al día." : $"Se aplicaron {pending.Count} migraciones: {string.Join(", ", pending)}");
            return true;
        }
        if (args.Contains("--generate-vapid"))
        {
            var keys = WebPush.VapidHelper.GenerateVapidKeys();
            Console.WriteLine($"Push__VapidPublicKey={keys.PublicKey}");
            Console.WriteLine($"Push__VapidPrivateKey={keys.PrivateKey}");
            Console.WriteLine("Guarda la llave privada como un secreto: quien la tenga puede enviar avisos como Habita.");
            return true;
        }
        if (args.Contains("--seed-demo"))
        {
            if (Security.ProductionGuards.IsProduction(app.Environment))
            {
                Console.Error.WriteLine("--seed-demo solo está permitido en Development: crea usuarios con una contraseña de ejemplo conocida.");
                Environment.ExitCode = 2;
                return true;
            }
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<HabitaDbContext>().Database.MigrateAsync();
            Console.WriteLine(await DemoSeeder.RunAsync(scope.ServiceProvider));
            return true;
        }
        return false;
    }
}
