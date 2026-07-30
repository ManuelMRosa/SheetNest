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

    /// <summary>
    /// The case treating a hole as solid broke, and the reason picking takes two passes. A small part
    /// parked in a big part's window is a legal placement, and it is the one under the cursor. Asking the
    /// outline alone let the ring answer first whenever it happened to be drawn later, and the part in its
    /// window could never be selected, moved or rotated by mouse again.
    /// </summary>
    [Fact]
    public void APartInsideAnothersHoleIsTheOnePicked()
    {
      var small = Square(8, 8, 4);
      var ring = Ring(0, 0);
      var drawnRingLast = new List<IPartPlacement> { small, ring };

      DxfViewer.PickAt(drawnRingLast, 10, 10).Should().BeSameAs(small);
    }

    /// <summary>And the other order, so it is not passing by accident of the list.</summary>
    [Fact]
    public void TheOrderTheyWereDrawnInDoesNotDecideIt()
    {
      var small = Square(8, 8, 4);
      var ring = Ring(0, 0);
      var drawnSmallLast = new List<IPartPlacement> { ring, small };

      DxfViewer.PickAt(drawnSmallLast, 10, 10).Should().BeSameAs(small);
    }

    /// <summary>What the outline pass is still there for: an empty cutout picks up the part it belongs
    /// to, rather than falling through to nothing.</summary>
    [Fact]
    public void AnEmptyHoleStillPicksItsOwner()
    {
      var ring = Ring(0, 0);

      DxfViewer.PickAt(new List<IPartPlacement> { ring }, 10, 10).Should().BeSameAs(ring);
    }

    /// <summary>Topmost still wins between two parts that genuinely overlap.</summary>
    [Fact]
    public void TheTopmostOverlappingPartIsPicked()
    {
      var under = Square(0, 0, 10);
      var over = Square(5, 5, 10);

      DxfViewer.PickAt(new List<IPartPlacement> { under, over }, 8, 8).Should().BeSameAs(over);
    }

    [Fact]
    public void ClickingEmptySheetPicksNothing()
    {
      DxfViewer.PickAt(new List<IPartPlacement> { Ring(0, 0) }, 50, 50).Should().BeNull();
    }

    /// <summary>The hole is not material, even though it is on the part for picking purposes.</summary>
    [Fact]
    public void TheHoleIsNotMaterial()
    {
      var ring = Ring(0, 0);

      DxfViewer.MaterialContains(ring, 10, 10).Should().BeFalse("that is the cutout");
      DxfViewer.MaterialContains(ring, 2, 2).Should().BeTrue();
    }

    /// <summary>A plain square of the given side at (x, y).</summary>
    private static IPartPlacement Square(double x, double y, double side)
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
