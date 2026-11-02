namespace DeepNestSharp.CiTests.RasterNest
{
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// How much search the opening of a timed nest gets: a share of work, in the engine's sample evaluations,
  /// split between the sheets the job is expected to take. Only the size of the job decides it, never the
  /// clock, so every machine gives the same opening the same work.
  /// </summary>
  public class OpeningWorkFixture
  {
    [Theory]
    [InlineData(100, 60, int.MaxValue, 2)]
    [InlineData(120, 60, int.MaxValue, 2)] // exactly two sheetfuls is two sheets
    [InlineData(121, 60, int.MaxValue, 3)]
    [InlineData(1, 60, int.MaxValue, 1)]
    [InlineData(0, 60, int.MaxValue, 1)] // a job always takes a sheet
    [InlineData(500, 60, 1, 1)] // never more sheets than are in stock
    [InlineData(500, 60, 3, 3)]
    [InlineData(500, 0, int.MaxValue, 1)] // no usable area to divide by
    public void ASheetIsExpectedPerSheetfulOfPartsRoundedUp(double partsArea, double usableArea, int inStock, int expected)
    {
      SparrowNestService.EstimatedSheets(partsArea, usableArea, inStock).Should().Be(expected);
    }

    [Fact]
    public void MoreSheetsShareTheOpeningsWorkButNeverDropBelowAFloor()
    {
      long one = SparrowNestService.OpeningEvals(1);
      long two = SparrowNestService.OpeningEvals(2);
      long four = SparrowNestService.OpeningEvals(4);
      long many = SparrowNestService.OpeningEvals(50);

      one.Should().BeGreaterThan(0);
      two.Should().BeLessThanOrEqualTo(one);
      four.Should().BeLessThan(one, "a job of four sheets has to split the opening between them to deliver in time");
      many.Should().BeLessThanOrEqualTo(four);
      many.Should().BeGreaterThan(0, "every sheet still gets a search");
      SparrowNestService.OpeningEvals(500).Should().Be(many, "past the floor more sheets cost no less each");
    }

    /// <summary>A time per sheet gives the job that much for every sheet it is expected to take, the same
    /// estimate the opening's work is split by.</summary>
    [Theory]
    [InlineData(2, 3, 6)]
    [InlineData(2, 1, 2)]
    [InlineData(10, 0, 10)] // never less than one sheet's time
    [InlineData(1.5, 4, 6)]
    public void ATimePerSheetGivesTheJobThatMuchForEachSheetExpected(double secondsPerSheet, int sheets, double expectedSeconds)
    {
      SparrowNestService.TimeLimit(secondsPerSheet, sheets).Should().Be(System.TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NoTimePerSheetIsNoLimit(double secondsPerSheet)
    {
      SparrowNestService.TimeLimit(secondsPerSheet, 3).Should().Be(NestRounds.NoLimit);
    }
  }
}
