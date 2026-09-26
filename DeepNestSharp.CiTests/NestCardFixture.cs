namespace DeepNestSharp.CiTests
{
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.Ui;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The Nests panel shows one card per distinct layout, in the order the viewer holds them, because
  /// picking a card is what puts that layout in the viewer: card n has to be layout n.
  /// </summary>
  public class NestCardFixture
  {
    [Fact]
    public void OneCardPerLayoutInTheViewersOrder()
    {
      var cards = NestCard.From(Plan(), unitsMm: false);

      cards.Select(c => c.Index).Should().Equal(0, 1);
      cards.Select(c => c.Name).Should().Equal("Nest 1", "Nest 2");
    }

    [Fact]
    public void SheetsIsHowManyTimesTheLayoutIsCut()
    {
      NestCard.From(Plan(), unitsMm: false).Select(c => c.Sheets).Should().Equal(3, 1);
    }

    /// <summary>
    /// One decimal, like the status bar. The second layout is picked so the value does not come out even:
    /// 100 / 7200 is 1.3888..., which has to print as 1.4%.
    /// </summary>
    [Fact]
    public void UtilizationIsAPercentageWithOneDecimal()
    {
      NestCard.From(Plan(), unitsMm: false).Select(c => c.Utilization).Should().Equal("1.0%", "1.4%");
    }

    [Theory]
    [InlineData(false, "200 × 100 in")]
    [InlineData(true, "200 × 100 mm")]
    public void SizeCarriesTheJobsUnits(bool unitsMm, string expected)
    {
      NestCard.From(Plan(), unitsMm)[0].Size.Should().Be(expected);
    }

    [Fact]
    public void NoResultMeansNoCards()
    {
      NestCard.From(null, unitsMm: false).Should().BeEmpty();
    }

    /// <summary>Two 10 x 10 parts on a 200 x 100 sheet cut three times, then one on a 120 x 60 cut once.</summary>
    private static IReadOnlyList<(ISheetPlacement Sheet, int Count, string Name)> Plan()
    {
      var first = new List<IPartPlacement>
      {
        new PartPlacement(Square("a.dxf")) { X = 0, Y = 0, Source = 0 },
        new PartPlacement(Square("a.dxf")) { X = 20, Y = 0, Source = 0 },
      };
      var second = new List<IPartPlacement>
      {
        new PartPlacement(Square("a.dxf")) { X = 0, Y = 0, Source = 0 },
      };

      return new List<(ISheetPlacement Sheet, int Count, string Name)>
      {
        (new SheetPlacement(PlacementTypeEnum.Gravity, Sheet.NewSheet(1, 200, 100), first, 0, false), 3, "Layout 1"),
        (new SheetPlacement(PlacementTypeEnum.Gravity, Sheet.NewSheet(2, 120, 60), second, 0, false), 1, "Layout 2"),
      };
    }

    private static INfp Square(string name)
      => new NoFitPolygon(new List<SvgPoint> { new SvgPoint(0, 0), new SvgPoint(10, 0), new SvgPoint(10, 10), new SvgPoint(0, 10) }) { Name = name };
  }
}
