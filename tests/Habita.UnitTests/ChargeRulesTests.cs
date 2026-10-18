using Habita.Domain.Charges;
using Habita.Domain.Common;
using Xunit;

namespace Habita.UnitTests;

public class ChargeRulesTests
{
    static readonly DateOnly Due = new(2026, 10, 5);

    [Theory]
    [InlineData("2026-10-04", ChargeStatus.Open)]
    [InlineData("2026-10-05", ChargeStatus.Open)]      // due today is not overdue yet
    [InlineData("2026-10-06", ChargeStatus.Overdue)]
    public void Open_becomes_overdue_only_after_the_due_date(string today, ChargeStatus expected) =>
        Assert.Equal(expected, RentCharge.Effective(ChargeStatus.Open, Due, DateOnly.Parse(today)));

    [Fact]
    public void Partially_paid_past_due_is_overdue_and_paid_or_voided_never_are()
    {
        var late = Due.AddDays(30);
        Assert.Equal(ChargeStatus.Overdue, RentCharge.Effective(ChargeStatus.PartiallyPaid, Due, late));
        Assert.Equal(ChargeStatus.Paid, RentCharge.Effective(ChargeStatus.Paid, Due, late));
        Assert.Equal(ChargeStatus.Voided, RentCharge.Effective(ChargeStatus.Voided, Due, late));
    }

    [Theory]
    [InlineData(1000, 0, ChargeStatus.Open)]
    [InlineData(1000, 700, ChargeStatus.PartiallyPaid)]
    [InlineData(1000, 1000, ChargeStatus.Paid)]
    public void Status_follows_the_allocated_amount(decimal amount, decimal allocated, ChargeStatus expected) =>
        Assert.Equal(expected, RentCharge.StatusFor(amount, allocated));

    [Theory]
    [InlineData(1000, 300, false, 700)]
    [InlineData(1000, 1000, false, 0)]
    [InlineData(1000, 1500, false, 0)]  // never negative
    [InlineData(1000, 0, true, 0)]      // voided has no balance
    public void Balance_is_never_negative(decimal amount, decimal allocated, bool voided, decimal expected) =>
        Assert.Equal(expected, RentCharge.Balance(amount, allocated, voided));

    [Fact]
    public void Due_date_uses_the_due_day_of_the_period_and_description_is_spanish()
    {
        Assert.Equal(new DateOnly(2027, 2, 28), RentCharge.DueDateFor(2027, 2, 28));
        Assert.Equal("Renta febrero 2027", RentCharge.RentDescription(2027, 2));
        Assert.Equal(new DateOnly(2028, 2, 29), RentCharge.PeriodRange(2028, 2).Last);
    }

    [Theory]
    [InlineData("", 100, null, null, false)]
    [InlineData("Luz", 0, null, null, false)]
    [InlineData("Luz", -1, null, null, false)]
    [InlineData("Luz", 10.005, null, null, false)]
    [InlineData("Luz", 100, 2026, null, false)]   // period must be complete
    [InlineData("Luz", 100, 2026, 13, false)]
    [InlineData("Luz", 100, 2026, 10, true)]
    [InlineData("Luz", 100, null, null, true)]
    public void Manual_charge_validation(string description, double amount, int? year, int? month, bool valid) =>
        Assert.Equal(valid, RentCharge.ValidateManual(description, (decimal)amount, year, month) is null);

    [Fact]
    public void Void_requires_a_reason_and_works_once()
    {
        var c = new RentCharge { Amount = 100 };
        Assert.Throws<DomainException>(() => c.Void("  ", DateTimeOffset.UtcNow));
        c.Void("Error de captura", DateTimeOffset.UtcNow);
        Assert.Equal(ChargeStatus.Voided, c.Status);
        Assert.Equal("Error de captura", c.VoidReason);
        Assert.Throws<DomainException>(() => c.Void("otra vez", DateTimeOffset.UtcNow));
    }
}
