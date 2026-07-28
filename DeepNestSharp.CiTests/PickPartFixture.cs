namespace DeepNestSharp.CiTests
{
  using System.Collections.Generic;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Which part the operator meant when they clicked. Picking used to ask the DRAWN geometry, which is
  /// even-odd so that a hole is a gap in it: pointing at a part in the middle of a big cutout picked up
  /// whatever was behind it, or deselected. A hole is still part of the part when you go to grab it.
  /// </summary>
  public class PickPartFixture
  {
    /// <summary>The one that was wrong: a ring, clicked through the middle.</summary>
    [Fact]
    public void ClickingInsideAHoleStillPicksThePart()
    {
      var ring = Ring(0, 0);

      DxfViewer.OutlineContains(ring, 10, 10).Should().BeTrue("the hole is in the middle of the part, not outside it");
    }

    [Fact]
    public void ClickingOnTheSolidPartPicksIt()
    {
      var ring = Ring(0, 0);

      DxfViewer.OutlineContains(ring, 2, 2).Should().BeTrue();
    }

    [Fact]
    public void ClickingOffThePartPicksNothing()
    {
      var ring = Ring(0, 0);

      DxfViewer.OutlineContains(ring, 25, 10).Should().BeFalse();
      DxfViewer.OutlineContains(ring, -1, 10).Should().BeFalse();
    }

    /// <summary>The part is judged where it SITS, not where its polygon was drawn.</summary>
    [Fact]
    public void ThePartIsPickedWhereItSits()
    {
      var moved = Ring(100, 50);

      DxfViewer.OutlineContains(moved, 110, 60).Should().BeTrue();
      DxfViewer.OutlineContains(moved, 10, 10).Should().BeFalse();
    }

    /// <summary>A concave outline is not a bounding box: the notch is off the part.</summary>
    [Fact]
    public void TheNotchOfAConcavePartIsNotOnIt()
    {
      var poly = new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(0, 0),
        new SvgPoint(20, 0),
        new SvgPoint(20, 20),
        new SvgPoint(12, 20),
        new SvgPoint(12, 6),
        new SvgPoint(8, 6),
        new SvgPoint(8, 20),
        new SvgPoint(0, 20),
      });
      var pp = new PartPlacement(poly) { X = 0, Y = 0 };

      DxfViewer.OutlineContains(pp, 10, 15).Should().BeFalse("that is the slot between the two legs");
      DxfViewer.OutlineContains(pp, 10, 3).Should().BeTrue("below the slot the part is solid");
    }

    [Fact]
    public void NothingIsPickedWhenThereIsNoPart()
    {
      DxfViewer.OutlineContains(null, 0, 0).Should().BeFalse();
    }

    /// <summary>A 20x20 square with a 10x10 hole in the middle, placed at (x, y).</summary>
    private static IPartPlacement Ring(double x, double y)
    {
      var outer = new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(0, 0),
        new SvgPoint(20, 0),
        new SvgPoint(20, 20),
        new SvgPoint(0, 20),
      });

      outer.Children.Add(new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(5, 5),
        new SvgPoint(15, 5),
        new SvgPoint(15, 15),
        new SvgPoint(5, 15),
      }));

      return new PartPlacement(outer) { X = x, Y = y };
    }
  }
}
