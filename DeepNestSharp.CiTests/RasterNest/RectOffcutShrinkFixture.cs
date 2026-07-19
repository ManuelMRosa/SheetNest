namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// The "Prefer rectangular offcut" shrink pass: with the flag ON the last sheet's parts re-pack
  /// onto a virtually shortened sheet, so the used strip never grows and every part stays inside it
  /// — the leftover beyond the strip is one clean rectangle. With the flag OFF the pipeline must be
  /// untouched.
  /// </summary>
  public class RectOffcutShrinkFixture : IDisposable
  {
    private const int SheetW = 100;
    private const int SheetH = 50;
    private const double PxPerInch = 24.0;

    private readonly string dxfDir;
    private readonly ITestOutputHelper output;

    public RectOffcutShrinkFixture(ITestOutputHelper output)
    {
      this.output = output;
      this.dxfDir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.dxfDir);
    }

    public void Dispose()
    {
      try
      {
        Directory.Delete(this.dxfDir, true);
      }
      catch (IOException)
      {
        // Leftover temp files must never fail the suite.
      }
    }

    private static OffcutOptions Options(OffcutDirection direction) => new OffcutOptions { Direction = direction, Spacing = 0.2 };

    [Fact]
    public void ShrinkNeverWorsensTheNestAndKeepsEveryPartInsideTheStrip()
    {
      var parts = this.MixedJob();

      var off = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out string errOff);
      var on = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out string errOn, rectOffcut: Options(OffcutDirection.End));

      errOff.Should().BeNull();
      errOn.Should().BeNull();
      off.Should().NotBeNull();
      on.Should().NotBeNull();

      on.UnplacedParts.Count().Should().Be(off.UnplacedParts.Count()).And.Be(0);
      on.UsedSheets.Count.Should().Be(off.UsedSheets.Count);
      TotalPlaced(on).Should().Be(TotalPlaced(off));

      // The strip must never grow, and no part may reach past it into the offcut rectangle.
      double extentOn = LastSheetExtentX(on);
      LastSheetExtentX(off).Should().BeGreaterThanOrEqualTo(extentOn - 1e-6);
      foreach (var placement in on.UsedSheets[on.UsedSheets.Count - 1].PartPlacements)
      {
        placement.MaxX.Should().BeLessThanOrEqualTo(extentOn + 1e-6);
        placement.MinX.Should().BeGreaterThanOrEqualTo(-1e-6);
        placement.MinY.Should().BeGreaterThanOrEqualTo(-1e-6);
        placement.MaxY.Should().BeLessThanOrEqualTo(SheetH + 1e-6);
      }
    }

    [Fact]
    public void SideDirectionShrinksTheShortAxis()
    {
      var parts = this.MixedJob();

      var off = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _);
      var on = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: Options(OffcutDirection.Side));

      on.Should().NotBeNull();
      TotalPlaced(on).Should().Be(TotalPlaced(off));
      on.UsedSheets.Count.Should().Be(off.UsedSheets.Count);

      double extentYOn = LastSheetExtentY(on);
      LastSheetExtentY(off).Should().BeGreaterThanOrEqualTo(extentYOn - 1e-6);
      foreach (var placement in on.UsedSheets[on.UsedSheets.Count - 1].PartPlacements)
      {
        placement.MaxY.Should().BeLessThanOrEqualTo(extentYOn + 1e-6);
        placement.MaxX.Should().BeLessThanOrEqualTo(SheetW + 1e-6);
      }
    }

    [Fact]
    public void BothDirectionSqueezesIntoACorner()
    {
      var parts = this.MixedJob();

      var off = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _);
      var on = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: Options(OffcutDirection.Both));

      on.Should().NotBeNull();
      TotalPlaced(on).Should().Be(TotalPlaced(off));
      on.UsedSheets.Count.Should().Be(off.UsedSheets.Count);

      // Neither axis may grow, and every part stays inside the packed corner rectangle.
      double extentXOn = LastSheetExtentX(on);
      double extentYOn = LastSheetExtentY(on);
      LastSheetExtentX(off).Should().BeGreaterThanOrEqualTo(extentXOn - 1e-6);
      LastSheetExtentY(off).Should().BeGreaterThanOrEqualTo(extentYOn - 1e-6);
      foreach (var placement in on.UsedSheets[on.UsedSheets.Count - 1].PartPlacements)
      {
        placement.MaxX.Should().BeLessThanOrEqualTo(extentXOn + 1e-6);
        placement.MaxY.Should().BeLessThanOrEqualTo(extentYOn + 1e-6);
      }
    }

    [Fact]
    public void FullSheetFallsBackToTheNormalNest()
    {
      // 8 × (24 × 24) squares gross-fill the 100 × 50 sheet — no shorter strip can exist, so the
      // shrink must quietly keep the normal result.
      var parts = new List<RasterPartInfo>
      {
        new RasterPartInfo { Path = this.WriteRectangle("full-square", 24, 24), Quantity = 8 },
      };

      var off = RasterNestService.Nest(parts, SheetW, SheetH, 1, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _);
      var on = RasterNestService.Nest(parts, SheetW, SheetH, 1, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: Options(OffcutDirection.End));

      on.Should().NotBeNull();
      TotalPlaced(on).Should().Be(TotalPlaced(off));
      on.UsedSheets.Count.Should().Be(off.UsedSheets.Count);
      LastSheetExtentX(off).Should().BeGreaterThanOrEqualTo(LastSheetExtentX(on) - 1e-6);
    }

    [Fact]
    public void FlagOffMatchesTheDefaultOverload()
    {
      var parts = this.MixedJob();

      var implicitOff = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _);
      var explicitOff = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: null);

      Signature(explicitOff).Should().Be(Signature(implicitOff));
    }

    [Fact]
    public void AutoPicksTheLargestQualifyingRemnant()
    {
      // Auto is the argmax over exactly the three manual plans, so its qualifying remnant area can
      // never fall below any of them — measured the same way the overlay/export will.
      var parts = this.MixedJob();

      var off = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _);
      var end = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: Options(OffcutDirection.End));
      var side = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: Options(OffcutDirection.Side));
      var both = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: Options(OffcutDirection.Both));
      var auto = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out string errAuto, rectOffcut: Options(OffcutDirection.Auto));

      errAuto.Should().BeNull();
      auto.Should().NotBeNull();
      auto.UnplacedParts.Count().Should().Be(0);
      TotalPlaced(auto).Should().Be(TotalPlaced(off));
      auto.UsedSheets.Count.Should().Be(off.UsedSheets.Count);

      double autoArea = RemnantAreaOf(auto);
      autoArea.Should().BeGreaterThanOrEqualTo(RemnantAreaOf(end) - 1e-6);
      autoArea.Should().BeGreaterThanOrEqualTo(RemnantAreaOf(side) - 1e-6);
      autoArea.Should().BeGreaterThanOrEqualTo(RemnantAreaOf(both) - 1e-6);

      foreach (var placement in auto.UsedSheets[auto.UsedSheets.Count - 1].PartPlacements)
      {
        placement.MinX.Should().BeGreaterThanOrEqualTo(-1e-6);
        placement.MinY.Should().BeGreaterThanOrEqualTo(-1e-6);
        placement.MaxX.Should().BeLessThanOrEqualTo(SheetW + 1e-6);
        placement.MaxY.Should().BeLessThanOrEqualTo(SheetH + 1e-6);
      }
    }

    [Fact]
    public void ShrinkIsDeterministic()
    {
      // The shrink (multi-start included) must give bit-identical results run to run.
      var parts = this.MixedJob();

      var a = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: Options(OffcutDirection.End));
      var b = RasterNestService.Nest(parts, SheetW, SheetH, 2, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: Options(OffcutDirection.End));

      Signature(b).Should().Be(Signature(a));
      this.output.WriteLine(FormattableString.Invariant(
        $"PROBE MixedJob End: extentX={LastSheetExtentX(a):F3} remnantArea={RemnantAreaOf(a):F2}"));
    }

    [Fact]
    public void BigAndSmallPartsKeepEveryInvariant()
    {
      // One dominant part plus a crowd of small ones — the kind of tail where the placement ORDER
      // changes the pack, so different starts can reach shorter strips. Only invariants are
      // asserted; the actual gain is measured from the probe output.
      var parts = new List<RasterPartInfo>
      {
        new RasterPartInfo { Path = this.WriteRectangle("big", 30, 46), Quantity = 1 },
        new RasterPartInfo { Path = this.WriteRectangle("small", 12, 11), Quantity = 6 },
      };

      var off = RasterNestService.Nest(parts, SheetW, SheetH, 1, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _);
      var end = RasterNestService.Nest(parts, SheetW, SheetH, 1, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: Options(OffcutDirection.End));
      var auto = RasterNestService.Nest(parts, SheetW, SheetH, 1, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: Options(OffcutDirection.Auto));

      end.Should().NotBeNull();
      auto.Should().NotBeNull();
      end.UnplacedParts.Count().Should().Be(0);
      auto.UnplacedParts.Count().Should().Be(0);
      TotalPlaced(end).Should().Be(TotalPlaced(off));
      TotalPlaced(auto).Should().Be(TotalPlaced(off));

      double extentXEnd = LastSheetExtentX(end);
      LastSheetExtentX(off).Should().BeGreaterThanOrEqualTo(extentXEnd - 1e-6);
      foreach (var placement in end.UsedSheets[end.UsedSheets.Count - 1].PartPlacements)
      {
        placement.MinX.Should().BeGreaterThanOrEqualTo(-1e-6);
        placement.MinY.Should().BeGreaterThanOrEqualTo(-1e-6);
        placement.MaxX.Should().BeLessThanOrEqualTo(SheetW + 1e-6);
        placement.MaxY.Should().BeLessThanOrEqualTo(SheetH + 1e-6);
      }

      this.output.WriteLine(FormattableString.Invariant(
        $"PROBE BigSmall: off extentX={LastSheetExtentX(off):F3} | End extentX={extentXEnd:F3} remnant={RemnantAreaOf(end):F2} | Auto remnant={RemnantAreaOf(auto):F2}"));
    }

    [Theory]
    [InlineData(0)] // End
    [InlineData(1)] // Side
    [InlineData(2)] // Both
    [InlineData(3)] // Auto
    public void OffcutNeverDegradesTheNest(int direction)
    {
      // The core promise: the nest always comes first and the offcut is an ADDITION — with the
      // flag ON nothing about the base nest may degrade, and only the LAST sheet may be re-packed.
      var parts = this.BigJob();

      var off = RasterNestService.Nest(parts, SheetW, SheetH, 3, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _);
      var on = RasterNestService.Nest(parts, SheetW, SheetH, 3, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: Options((OffcutDirection)direction));

      off.Should().NotBeNull();
      on.Should().NotBeNull();
      off.UsedSheets.Count.Should().BeGreaterThan(1); // multi-sheet, or the earlier-sheets guarantee is vacuous

      on.UnplacedParts.Count().Should().Be(off.UnplacedParts.Count());
      TotalPlaced(on).Should().Be(TotalPlaced(off));
      on.UsedSheets.Count.Should().Be(off.UsedSheets.Count);

      // Every sheet before the last is bit-identical to the offcut-less nest.
      for (int s = 0; s < on.UsedSheets.Count - 1; s++)
      {
        SheetSignature(on.UsedSheets[s]).Should().Be(SheetSignature(off.UsedSheets[s]), $"sheet {s} must be untouched by the offcut");
      }

      // And the re-packed last sheet keeps every part inside the sheet.
      foreach (var placement in on.UsedSheets[on.UsedSheets.Count - 1].PartPlacements)
      {
        placement.MinX.Should().BeGreaterThanOrEqualTo(-1e-6);
        placement.MinY.Should().BeGreaterThanOrEqualTo(-1e-6);
        placement.MaxX.Should().BeLessThanOrEqualTo(SheetW + 1e-6);
        placement.MaxY.Should().BeLessThanOrEqualTo(SheetH + 1e-6);
      }
    }

    [Fact]
    public void RemnantRectsAreTheSingleSourceForLinesAndArea()
    {
      // 100x50 sheet (growX), pack reaching x=80 / y=45, 0.2 gap → cutX=80.2, cutY=45.2.
      // End: one full-height end strip. Its cut edge is the strip's inner (left) vertical edge.
      var end = OffcutGeometry.CutPositionsCore(80, 45, 100, 50, 0.2, OffcutDirection.End);
      var endRects = OffcutGeometry.RemnantRects(end.CutX, end.CutY, 100, 50);
      endRects.Count.Should().Be(1);
      var e = endRects[0];
      e.X.Should().BeApproximately(80.2, 1e-9);
      e.Y.Should().BeApproximately(0, 1e-9);
      e.W.Should().BeApproximately(19.8, 1e-9);
      e.H.Should().BeApproximately(50, 1e-9);
      e.Cut.X1.Should().BeApproximately(80.2, 1e-9);
      e.Cut.Y1.Should().BeApproximately(0, 1e-9);
      e.Cut.X2.Should().BeApproximately(80.2, 1e-9);
      e.Cut.Y2.Should().BeApproximately(50, 1e-9);
      OffcutGeometry.RemnantArea(end.CutX, end.CutY, 100, 50).Should().BeApproximately(19.8 * 50, 1e-7);

      // Both: L-shape — the long-axis (X) strip is full height; the short-axis (Y) strip stops at
      // the X cut (guillotine). Areas sum to sheet − packed rectangle.
      var both = OffcutGeometry.CutPositionsCore(80, 45, 100, 50, 0.2, OffcutDirection.Both);
      var bothRects = OffcutGeometry.RemnantRects(both.CutX, both.CutY, 100, 50);
      bothRects.Count.Should().Be(2);
      bothRects[0].Cut.X1.Should().BeApproximately(80.2, 1e-9); // vertical...
      bothRects[0].Cut.Y2.Should().BeApproximately(50, 1e-9);   // ...full height
      bothRects[1].Cut.Y1.Should().BeApproximately(45.2, 1e-9); // horizontal...
      bothRects[1].Cut.X2.Should().BeApproximately(80.2, 1e-9); // ...stops at the X cut
      double summed = bothRects[0].W * bothRects[0].H + bothRects[1].W * bothRects[1].H;
      summed.Should().BeApproximately(OffcutGeometry.RemnantArea(both.CutX, both.CutY, 100, 50), 1e-9);
      summed.Should().BeApproximately((100.0 * 50) - (80.2 * 45.2), 1e-9);

      // No qualifying strip → no rects, zero area.
      var none = OffcutGeometry.RemnantRects(null, null, 100, 50);
      none.Count.Should().Be(0);
      OffcutGeometry.RemnantArea(null, null, 100, 50).Should().Be(0);
    }

    [Fact]
    public void LargeTailStillProducesValidOffcut()
    {
      // Above the multi-start tail cap (16): the shrink falls back to the default ordering only, but
      // must still never degrade the nest and keep every part inside the sheet.
      var parts = new List<RasterPartInfo>
      {
        new RasterPartInfo { Path = this.WriteRectangle("many", 8, 8), Quantity = 20 },
      };

      var off = RasterNestService.Nest(parts, SheetW, SheetH, 1, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _);
      var on = RasterNestService.Nest(parts, SheetW, SheetH, 1, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: Options(OffcutDirection.End));

      on.Should().NotBeNull();
      on.UnplacedParts.Count().Should().Be(off.UnplacedParts.Count()).And.Be(0);
      TotalPlaced(on).Should().Be(TotalPlaced(off));
      on.UsedSheets.Count.Should().Be(off.UsedSheets.Count);
      LastSheetExtentX(off).Should().BeGreaterThanOrEqualTo(LastSheetExtentX(on) - 1e-6);
      foreach (var placement in on.UsedSheets[on.UsedSheets.Count - 1].PartPlacements)
      {
        placement.MinX.Should().BeGreaterThanOrEqualTo(-1e-6);
        placement.MaxX.Should().BeLessThanOrEqualTo(SheetW + 1e-6);
        placement.MaxY.Should().BeLessThanOrEqualTo(SheetH + 1e-6);
      }
    }

    [Fact]
    public void MinimumRemnantWidthGatesTheCut()
    {
      // 100×50 sheet, pack reaching x=80 → a 20-wide end strip; y=45 → a 5-tall side strip.

      // Automatic rule (5% of the side): both strips qualify on their axes.
      var auto = OffcutGeometry.CutPositionsCore(80, 45, 100, 50, 0.2, OffcutDirection.End);
      auto.CutX.Should().NotBeNull();

      // Explicit minimum above the strip width: the sheet is left uncut.
      var below = OffcutGeometry.CutPositionsCore(80, 45, 100, 50, 0.2, OffcutDirection.End, 25);
      below.CutX.Should().BeNull();

      // Explicit minimum below the strip width: cut at extent + spacing.
      var above = OffcutGeometry.CutPositionsCore(80, 45, 100, 50, 0.2, OffcutDirection.End, 10);
      above.CutX.Should().BeApproximately(80.2, 1e-9);

      // The minimum applies per axis (Auto counts both): 20 ≥ 8 qualifies, 5 < 8 does not.
      var both = OffcutGeometry.CutPositionsCore(80, 45, 100, 50, 0.2, OffcutDirection.Auto, 8);
      both.CutX.Should().NotBeNull();
      both.CutY.Should().BeNull();

      // <=0 keeps the historical 5% rule: a 4-wide strip fails 5% of 100.
      var tiny = OffcutGeometry.CutPositionsCore(96, 45, 100, 50, 0.2, OffcutDirection.End, 0);
      tiny.CutX.Should().BeNull();

      // The minimum is measured on the ACTUAL remnant (free space minus the cut-line gap). A 10-wide
      // free space with a 3-wide gap leaves only 7 of usable remnant — below an 8 minimum, so no cut,
      // even though the pre-fix "free space >= minimum" test would have passed it.
      var eatenBySpacing = OffcutGeometry.CutPositionsCore(90, 45, 100, 50, 3, OffcutDirection.End, 8);
      eatenBySpacing.CutX.Should().BeNull();

      // Same free space, minimum 5: 7 usable >= 5 → cut at extent + gap.
      var qualifiesWithSpacing = OffcutGeometry.CutPositionsCore(90, 45, 100, 50, 3, OffcutDirection.End, 5);
      qualifiesWithSpacing.CutX.Should().BeApproximately(93, 1e-9);
    }

    [Fact]
    public void SquareSheetOffcutCutsTheShrunkAxis()
    {
      // On a SQUARE sheet the engine (growX) and the overlay/export geometry (OffcutGeometry) must
      // agree on which axis grows — otherwise the engine shrinks one axis while the cut is drawn on
      // the other, freeing no usable remnant. A slack job leaves a real end strip either way.
      var parts = new List<RasterPartInfo>
      {
        new RasterPartInfo { Path = this.WriteRectangle("sq", 14, 14), Quantity = 4 },
      };

      var off = RasterNestService.Nest(parts, 50, 50, 1, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _);
      var on = RasterNestService.Nest(parts, 50, 50, 1, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: new OffcutOptions { Direction = OffcutDirection.End, Spacing = 0.2 });

      on.Should().NotBeNull();
      on.UnplacedParts.Count().Should().Be(off.UnplacedParts.Count()).And.Be(0);
      TotalPlaced(on).Should().Be(TotalPlaced(off));
      on.UsedSheets.Count.Should().Be(off.UsedSheets.Count);

      // The engine actually shrank the axis the cut is drawn on, so a usable remnant is freed.
      var sheet = on.UsedSheets[on.UsedSheets.Count - 1];
      var (cutX, cutY) = OffcutGeometry.CutPositionsCore(
        sheet.PartPlacements.Max(p => p.MaxX), sheet.PartPlacements.Max(p => p.MaxY),
        50, 50, 0.2, OffcutDirection.End);
      (cutX ?? cutY).Should().NotBeNull("a square-sheet End offcut must free a real remnant");
    }

    [Fact]
    public void AutoOnAFullSheetKeepsTheNormalResult()
    {
      // No qualifying strip exists on a gross-filled sheet: all three of Auto's attempts come back
      // empty and the original greedy layout must survive untouched.
      var parts = new List<RasterPartInfo>
      {
        new RasterPartInfo { Path = this.WriteRectangle("full-square-auto", 24, 24), Quantity = 8 },
      };

      var off = RasterNestService.Nest(parts, SheetW, SheetH, 1, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _);
      var auto = RasterNestService.Nest(parts, SheetW, SheetH, 1, PlacementTypeEnum.BoundingBox, 4, 0.2, 0.5, PxPerInch, out _, rectOffcut: Options(OffcutDirection.Auto));

      auto.Should().NotBeNull();
      TotalPlaced(auto).Should().Be(TotalPlaced(off));
      auto.UsedSheets.Count.Should().Be(off.UsedSheets.Count);
    }

    private static int TotalPlaced(INestResult result) => result.UsedSheets.Sum(s => s.PartPlacements.Count);

    private static double LastSheetExtentX(INestResult result) =>
      result.UsedSheets[result.UsedSheets.Count - 1].PartPlacements.Max(p => p.MaxX);

    private static double LastSheetExtentY(INestResult result) =>
      result.UsedSheets[result.UsedSheets.Count - 1].PartPlacements.Max(p => p.MaxY);

    /// <summary>The last sheet's qualifying remnant area, measured with the same shared math the
    /// engine's Auto mode and the overlay/export use (both axes eligible, 5% strip filter).</summary>
    private static double RemnantAreaOf(INestResult result)
    {
      var (cutX, cutY) = OffcutGeometry.CutPositionsCore(
        LastSheetExtentX(result), LastSheetExtentY(result), SheetW, SheetH, 0.2, OffcutDirection.Auto);
      return OffcutGeometry.RemnantArea(cutX, cutY, SheetW, SheetH);
    }

    private static string Signature(INestResult result) =>
      string.Join("|", result.UsedSheets.SelectMany(s => s.PartPlacements)
        .Select(p => FormattableString.Invariant($"{p.Part.Name}:{p.X:F4}:{p.Y:F4}:{p.Rotation:F1}")));

    private static string SheetSignature(ISheetPlacement sheet) =>
      string.Join("|", sheet.PartPlacements
        .Select(p => FormattableString.Invariant($"{p.Part.Name}:{p.X:F4}:{p.Y:F4}:{p.Rotation:F1}")));

    /// <summary>Triangles pack with internal gaps under greedy bottom-left — the shrink's target case.</summary>
    private List<RasterPartInfo> MixedJob() => new List<RasterPartInfo>
    {
      new RasterPartInfo { Path = this.WriteTriangle("tri", 20, 12), Quantity = 6 },
      new RasterPartInfo { Path = this.WriteRectangle("rect", 15, 10), Quantity = 4 },
    };

    /// <summary>MixedJob scaled to spill onto a second sheet — exercises the multi-sheet guarantees.</summary>
    private List<RasterPartInfo> BigJob() => new List<RasterPartInfo>
    {
      new RasterPartInfo { Path = this.WriteTriangle("tri-big", 20, 12), Quantity = 24 },
      new RasterPartInfo { Path = this.WriteRectangle("rect-big", 15, 10), Quantity = 16 },
    };

    private string WriteRectangle(string name, double w, double h) => this.WriteDxf(name, new[]
    {
      new DxfPoint(0, 0, 0), new DxfPoint(w, 0, 0), new DxfPoint(w, h, 0), new DxfPoint(0, h, 0), new DxfPoint(0, 0, 0),
    });

    private string WriteTriangle(string name, double w, double h) => this.WriteDxf(name, new[]
    {
      new DxfPoint(0, 0, 0), new DxfPoint(w, 0, 0), new DxfPoint(0, h, 0), new DxfPoint(0, 0, 0),
    });

    private string WriteDxf(string name, IEnumerable<DxfPoint> points)
    {
      var file = new DxfFile();
      file.Entities.Add(new DxfPolyline(points.Select(p => new DxfVertex(p))) { IsClosed = true });
      string path = Path.Combine(this.dxfDir, name + ".dxf");
      file.Save(path);
      return path;
    }
  }
}
