using Habita.Domain.Charges;
using Habita.Domain.Deposits;
using Habita.Domain.Leases;
using Habita.Domain.Settings;
using Xunit;

namespace Habita.UnitTests;

public class GraceDaysRulesTests
{
    static readonly DateOnly Due = new(2026, 10, 5);

    [Theory]
    [InlineData(0, "2026-10-05", false)]   // the due day itself is never overdue
    [InlineData(0, "2026-10-06", true)]    // no grace: overdue the day after, as before Hito 15
    [InlineData(3, "2026-10-06", false)]
    [InlineData(3, "2026-10-08", false)]   // the last day of grace
    [InlineData(3, "2026-10-09", true)]    // first overdue day
    [InlineData(30, "2026-11-04", false)]
    [InlineData(30, "2026-11-05", true)]
    public void A_charge_is_overdue_only_after_the_grace_days(int grace, string today, bool overdue) =>
        Assert.Equal(overdue ? ChargeStatus.Overdue : ChargeStatus.Open, RentCharge.Effective(ChargeStatus.Open, Due, DateOnly.Parse(today), grace));

    [Theory]
    [InlineData(ChargeStatus.Paid)]
    [InlineData(ChargeStatus.Voided)]
    public void Settled_charges_are_never_overdue_whatever_the_date(ChargeStatus stored) =>
        Assert.Equal(stored, RentCharge.Effective(stored, Due, Due.AddDays(400), 0));

    [Fact]
    public void A_part_paid_charge_follows_the_same_rule() =>
        Assert.Equal(ChargeStatus.PartiallyPaid, RentCharge.Effective(ChargeStatus.PartiallyPaid, Due, Due.AddDays(3), 3));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)] [InlineData(10)] [InlineData(30)]
    public void The_query_cutoff_and_the_status_rule_always_agree(int grace)
    {
        // Queries filter on "due before the cutoff"; the status uses Effective. Both must give the same answer for every day.
        var today = new DateOnly(2026, 10, 20);
        for (var offset = -45; offset <= 5; offset++)
        {
            var due = today.AddDays(offset);
            var byQuery = due < RentCharge.OverdueBefore(today, grace);
            var byStatus = RentCharge.Effective(ChargeStatus.Open, due, today, grace) == ChargeStatus.Overdue;
            Assert.Equal(byQuery, byStatus);
        }
    }
}

public class GuarantorAndInventoryRulesTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData("Pedro Fiador", null, true)]
    [InlineData("Pedro Fiador", "55 8765 4321", true)]
    [InlineData(null, "55 8765 4321", false)]          // a phone with no name is anonymous
    [InlineData("Pedro", "123", false)]
    [InlineData("Pedro", "teléfono", false)]
    public void A_guarantor_is_a_name_and_optionally_a_valid_phone(string? name, string? phone, bool valid) =>
        Assert.Equal(valid, Lease.ValidateGuarantor(name, phone) is null);

    [Fact]
    public void Guarantor_fields_have_length_limits()
    {
        Assert.NotNull(Lease.ValidateGuarantor(new string('x', 201), null));
        Assert.NotNull(Lease.ValidateGuarantor("Pedro", new string('5', 31)));
    }

    [Theory]
    [InlineData("Estufa", 1, ItemCondition.Good, true)]
    [InlineData("Cama", 999, ItemCondition.Poor, true)]
    [InlineData("", 1, ItemCondition.Good, false)]
    [InlineData("  ", 1, ItemCondition.Good, false)]
    [InlineData("Cama", 0, ItemCondition.Good, false)]
    [InlineData("Cama", 1000, ItemCondition.Good, false)]
    [InlineData("Cama", 1, (ItemCondition)9, false)]
    public void Inventory_items_need_a_name_a_sensible_quantity_and_a_known_condition(string name, int quantity, ItemCondition condition, bool valid) =>
        Assert.Equal(valid, InventoryItem.Validate(name, quantity, condition, null) is null);

    [Fact]
    public void Names_and_notes_have_length_limits()
    {
        Assert.NotNull(InventoryItem.Validate(new string('x', 101), 1, ItemCondition.Good, null));
        Assert.NotNull(InventoryItem.Validate("Cama", 1, ItemCondition.Good, new string('n', 301)));
        Assert.Equal("Bueno", InventoryItem.Label(ItemCondition.Good)); Assert.Equal("Regular", InventoryItem.Label(ItemCondition.Fair)); Assert.Equal("Malo", InventoryItem.Label(ItemCondition.Poor));
    }
}

