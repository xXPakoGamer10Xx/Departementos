using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Habita.Application.Abstractions;
using Habita.Domain.Audit;
using Habita.Domain.Organizations;
using Habita.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Habita.Infrastructure.Services;

public class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public class CurrentOrganization : ICurrentOrganization
{
    public Guid? Id { get; private set; }
    public Role? Role { get; private set; }
    public OrganizationStatus? Status { get; private set; }
    public Guid? TenantId { get; private set; }
    public void Set(Guid id, Role role, OrganizationStatus status, Guid? tenantId = null) { Id = id; Role = role; Status = status; TenantId = tenantId; }
}

public class AuditWriter(HabitaDbContext db, ICurrentOrganization org, ICurrentUser user, IClock clock) : IAuditWriter
{
    public void Write(string action, string entityType, Guid entityId, string? reason = null, object? metadata = null) =>
        db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = org.Id ?? throw new InvalidOperationException("No hay organización activa."),
            ActorUserId = user.UserId ?? throw new InvalidOperationException("No hay usuario autenticado."),
            Action = action, EntityType = entityType, EntityId = entityId,
            OccurredAt = clock.UtcNow, Reason = reason,
            Metadata = metadata is null ? null : JsonSerializer.Serialize(metadata),
        });
}

public class SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        var host = config["Smtp:Host"];
        if (string.IsNullOrEmpty(host)) { log.LogWarning("SMTP no configurado; correo '{Subject}' no enviado.", subject); return; }
        using var client = new SmtpClient(host, config.GetValue("Smtp:Port", 1025))
        {
            EnableSsl = config.GetValue("Smtp:EnableSsl", false),
        };
        if (config["Smtp:User"] is { Length: > 0 } u) client.Credentials = new NetworkCredential(u, config["Smtp:Password"]);
        await client.SendMailAsync(new MailMessage(config["Smtp:From"] ?? "no-reply@habita.local", to, subject, body), ct);
    }
}
