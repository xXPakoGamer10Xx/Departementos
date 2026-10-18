using Habita.Domain.Common;
using Habita.Domain.Leases;
using Xunit;

namespace Habita.UnitTests;

public class LeaseRulesTests
{
    static readonly DateOnly D = new(2026, 1, 1);
    static Lease Active(DateOnly? end = null) => new() { StartDate = D, EndDate = end, Status = LeaseStatus.Active };

    [Theory]
    [InlineData(0, true)] [InlineData(1, false)] [InlineData(28, false)] [InlineData(29, true)] [InlineData(-1, true)]
    public void DueDay_must_be_between_1_and_28(int dueDay, bool invalid) =>
        Assert.Equal(invalid, Lease.ValidateTerms(D, null, 8500m, dueDay, null, null) is not null);

    [Fact] public void End_before_start_is_invalid() => Assert.NotNull(Lease.ValidateTerms(D, D.AddDays(-1), 1m, 1, null, null));
    [Fact] public void Single_day_lease_is_valid() => Assert.Null(Lease.ValidateTerms(D, D, 1m, 1, null, null));
    [Fact] public void Negative_rent_is_invalid() => Assert.NotNull(Lease.ValidateTerms(D, null, -0.01m, 1, null, null));
    [Fact] public void Rent_with_three_decimals_is_invalid() => Assert.NotNull(Lease.ValidateTerms(D, null, 10.005m, 1, null, null));
    [Fact] public void Negative_deposit_is_invalid() => Assert.NotNull(Lease.ValidateTerms(D, null, 1m, 1, -1m, null));

    [Theory]
    [InlineData("2026-01-01", "2026-12-31", "2026-12-31", "2027-06-01", true)]  // touching on the last day overlaps
    [InlineData("2026-01-01", "2026-12-31", "2027-01-01", "2027-06-01", false)] // next day does not
    [InlineData("2026-01-01", null,         "2030-01-01", null,         true)]  // open-ended overlaps anything later
    [InlineData("2026-06-01", "2026-06-30", "2026-01-01", "2026-05-31", false)]
    public void Overlap_is_inclusive_of_both_endpoints(string s1, string? e1, string s2, string? e2, bool expected)
    {
        var l = new Lease { StartDate = DateOnly.Parse(s1), EndDate = e1 is null ? null : DateOnly.Parse(e1) };
        Assert.Equal(expected, l.Overlaps(DateOnly.Parse(s2), e2 is null ? null : DateOnly.Parse(e2)));
    }

    [Fact]
    public void Only_drafts_activate()
    {
        var l = new Lease(); l.Activate();
        Assert.Equal(LeaseStatus.Active, l.Status);
        Assert.Throws<DomainException>(() => l.Activate());
    }

    [Fact]
    public void Terminate_sets_end_date_reason_and_status_and_cannot_extend()
    {
        var l = Active(end: D.AddDays(100));
        Assert.Throws<DomainException>(() => l.Terminate(D.AddDays(101), "x"));
        Assert.Throws<DomainException>(() => l.Terminate(D.AddDays(-1), "x"));
        l.Terminate(D.AddDays(30), "Se mudó");
        Assert.Equal(LeaseStatus.Ended, l.Status);
        Assert.Equal(D.AddDays(30), l.EndDate);
        Assert.Equal("Se mudó", l.ClosureReason);
        Assert.Throws<DomainException>(() => l.Terminate(D.AddDays(10), "otra vez"));
    }

    [Fact]
    public void Cancel_works_from_draft_or_active_only()
    {
        var draft = new Lease(); draft.Cancel("Error"); Assert.Equal(LeaseStatus.Cancelled, draft.Status);
        var active = Active(); active.Cancel("Nunca inició"); Assert.Equal(LeaseStatus.Cancelled, active.Status);
        Assert.Throws<DomainException>(() => draft.Cancel("de nuevo"));
        var ended = Active(); ended.Terminate(D.AddDays(5), "x");
        Assert.Throws<DomainException>(() => ended.Cancel("x"));
    }

    [Fact]
    public void Ended_lease_still_reserves_until_its_end_date_but_cancelled_and_draft_never_do()
    {
        var ended = Active(); ended.Terminate(D.AddDays(30), "x");
        Assert.True(ended.InForce);
        Assert.True(ended.IsCurrent(D.AddDays(30)));
        Assert.False(ended.IsCurrent(D.AddDays(31)));
        Assert.False(new Lease { StartDate = D }.IsCurrent(D));
        Assert.False(new Lease { StartDate = D, Status = LeaseStatus.Cancelled }.InForce);
    }
}