public class DepositRulesTests
{
    static readonly DateOnly Today = new(2026, 10, 17);

    [Theory]
    [InlineData(100, "2026-10-17", true)]
    [InlineData(0, "2026-10-17", false)]
    [InlineData(-5, "2026-10-17", false)]
    [InlineData(10.005, "2026-10-17", false)]
    [InlineData(100, "1999-12-31", false)]
    [InlineData(100, "2101-01-01", false)]
    public void An_installment_needs_a_positive_amount_with_cents_and_a_sensible_date(double amount, string due, bool valid) =>
        Assert.Equal(valid, DepositRules.ValidateInstallment((decimal)amount, DateOnly.Parse(due)) is null);

    static string? Settle(decimal held, params (string? Concept, decimal Amount)[] deductions) =>
        DepositRules.ValidateSettlement(held, deductions, Today, Today, null);

    [Fact]
    public void A_settlement_needs_money_held_and_deductions_that_fit_inside_it()
    {
        Assert.Null(Settle(8500m));                                                  // all of it goes back
        Assert.Null(Settle(8500m, ("Pintura de la sala", 1200m), ("Llave perdida", 300m)));
        Assert.Null(Settle(8500m, ("Todo se queda por adeudo", 8500m)));             // nothing goes back, which is allowed
        Assert.NotNull(Settle(0m));                                                  // nothing was ever collected
        Assert.NotNull(Settle(8500m, ("Demasiado", 8500.01m)));                      // more than was held
        Assert.NotNull(Settle(1000m, ("A", 600m), ("B", 600m)));                     // the two together exceed it
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Every_deduction_carries_its_reason(string? concept) => Assert.NotNull(Settle(1000m, (concept, 100m)));

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(10.005)]
    public void A_deduction_is_a_positive_amount_with_at_most_cents(double amount) => Assert.NotNull(Settle(1000m, ("Motivo", (decimal)amount)));

    [Fact]
    public void Dates_and_lengths_are_checked()
    {
        Assert.NotNull(DepositRules.ValidateSettlement(100m, [], Today.AddDays(1), Today, null));   // not in the future
        Assert.NotNull(DepositRules.ValidateSettlement(100m, [], new DateOnly(1999, 1, 1), Today, null));
        Assert.NotNull(DepositRules.ValidateSettlement(100m, [], Today, Today, new string('x', 1001)));
        Assert.NotNull(Settle(100m, ("x".PadRight(201, 'x'), 1m)));
        Assert.NotNull(Settle(10000m, Enumerable.Range(0, DepositRules.MaxDeductions + 1).Select(i => ((string?)$"Motivo {i}", 1m)).ToArray()));
    }

    [Fact]
    public void A_settlement_can_be_voided_once_and_only_with_a_reason()
    {
        var s = new DepositSettlement();
        Assert.Throws<Habita.Domain.Common.DomainException>(() => s.Void("  ", Guid.NewGuid(), DateTimeOffset.UtcNow));
        s.Void("Error de captura", Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.False(s.IsActive);
        Assert.Equal("deposit.already_voided", Assert.Throws<Habita.Domain.Common.DomainException>(() => s.Void("otra vez", Guid.NewGuid(), DateTimeOffset.UtcNow)).Code);
    }

    [Fact]
    public void Grace_days_have_a_maximum() => Assert.Equal(30, OrganizationSettings.MaxGraceDays);
}
