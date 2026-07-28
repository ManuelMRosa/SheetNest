namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// What Export DXF asks for, and what it has to get back. Export calls BuildLines on the last sheet of
  /// every run, whatever the offcut settings say, so "there is no offcut" has to come back as a null list
  /// rather than as a throw.
  /// <para>Issue #4, reported against 1.1.7: with "Prefer rectangular offcut" left off, which is the
  /// default, CurrentOffcutOptions() hands over null and the export died on a NullReferenceException
  /// before writing a single file. CutPositions already guarded that case; BuildLines did not, and no
  /// test reached BuildLines at all, only the pure maths underneath it.</para>
  /// </summary>
  public class OffcutExportLinesFixture
  {
    private const int W = 120;
    private const int H = 60;

    /// <summary>The crash itself: the feature is off, so there are no lines to draw, and saying so must
    /// not cost the user their export.</summary>
    [Fact]
    public void WithTheFeatureOffTheExportGetsNoLinesInsteadOfACrash()
    {
      var act = () => OffcutGeometry.BuildLines(Packed(90), null);

      act.Should().NotThrow<NullReferenceException>("null options is how the UI says the feature is off");
      OffcutGeometry.BuildLines(Packed(90), null).Should().BeNull();
    }

    /// <summary>The other half of the same guard. Nothing placed on nothing frees no remnant.</summary>
    [Fact]
    public void NoSheetPlacementAtAllGivesNoLines()
    {
      var act = () => OffcutGeometry.BuildLines(null, new OffcutOptions { Direction = OffcutDirection.End });

      act.Should().NotThrow<NullReferenceException>();
      OffcutGeometry.BuildLines(null, new OffcutOptions { Direction = OffcutDirection.End }).Should().BeNull();
    }

    /// <summary>The control that keeps the guard honest: with the feature ON the same sheet still yields
    /// its cut line, at the end of the pack, running the full height of the sheet.</summary>
    [Fact]
    public void WithTheFeatureOnTheCutLineStillComesBack()
    {
      var lines = OffcutGeometry.BuildLines(Packed(90), new OffcutOptions { Direction = OffcutDirection.End });

      lines.Should().HaveCount(1);
      lines[0].X1.Should().Be(90);
      lines[0].X2.Should().Be(90);
      lines[0].Y1.Should().Be(0);
      lines[0].Y2.Should().Be(H);
    }

    /// <summary>A sheet with one part packed against the left edge, occupying <paramref name="partWidth"/>
    /// of the sheet's width and all of its height.</summary>
    private static ISheetPlacement Packed(int partWidth)
    {
      var part = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(partWidth, 0),
        new SvgPoint(partWidth, H),
        new SvgPoint(0, H),
      });

      var placements = new List<IPartPlacement> { new PartPlacement(part) { X = 0, Y = 0 } };
      return new SheetPlacement(PlacementTypeEnum.Gravity, Sheet.NewSheet(1, W, H), placements, 0);
    }
  }
}
