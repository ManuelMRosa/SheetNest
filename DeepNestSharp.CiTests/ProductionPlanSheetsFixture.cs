namespace DeepNestSharp.CiTests
{
  using System.Collections.Generic;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The condensed production plan ("cut 24 of A, then 1 of B") builds its remainder sheet by taking some
  /// of a full sheet's parts. Those two sheets are DIFFERENT layouts and the operator edits them one at a
  /// time, so they must not be wired to each other.
  /// </summary>
  public class ProductionPlanSheetsFixture
  {
    /// <summary>
    /// The remainder sheet used to be handed the very same placement objects as the sheet it was taken
    /// from. A drag writes X and Y in place, so moving a part on the remainder moved it on the full sheet
    /// too, and nothing said so: only the sheet on screen is repainted, so the damage showed up later, on
    /// another layout, or in the exported DXF.
    /// </summary>
    [Fact]
    public void ARemainderSheetDoesNotShareItsPartsWithTheSheetItCameFrom()
    {
      var master = FullSheet();
      var taken = DxfViewer.SelectPlacements(master, new Dictionary<int, int> { { 0, 1 } });

      taken.Should().HaveCount(1);
      taken[0].Should().NotBeSameAs(master.PartPlacements[0]);

      taken[0].X = 50; // the operator drags it across the remainder sheet

      master.PartPlacements[0].X.Should().Be(0, "a part moved on one layout must not move on another");
    }

    /// <summary>And the copy is the same part in the same place, or the remainder sheet would be a
    /// different nest from the one the engine worked out.</summary>
    [Fact]
    public void TheCopyIsTheSamePartInTheSamePlace()
    {
      var master = FullSheet();
      var taken = DxfViewer.SelectPlacements(master, new Dictionary<int, int> { { 0, 2 } });

      taken.Should().HaveCount(2);
      for (int i = 0; i < taken.Count; i++)
      {
        var from = master.PartPlacements[i];
        taken[i].X.Should().Be(from.X);
        taken[i].Y.Should().Be(from.Y);
        taken[i].Rotation.Should().Be(from.Rotation);
        taken[i].Source.Should().Be(from.Source);
        taken[i].Id.Should().Be(from.Id);
        taken[i].Part.Should().BeSameAs(from.Part, "the polygon is read-only and shared on purpose");
      }
    }

    /// <summary>Only as many as the plan asked for, in sheet order.</summary>
    [Fact]
    public void ItTakesOnlyWhatThePlanAskedFor()
    {
      var master = FullSheet();

      DxfViewer.SelectPlacements(master, new Dictionary<int, int> { { 0, 1 } }).Should().HaveCount(1);
      DxfViewer.SelectPlacements(master, new Dictionary<int, int> { { 0, 3 } }).Should().HaveCount(3);
      DxfViewer.SelectPlacements(master, new Dictionary<int, int> { { 0, 99 } }).Should().HaveCount(3, "there are only three on the sheet");
      DxfViewer.SelectPlacements(master, new Dictionary<int, int> { { 7, 1 } }).Should().BeEmpty("no part on this sheet comes from source 7");
    }

    private static ISheetPlacement FullSheet()
    {
      var placements = new List<IPartPlacement>
      {
        Square(0, 0, 1),
        Square(20, 0, 2),
        Square(40, 0, 3),
      };

      return new SheetPlacement(PlacementTypeEnum.Gravity, Sheet.NewSheet(1, 100, 50), placements, 0);
    }

    private static IPartPlacement Square(double x, double y, int id)
    {
      var poly = new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(0, 10),
      });

      return new PartPlacement(poly) { X = x, Y = y, Source = 0, Id = id };
    }
  }
}
