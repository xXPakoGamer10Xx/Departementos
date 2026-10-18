using Habita.Domain.Collections;
using Habita.Domain.Common;
using Habita.Domain.Organizations;
using Habita.Domain.Payments;
using Xunit;

namespace Habita.UnitTests;

public class ClabeRulesTests
{
    [Theory]
    [InlineData("012180001123456781")]
    [InlineData("072600001234567898")]
    [InlineData("014180009876543213")]
    [InlineData("999000000000000014")]
    public void Valid_clabes_pass_the_check_digit(string clabe) => Assert.True(ClabeRules.IsValid(clabe));

    [Theory]
    [InlineData("012180001123456782")]   // wrong check digit
    [InlineData("012180001123456780")]
    [InlineData("01218000112345678")]    // 17 digits
    [InlineData("0121800011234567810")]  // 19 digits
    [InlineData("01218000112345678A")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_clabes_are_rejected(string? clabe) => Assert.False(ClabeRules.IsValid(clabe));

    [Fact]
    public void Every_single_digit_typo_is_caught()
    {
        // The check digit exists to catch a mistyped digit: change each of the first 17 digits to every other value and none may validate.
        const string good = "012180001123456781";
        for (var i = 0; i < 17; i++)
            for (var d = '0'; d <= '9'; d++)
            {
                if (d == good[i]) continue;
                var typo = good.Remove(i, 1).Insert(i, d.ToString());
                Assert.False(ClabeRules.IsValid(typo), $"{typo} should not validate");
            }
    }

    [Fact]
    public void Spaces_and_dashes_are_stripped_and_the_bank_is_detected()
    {
        Assert.Equal("012180001123456781", ClabeRules.Normalize(" 012 180-0011 2345 6781 "));
        Assert.Null(ClabeRules.Normalize("   "));
        Assert.Equal("BBVA México", ClabeRules.BankName("012180001123456781"));
        Assert.Equal("Banorte", ClabeRules.BankName("072600001234567898"));
        Assert.Null(ClabeRules.BankName("999000000000000014")); // valid number, prefix not in the catalogue
        Assert.Equal("012 180 00112345678 1", ClabeRules.Format("012180001123456781"));
    }
}

public class PaymentPromiseRulesTests
{
    static readonly DateOnly Today = new(2026, 10, 10);

    [Theory]
    [InlineData("2026-10-10", true)]   // today is allowed
    [InlineData("2026-12-01", true)]
    [InlineData("2027-01-08", true)]   // exactly 90 days
    [InlineData("2027-01-09", false)]
    [InlineData("2026-10-09", false)]
    public void The_promised_date_must_be_today_or_within_90_days(string date, bool valid) =>
        Assert.Equal(valid, PaymentPromise.Validate(DateOnly.Parse(date), Today, null) is null);

    [Fact]
    public void The_note_has_a_length_limit() => Assert.NotNull(PaymentPromise.Validate(Today, Today, new string('x', 501)));

    [Theory]
    [InlineData("2026-10-15", "2026-10-10", 1000, 1000, false, false, PromiseStatus.Kept)]     // paid early
    [InlineData("2026-10-15", "2026-10-15", 1000, 1000, false, false, PromiseStatus.Kept)]     // paid on the day
    [InlineData("2026-10-15", "2026-10-12", 1000, 400, false, false, PromiseStatus.Pending)]   // partial, day not reached
    [InlineData("2026-10-15", "2026-10-15", 1000, 400, false, false, PromiseStatus.Pending)]   // the day itself is still open
    [InlineData("2026-10-15", "2026-10-16", 1000, 400, false, false, PromiseStatus.Broken)]    // day passed, still short
    [InlineData("2026-10-15", "2026-10-16", 1000, 0, false, false, PromiseStatus.Broken)]
    [InlineData("2026-10-15", "2026-10-12", 1000, 0, true, false, PromiseStatus.Cancelled)]    // charge voided
    [InlineData("2026-10-15", "2026-10-12", 1000, 0, false, true, PromiseStatus.Cancelled)]    // promise cancelled
    [InlineData("2026-10-15", "2026-10-20", 1000, 1000, false, true, PromiseStatus.Cancelled)] // cancelled wins over everything
    public void Status_comes_from_the_money_received_by_the_promised_day(string promised, string today, double amount, double paid, bool voided, bool cancelled, PromiseStatus expected) =>
        Assert.Equal(expected, PaymentPromise.StatusFor(DateOnly.Parse(promised), DateOnly.Parse(today), (decimal)amount, (decimal)paid, voided, cancelled));

