using Habita.Domain.Common;
using Habita.Domain.Maintenance;
using Xunit;

namespace Habita.UnitTests;

public class MaintenanceRulesTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(IssueStatus.Open, IssueStatus.InProgress, true)]
    [InlineData(IssueStatus.Open, IssueStatus.Waiting, true)]
    [InlineData(IssueStatus.Open, IssueStatus.Resolved, true)]
    [InlineData(IssueStatus.Open, IssueStatus.Closed, false)]       // closing requires resolving first
    [InlineData(IssueStatus.InProgress, IssueStatus.Waiting, true)]
    [InlineData(IssueStatus.Waiting, IssueStatus.InProgress, true)]
    [InlineData(IssueStatus.Resolved, IssueStatus.Closed, true)]
    [InlineData(IssueStatus.Resolved, IssueStatus.InProgress, true)] // reopen
    [InlineData(IssueStatus.Resolved, IssueStatus.Open, false)]
    [InlineData(IssueStatus.Closed, IssueStatus.Open, false)]       // closed is final
    [InlineData(IssueStatus.Closed, IssueStatus.InProgress, false)]
    [InlineData(IssueStatus.InProgress, IssueStatus.InProgress, false)]
    public void Transition_graph(IssueStatus from, IssueStatus to, bool allowed) => Assert.Equal(allowed, MaintenanceIssue.CanTransition(from, to));

    [Fact]
    public void Resolving_records_who_and_when_and_reopening_clears_them()
    {
        var user = Guid.NewGuid();
        var i = new MaintenanceIssue { Status = IssueStatus.InProgress };
        i.ChangeStatus(IssueStatus.Resolved, user, Now);
        Assert.Equal(Now, i.ResolvedAt);
        Assert.Equal(user, i.ResolvedByUserId);

        i.ChangeStatus(IssueStatus.InProgress, Guid.NewGuid(), Now.AddHours(1));
        Assert.Null(i.ResolvedAt);
        Assert.Null(i.ResolvedByUserId);
    }

    [Fact]
    public void Closing_keeps_the_resolution_data_and_a_closed_issue_cannot_move()
    {
        var user = Guid.NewGuid();
        var i = new MaintenanceIssue { Status = IssueStatus.Open };
        i.ChangeStatus(IssueStatus.Resolved, user, Now);
        i.ChangeStatus(IssueStatus.Closed, Guid.NewGuid(), Now.AddDays(1));
        Assert.Equal(IssueStatus.Closed, i.Status);
        Assert.Equal(user, i.ResolvedByUserId);
        Assert.Throws<DomainException>(() => i.ChangeStatus(IssueStatus.InProgress, user, Now));
    }

    [Fact]
    public void Invalid_transition_throws_with_a_code()
    {
        var ex = Assert.Throws<DomainException>(() => new MaintenanceIssue { Status = IssueStatus.Open }.ChangeStatus(IssueStatus.Closed, Guid.NewGuid(), Now));
        Assert.Equal("issue.invalid_transition", ex.Code);
    }

    [Theory]
    [InlineData(IssueStatus.InProgress, true)] [InlineData(IssueStatus.Waiting, true)] [InlineData(IssueStatus.Resolved, true)]
    [InlineData(IssueStatus.Open, false)] [InlineData(IssueStatus.Closed, false)]
    public void Maintenance_staff_cannot_close_or_reset_to_open(IssueStatus to, bool allowed) => Assert.Equal(allowed, MaintenanceIssue.MaintenanceMayMove(to));

    [Theory]
    [InlineData("Fuga en lavabo", "", true)]
    [InlineData("", "x", false)]
    [InlineData("   ", "x", false)]
    public void Title_is_required(string title, string description, bool valid) => Assert.Equal(valid, MaintenanceIssue.ValidateText(title, description) is null);

    [Fact]
    public void Text_and_comment_length_limits()
    {
        Assert.NotNull(MaintenanceIssue.ValidateText(new string('t', 201), ""));
        Assert.NotNull(MaintenanceIssue.ValidateText("t", new string('d', 4001)));
        Assert.Null(MaintenanceIssue.ValidateText(new string('t', 200), new string('d', 4000)));
        Assert.NotNull(MaintenanceIssueEvent.ValidateComment("  "));
        Assert.NotNull(MaintenanceIssueEvent.ValidateComment(new string('c', 2001)));
        Assert.Null(MaintenanceIssueEvent.ValidateComment(new string('c', 2000)));
    }
}
