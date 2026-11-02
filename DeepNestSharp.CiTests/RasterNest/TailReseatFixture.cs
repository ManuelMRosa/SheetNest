namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// On the sheet that takes everything still to be placed, a part sitting a hair inside its clearance used
  /// to be given back, and the only place left for it was a sheet of its own: five welding tables came out on
  /// four sheets, the fourth holding one strip, while the third used under a third of its length. The part is
  /// now first tried past the end of the pack on its own sheet, and when there is no room there, past the end
  /// of an earlier sheet before it is given a sheet of its own.
  /// </summary>
  public class TailReseatFixture
  {
    private const double SheetW = 100;
    private const double SheetH = 50;
    private const double Margin = 1;

    private static readonly Func<IPartPlacement, IPartPlacement, double> Clear = (a, b) => 0.25;

    [Fact]
    public void APartThatCannotStayWhereItIsGoesPastTheEndOfThePack()
    {
      var parts = Row(4);
      var victim = Square(5, 5); // on top of the first square, deeper than any nudge
      parts.Add(victim);
      PlacementAcceptance.CountUnfit(parts, SheetW, SheetH, Margin, Clear).Should().BeGreaterThan(0);

      bool moved = SparrowNestService.TryReseatAtTail(parts, victim, Clear, new List<SparrowNestService.CommonEdgeIntent>(), SheetW, SheetH, Margin, 0.01, 0.0001);

      moved.Should().BeTrue("the sheet has plenty of room past its last part");
      PlacementAcceptance.CountUnfit(parts, SheetW, SheetH, Margin, Clear).Should().Be(0, "where it went is clear by the rule the sheet is judged by");
      victim.X.Should().BeApproximately(parts.Where(p => !ReferenceEquals(p, victim)).Max(p => p.X + 10) + 0.25, 0.01,
        "it goes one clearance past the farthest part, so the sheet grows by no more than it has to");
      victim.Y.Should().Be(Margin, "the first place tried across the sheet is at the margin");
    }

    [Fact]
    public void WithNoRoomPastThePackItStaysPutAndIsGivenBack()
    {
      var parts = Row(9); // the last square ends one margin short of the sheet's far edge
      var victim = Square(5, 5);
      parts.Add(victim);

      bool moved = SparrowNestService.TryReseatAtTail(parts, victim, Clear, new List<SparrowNestService.CommonEdgeIntent>(), SheetW, SheetH, Margin, 0.01, 0.0001);

      moved.Should().BeFalse();
      victim.X.Should().Be(5, "a part with nowhere to go is left exactly where it was");
      victim.Y.Should().Be(5);
    }

    [Fact]
    public void ALastSheetWhosePartsFitPastTheEndOfAnEarlierOneIsDropped()
    {
      var first = Row(4);
      var lone = Square(Margin, Margin);
      var sheets = new List<List<IPartPlacement>> { first, new List<IPartPlacement> { lone } };
      var edges = new List<List<SparrowNestService.CommonEdgeIntent>> { new(), new() };
      var sizes = new List<(int W, int H)> { ((int)SheetW, (int)SheetH), ((int)SheetW, (int)SheetH) };
      double end = first.Max(p => p.X + 10);

      bool dropped = SparrowNestService.FoldLastSheetBack(sheets, edges, sizes, Clear, Margin, 0.01, 0.0001);

      dropped.Should().BeTrue("the first sheet has room past its last part");
      sheets.Should().HaveCount(1);
      edges.Should().HaveCount(1);
      sizes.Should().HaveCount(1);
      sheets[0].Should().Contain(lone);
      lone.X.Should().BeApproximately(end + 0.25, 0.01, "it goes one clearance past the farthest part");
      PlacementAcceptance.CountUnfit(sheets[0], SheetW, SheetH, Margin, Clear).Should().Be(0);
    }

    [Fact]
    public void WhenOnePartOfTheLastSheetFitsNowhereNothingMoves()
    {
      var first = Row(8); // room past the pack for one more square, not two
      var a = Square(Margin, Margin);
      var b = Square(Margin + 10.9, Margin);
      var sheets = new List<List<IPartPlacement>> { first, new List<IPartPlacement> { a, b } };
      var edges = new List<List<SparrowNestService.CommonEdgeIntent>> { new(), new() };
      var sizes = new List<(int W, int H)> { ((int)SheetW, (int)SheetH), ((int)SheetW, (int)SheetH) };

      bool dropped = SparrowNestService.FoldLastSheetBack(sheets, edges, sizes, Clear, Margin, 0.01, 0.0001);

      dropped.Should().BeFalse();
      sheets.Should().HaveCount(2);
      sheets[0].Should().HaveCount(8, "the square that did fit is taken back off the first sheet");
      sheets[1].Should().Equal(a, b);
      (a.X, a.Y, b.X, b.Y).Should().Be((Margin, Margin, Margin + 10.9, Margin), "both stay exactly where they were");
    }

    /// <summary>Squares of 10 along the bottom from the margin, each a clearance and a bit from the next.</summary>
    private static List<IPartPlacement> Row(int count)
      => Enumerable.Range(0, count).Select(i => Square(Margin + (i * 10.9), Margin)).ToList();

    private static IPartPlacement Square(double x, double y, double side = 10)
    {
      var poly = new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(0, 0),
        new SvgPoint(side, 0),
        new SvgPoint(side, side),
        new SvgPoint(0, side),
      });

      return new PartPlacement(poly) { X = x, Y = y };
    }
  }
}