    [Fact]
    public void A_promise_cannot_be_cancelled_twice()
    {
        var p = new PaymentPromise();
        p.Cancel("Ya no aplica", DateTimeOffset.UtcNow);
        Assert.Equal("promise.already_cancelled", Assert.Throws<DomainException>(() => p.Cancel("otra vez", DateTimeOffset.UtcNow)).Code);
    }
}

public class CashTokenRulesTests
{
    [Fact]
    public void Generated_codes_use_the_unambiguous_alphabet()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = CashToken.Generate();
            Assert.Equal(10, code.Length);
            Assert.All(code, c => Assert.Contains(c, AccessCode.Alphabet));
        }
    }

    [Theory]
    [InlineData("ABCD-EFGH-JK", "ABCDEFGHJK")]
    [InlineData("abcd efgh jk", "ABCDEFGHJK")]
    [InlineData("habita:cash:ABCD-EFGH-JK", "ABCDEFGHJK")]
    [InlineData("  HABITA:CASH:abcdefghjk  ", "ABCDEFGHJK")]
    public void People_and_scanners_write_the_same_code_differently(string input, string expected) => Assert.Equal(expected, CashToken.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCD-EFGH")]
    [InlineData("0000000000")]   // 0 and O are not in the alphabet
    [InlineData("https://example.com/pay")]
    [InlineData("habita:cash:")]
    public void Anything_else_is_not_a_code(string? input) => Assert.Null(CashToken.Normalize(input));

    [Fact]
    public void The_hash_is_not_the_access_code_hash_for_the_same_text()
    {
        // The same characters as an access code must not open a cash token (and vice versa).
        Assert.NotEqual(AccessCode.Hash("ABCDEFGHJK"), CashToken.Hash("ABCDEFGHJK"));
        Assert.Equal(CashToken.Hash("ABCDEFGHJK"), CashToken.Hash("ABCDEFGHJK"));
    }

    [Fact]
    public void A_token_is_usable_only_when_new_unrevoked_and_unexpired()
    {
        var now = DateTimeOffset.UtcNow;
        var t = new CashToken { ExpiresAt = now.AddHours(1) };
        Assert.True(t.IsUsable(now));
        Assert.False(t.IsUsable(now.AddHours(2)));
        Assert.False(new CashToken { ExpiresAt = now.AddHours(1), RevokedAt = now }.IsUsable(now));
        Assert.False(new CashToken { ExpiresAt = now.AddHours(1), UsedAt = now }.IsUsable(now));
    }
}

public class PaymentClaimRulesTests
{
    static readonly DateOnly Today = new(2026, 10, 10);

    [Theory]
    [InlineData(1000, "2026-10-10", PaymentMethod.BankTransfer, true)]
    [InlineData(1000, "2026-10-10", PaymentMethod.Cash, true)]
    [InlineData(1000, "2026-10-10", PaymentMethod.Card, false)]     // not something a tenant reports
    [InlineData(0, "2026-10-10", PaymentMethod.BankTransfer, false)]
    [InlineData(10.005, "2026-10-10", PaymentMethod.BankTransfer, false)]
    [InlineData(1000, "2026-10-11", PaymentMethod.BankTransfer, false)] // future
    [InlineData(1000, "2025-10-09", PaymentMethod.BankTransfer, false)] // more than a year ago
    [InlineData(1000, "2025-10-10", PaymentMethod.BankTransfer, true)]
    public void Reporting_rules(double amount, string paidOn, PaymentMethod method, bool valid) =>
        Assert.Equal(valid, PaymentClaim.Validate((decimal)amount, DateOnly.Parse(paidOn), Today, method, null, null) is null);

    [Fact]
    public void Note_and_reference_have_limits()
    {
        Assert.NotNull(PaymentClaim.Validate(1, Today, Today, PaymentMethod.Cash, null, new string('x', 501)));
        Assert.NotNull(PaymentClaim.Validate(1, Today, Today, PaymentMethod.Cash, new string('x', 101), null));
    }

    [Fact]
    public void A_pending_claim_moves_once_and_then_is_closed()
    {
        var now = DateTimeOffset.UtcNow; var user = Guid.NewGuid();
        var confirmed = new PaymentClaim();
        confirmed.Confirm(Guid.NewGuid(), user, now);
        Assert.Equal(ClaimStatus.Confirmed, confirmed.Status);
        Assert.NotNull(confirmed.PaymentId);
        Assert.Equal("claim.not_pending", Assert.Throws<DomainException>(() => confirmed.Confirm(Guid.NewGuid(), user, now)).Code);
        Assert.Equal("claim.not_pending", Assert.Throws<DomainException>(() => confirmed.Reject("no", user, now)).Code);
        Assert.Equal("claim.not_pending", Assert.Throws<DomainException>(() => confirmed.Cancel(now)).Code);

        var rejected = new PaymentClaim();
        rejected.Reject("  No coincide el monto  ", user, now);
        Assert.Equal("No coincide el monto", rejected.ReviewComment);
        Assert.Null(rejected.PaymentId);
        Assert.Throws<DomainException>(() => rejected.Confirm(Guid.NewGuid(), user, now));

        var cancelled = new PaymentClaim();
        cancelled.Cancel(now);
        Assert.Equal(ClaimStatus.Cancelled, cancelled.Status);
        Assert.Throws<DomainException>(() => cancelled.Reject("x", user, now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejecting_needs_a_comment_for_the_tenant(string? comment)
    {
        Assert.NotNull(PaymentClaim.ValidateRejection(comment));
        var claim = new PaymentClaim();
        Assert.Equal("claim.comment_required", Assert.Throws<DomainException>(() => claim.Reject(comment!, Guid.NewGuid(), DateTimeOffset.UtcNow)).Code);
        Assert.Equal(ClaimStatus.Pending, claim.Status); // a failed rejection leaves it pending
        Assert.NotNull(PaymentClaim.ValidateRejection(new string('x', 501)));
    }
}
