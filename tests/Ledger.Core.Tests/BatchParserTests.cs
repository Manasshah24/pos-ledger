using Ledger.Core.Events;

namespace Ledger.Core.Tests;

public class BatchParserTests
{
    private const string GoodEvent =
        """{"eventId":"e1","storeId":"S1","terminalId":"T1","type":"sale","occurredAt":"2026-01-05T10:00:00Z","saleId":"sale-1","amountMinor":500}""";

    [Fact]
    public void Reads_a_well_formed_batch()
    {
        var result = BatchParser.Parse($$"""{"events":[{{GoodEvent}}]}""");

        var item = Assert.Single(result.Events!);
        Assert.Equal("e1", item.Event!.EventId);
        Assert.Equal(500, item.Event.AmountMinor);
    }

    [Theory]
    [InlineData("not json", BatchParser.InvalidJson)]
    [InlineData("{}", BatchParser.MissingEventsArray)]
    public void Bodies_that_are_not_a_batch_are_an_error(string body, string error)
    {
        Assert.Equal(error, BatchParser.Parse(body).Error);
    }

    [Fact]
    public void A_bad_element_stays_in_place_so_the_rest_of_the_batch_survives()
    {
        var result = BatchParser.Parse($$"""{"events":[{{GoodEvent}}, 42, {"eventId":"e2","amountMinor":12.5}, {"eventId":"e3","amountMinor":"500"}, {{GoodEvent}}]}""");

        Assert.NotNull(result.Events![0].Event);
        Assert.Null(result.Events[1].Event); // not an object
        Assert.Null(result.Events[2].Event); // fractional money is malformed, not rounded
        Assert.Null(result.Events[3].Event); // money as a string is malformed, not coerced
        Assert.NotNull(result.Events[4].Event);
    }

    [Fact]
    public void Batch_over_the_cap_is_refused()
    {
        var body = "{\"events\":[" + string.Join(",", Enumerable.Repeat("{}", BatchParser.MaxBatchSize + 1)) + "]}";

        Assert.Equal(BatchParser.BatchTooLarge, BatchParser.Parse(body).Error);
    }
}
