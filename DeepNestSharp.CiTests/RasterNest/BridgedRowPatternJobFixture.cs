namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// Nineteen copies of an invented 9 x 36 in bar with R1 corners, common line, on one 120 x 60 in sheet at two
  /// seconds a sheet, the way the operator nests. The row pattern used to lay them as a standing band with lying
  /// rows at the spacing above it, 19 shared cuts; only a search round that finished in time joined the rows to
  /// the band, 31. At two seconds that happened some of the time with two angles and never with four, and once
  /// it left the block 2.14 in off the left border. The bridged pattern gives 31 every time, from the corner, and
  /// the one standing part closes the layout after the lying columns. The same with a narrower bar, whose lying
  /// grid slides along onto the standing band.
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public sealed class BridgedRowPatternJobFixture : IDisposable
  {
    private const double Spacing = 0.25D;
    private const double Margin = 0.25D;
    private readonly ITestOutputHelper output;
    private readonly string directory;
    private readonly string barPath;
    private readonly string narrowBarPath;

    public BridgedRowPatternJobFixture(ITestOutputHelper output)
    {
      this.output = output;
      this.directory = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.directory);
      this.barPath = Path.Combine(this.directory, "wide-bar.dxf");
      RowPatternPackerFixture.CreateRoundedRectangle(this.barPath, 9D, 36D);
      this.narrowBarPath = Path.Combine(this.directory, "narrow-bar.dxf");
      RowPatternPackerFixture.CreateRoundedRectangle(this.narrowBarPath, 7D, 36D);
    }

    public void Dispose()
    {
      try
      {
        Directory.Delete(this.directory, true);
      }
      catch (IOException)
      {
        // Test cleanup must not hide the actual assertion result.
      }
    }

    [SparrowTheory]
    [InlineData(4)]
    [InlineData(RotationCodes.RotZeroAnd90)]
    public void NineteenWiderBarsShareThirtyOneCutsFromTheCorner(int rotations)
    {
      var placed = this.NestOneSheet(this.barPath, 19, rotations, 31);

      var standing = placed.Single(p => p.PlacedPart.MaxX - p.PlacedPart.MinX < 10);
      standing.PlacedPart.MinX.Should().BeGreaterThan(placed.Where(p => p != standing).Max(p => p.PlacedPart.MaxX), "the standing part closes the layout");
      standing.PlacedPart.MinY.Should().BeApproximately(Margin, 1e-4);
    }

    /// <summary>Twenty-six of an invented 7 x 36 in bar with R1 corners, test2's outline: 17 standing in a band and
    /// a grid of 3 x 3 lying above it. The lying ends did not rest on the standing parts, so the grid kept the
    /// spacing from the band, 28 shared cuts in two blocks; slid along onto the band, every part shares in one
    /// block, 46.</summary>
    [SparrowTheory]
    [InlineData(4)]
    [InlineData(RotationCodes.RotZeroAnd90)]
    public void TwentySixNarrowBarsShareInOneBlock(int rotations)
    {
      this.NestOneSheet(this.narrowBarPath, 26, rotations, 46);
    }

    /// <summary>Nests the copies at two seconds a sheet and checks they come out on one sheet from the corner with
    /// the given number of shared cuts, every part sharing.</summary>
    private List<IPartPlacement> NestOneSheet(string path, int quantity, int rotations, int sharedCuts)
    {
      var parts = new[]
      {
        new RasterPartInfo
        {
          Path = path,
          Quantity = quantity,
          Rotations = rotations,
          Spacing = Spacing,
          Cc = CommonCuttingMode.Unrestricted,
          CommonCuttingDomain = "direct",
          RequiresMergedLines = true,
        },
      };

      INestResult result = SparrowNestService.NestForTime(
        parts,
        new[] { (120, 60, 1, false) },
        rotations,
        Spacing,
        Margin,
        5,
        SparrowExe.Resolve(),
        NestRounds.NoLimit,
        out string error,
        out NestRoundsReport report,
        commonLineGap: 0.010D,
        geometryTolerance: 0.0001D,
        secondsPerSheet: 2);

      result.Should().NotBeNull(error);
      var sheets = result.UsedSheets.ToList();
      sheets.Should().ContainSingle();
      var sheet = sheets[0];
      var placed = sheet.PartPlacements.ToList();
      var welded = new HashSet<int>((sheet.CommonEdges ?? Array.Empty<CommonEdge>()).SelectMany(e => new[] { e.PartAId, e.PartBId }));
      this.output.WriteLine(FormattableString.Invariant(
        $"BRIDGED x{quantity} rotations={rotations}: best={report.BestRound} edges={sheet.CommonEdges?.Count} minX={placed.Min(p => p.PlacedPart.MinX):F4} minY={placed.Min(p => p.PlacedPart.MinY):F4}"));
      placed.Should().HaveCount(quantity);
      sheet.CommonEdges.Should().HaveCount(sharedCuts);
      placed.Should().OnlyContain(p => welded.Contains(p.Id), "every part shares a cut");
      CommonLineGeometryResolver.Resolve(sheet).Clusters.Should().ContainSingle("every part is in one block");
      placed.Min(p => p.PlacedPart.MinX).Should().BeApproximately(Margin, 1e-4, "the block starts at the border");
      placed.Min(p => p.PlacedPart.MinY).Should().BeApproximately(Margin, 1e-4);
      return placed;
    }
  }
}
