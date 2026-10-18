using Habita.Domain.Common;
using Habita.Domain.Payments;
using Xunit;

namespace Habita.UnitTests;

public class PaymentRulesTests
{
    static readonly DateOnly Today = new(2026, 10, 10);

    [Theory]
    [InlineData(1000, "2026-10-10", true)]
    [InlineData(0, "2026-10-10", false)]
    [InlineData(-5, "2026-10-10", false)]
    [InlineData(10.005, "2026-10-10", false)]
    [InlineData(1000, "2026-10-11", false)]   // cannot be received in the future
    [InlineData(1000, "1999-12-31", false)]
    [InlineData(1000, "2026-01-01", true)]
    public void New_payment_validation(double amount, string receivedOn, bool valid) =>
        Assert.Equal(valid, Payment.ValidateNew((decimal)amount, DateOnly.Parse(receivedOn), Today, null, null) is null);

    [Fact]
    public void Reference_and_notes_have_length_limits()
    {
        Assert.NotNull(Payment.ValidateNew(1, Today, Today, new string('x', 101), null));
        Assert.NotNull(Payment.ValidateNew(1, Today, Today, null, new string('x', 2001)));
        Assert.Null(Payment.ValidateNew(1, Today, Today, new string('x', 100), new string('x', 2000)));
    }

    [Theory]
    [InlineData(1000, 700, 300)]   // acceptance criterion 4: $1,000 with $700 applied leaves $300 unassigned
    [InlineData(1000, 1000, 0)]
    [InlineData(1000, 1200, 0)]    // never negative
    [InlineData(1000, 0, 1000)]
    public void Unassigned_balance(decimal amount, decimal allocated, decimal expected) =>
        Assert.Equal(expected, Payment.Unassigned(amount, allocated));

    [Fact]
    public void Reverse_needs_a_reason_and_works_once()
    {
        var p = new Payment { Amount = 100 };
        var user = Guid.NewGuid();
        Assert.Throws<DomainException>(() => p.Reverse(" ", user, DateTimeOffset.UtcNow));
        p.Reverse("Cheque rebotado", user, DateTimeOffset.UtcNow);
        Assert.Equal(PaymentStatus.Reversed, p.Status);
        Assert.Equal("Cheque rebotado", p.ReversalReason);
        Assert.Equal(user, p.ReversedByUserId);
        Assert.Throws<DomainException>(() => p.Reverse("otra vez", user, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Allocation_amount_validation_and_single_reverse()
    {
        Assert.NotNull(PaymentAllocation.ValidateAmount(0));
        Assert.NotNull(PaymentAllocation.ValidateAmount(-1));
        Assert.NotNull(PaymentAllocation.ValidateAmount(1.234m));
        Assert.Null(PaymentAllocation.ValidateAmount(700.50m));

        var a = new PaymentAllocation { Amount = 10 };
        Assert.True(a.IsActive);
        Assert.Throws<DomainException>(() => a.Reverse("", DateTimeOffset.UtcNow));
        a.Reverse("Mal aplicado", DateTimeOffset.UtcNow);
        Assert.False(a.IsActive);
        Assert.Throws<DomainException>(() => a.Reverse("de nuevo", DateTimeOffset.UtcNow));
    }
}
