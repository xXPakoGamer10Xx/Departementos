using Habita.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Habita.Infrastructure.Persistence;

/// <summary>
/// The next receipt number. One counter row per organization: the first payment creates it, every later one raises it by one and keeps the row locked until its
/// transaction ends. Two payments at once therefore get different, consecutive numbers, and a payment that fails gives its number back (no gaps).
/// </summary>
public class ReceiptFolios(HabitaDbContext db, ICurrentOrganization org) : IReceiptFolios
{
    public async Task<int> NextAsync(CancellationToken ct)
    {
        var orgId = org.Id ?? throw new InvalidOperationException("No hay organización activa.");
        var transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException("El folio de un recibo se asigna dentro de la transacción del pago.");
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            INSERT INTO "ReceiptCounters" ("OrganizationId","LastFolio") VALUES (@org, 1)
            ON CONFLICT ("OrganizationId") DO UPDATE SET "LastFolio" = "ReceiptCounters"."LastFolio" + 1
            RETURNING "LastFolio"
            """;
        var parameter = command.CreateParameter(); parameter.ParameterName = "org"; parameter.Value = orgId; command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }
}
