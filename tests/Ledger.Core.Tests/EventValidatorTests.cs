using FsCheck;
using FsCheck.Fluent;
using Ledger.Core.Events;

namespace Ledger.Core.Tests;

public class EventValidatorTests
{
    private static IncomingEvent Good(string type = EventType.Sale) =>
        new("evt-1", "S001", "S001-T01", type, "2026-01-05T10:00:00Z", "sale-1", 1250);

    private static string? Reason(IncomingEvent e) =>
        EventValidator.TryValidate(e, out _, out var reason) ? null : reason;

    [Theory]
    [InlineData(EventType.Sale)]
    [InlineData(EventType.Return)]
    [InlineData(EventType.Void)]
    public void Valid_events_of_each_supported_type_pass(string type)
    {
        Assert.True(EventValidator.TryValidate(Good(type), out var valid, out _));
        Assert.Equal(1250, valid.AmountMinor);
    }

    [Fact]
    public void A_gift_card_redemption_needs_a_card_not_a_sale()
    {
        var redeem = new IncomingEvent("e1", "S1", "T1", EventType.GiftcardRedeem, "2026-01-05T10:00:00Z", null, 500, "card-1", 7);

        Assert.True(EventValidator.TryValidate(redeem, out var valid, out _));
        Assert.Equal(("card-1", 7L), (valid.CardId, valid.LeaseId));
        Assert.Equal(RejectReason.MissingCardId, Reason(redeem with { CardId = " " }));
        Assert.Equal(RejectReason.InvalidAmount, Reason(redeem with { AmountMinor = 0 }));
    }

    [Fact]
    public void A_loyalty_event_needs_an_account_and_a_positive_number_of_points()
    {
        var earn = new IncomingEvent("e1", "S1", "T1", EventType.LoyaltyEarn, "2026-01-05T10:00:00Z", null, null, AccountId: "a1", Points: 50);

        Assert.True(EventValidator.TryValidate(earn, out var valid, out _));
        Assert.Equal(("a1", 50L), (valid.AccountId, valid.Points));
        Assert.Equal(RejectReason.MissingAccountId, Reason(earn with { AccountId = null }));
        Assert.Equal(RejectReason.InvalidPoints, Reason(earn with { Points = 0 }));
        Assert.Equal(RejectReason.InvalidPoints, Reason(earn with { Points = null }));
    }

    [Fact]
    public void Timestamps_become_utc_microseconds()
    {
        EventValidator.TryValidate(Good() with { OccurredAt = "2026-01-05T15:30:00.1234567+05:30" }, out var valid, out _);

        Assert.Equal(new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_560), valid!.OccurredAtUtc);
    }

    public static TheoryData<IncomingEvent, string> Invalid => new()
    {
        { Good() with { EventId = "  " }, RejectReason.MissingEventId },
        { Good() with { StoreId = "" }, RejectReason.MissingStoreId },
        { Good() with { Type = "refund" }, RejectReason.UnsupportedType },
        { Good() with { OccurredAt = "2026-01-05T10:00:00" }, RejectReason.InvalidOccurredAt }, // no offset: ambiguous
        { Good() with { SaleId = null }, RejectReason.MissingSaleId },
        { Good() with { AmountMinor = 0 }, RejectReason.InvalidAmount },
        { Good() with { AmountMinor = -5 }, RejectReason.InvalidAmount },
        { Good() with { EventId = "a\0b" }, RejectReason.InvalidCharacters },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_events_are_refused_with_a_specific_reason(IncomingEvent e, string expected)
    {
        Assert.Equal(expected, Reason(e));
    }

    [Fact]
    public void Property_validation_never_throws_and_always_gives_a_known_answer()
    {
        var known = typeof(RejectReason).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
        var text = Gen.OneOf(Gen.Elements("", " ", "x", "sale", "return", "2026-01-05T10:00:00Z", "\0"), ArbMap.Default.GeneratorFor<string>());
        var number = Gen.OneOf(Gen.Constant<long?>(null), Gen.Choose(-5, 100_000).Select(i => (long?)i));
        var gen =
            from a in text from b in text from c in text from d in text from e in text from f in text from n in number
            select new IncomingEvent(a, b, c, d, e, f, n);

        Prop.ForAll(gen.ToArbitrary(), e =>
            EventValidator.TryValidate(e, out var valid, out var reason)
                ? valid is not null && valid.AmountMinor > 0
                : reason is not null && known.Contains(reason)).QuickCheckThrowOnFailure();
    }
}
