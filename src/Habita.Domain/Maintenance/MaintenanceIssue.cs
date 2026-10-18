using Habita.Domain.Common;

namespace Habita.Domain.Maintenance;

public enum IssuePriority { Low, Normal, High, Urgent }
public enum IssueStatus { Open, InProgress, Waiting, Resolved, Closed }
public enum IssueEventType { Created, StatusChanged, Comment, Assigned, Updated }

public class MaintenanceIssue : IOrganizationOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid? UnitId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public IssuePriority Priority { get; set; } = IssuePriority.Normal;
    public IssueStatus Status { get; set; } = IssueStatus.Open;
    public DateTimeOffset ReportedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    /// <summary>Set when a tenant reported it from the portal. That tenant (and no other) may follow it.</summary>
    public Guid? ReportedByTenantId { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Open ⇄ InProgress ⇄ Waiting, any of those → Resolved, Resolved → Closed or back to InProgress (reopen). Closed is final.</summary>
    public static bool CanTransition(IssueStatus from, IssueStatus to) => (from, to) switch
    {
        (IssueStatus.Open, IssueStatus.InProgress or IssueStatus.Waiting or IssueStatus.Resolved) => true,
        (IssueStatus.InProgress, IssueStatus.Open or IssueStatus.Waiting or IssueStatus.Resolved) => true,
        (IssueStatus.Waiting, IssueStatus.Open or IssueStatus.InProgress or IssueStatus.Resolved) => true,
        (IssueStatus.Resolved, IssueStatus.Closed or IssueStatus.InProgress) => true,
        _ => false,
    };

    /// <summary>Statuses a Maintenance user may set on an issue assigned to them. Closing stays with Owner/Admin.</summary>
    public static bool MaintenanceMayMove(IssueStatus to) => to is IssueStatus.InProgress or IssueStatus.Waiting or IssueStatus.Resolved;

    public static string? ValidateText(string title, string description)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200) return "El título es obligatorio y no puede superar 200 caracteres.";
        if (description.Length > 4000) return "La descripción no puede superar 4000 caracteres.";
        return null;
    }

    public void ChangeStatus(IssueStatus to, Guid actor, DateTimeOffset now)
    {
        if (!CanTransition(Status, to)) throw new DomainException("issue.invalid_transition", $"No se puede pasar de {Status} a {to}.");
        // Resolved requires who and when; leaving Resolved (reopening) clears them. The event history keeps the past.
        if (to == IssueStatus.Resolved) { ResolvedAt = now; ResolvedByUserId = actor; }
        else if (Status == IssueStatus.Resolved && to != IssueStatus.Closed) { ResolvedAt = null; ResolvedByUserId = null; }
        Status = to;
        UpdatedAt = now;
    }
}

/// <summary>Immutable history entry: creation, status changes, assignments, edits and comments.</summary>
public class MaintenanceIssueEvent : IOrganizationOwned
{
    public const int MaxCommentLength = 2000;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid IssueId { get; set; }
    public IssueEventType Type { get; set; }
    public Guid ActorUserId { get; set; }
    public IssueStatus? FromStatus { get; set; }
    public IssueStatus? ToStatus { get; set; }
    public Guid? TargetUserId { get; set; }
    public string? Body { get; set; }
    /// <summary>A comment the reporting tenant may read. Internal by default: nothing reaches a tenant unless someone chooses to.</summary>
    public bool TenantVisible { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public static string? ValidateComment(string? body) =>
        string.IsNullOrWhiteSpace(body) ? "El comentario no puede estar vacío." : body.Trim().Length > MaxCommentLength ? $"El comentario no puede superar {MaxCommentLength} caracteres." : null;
}
