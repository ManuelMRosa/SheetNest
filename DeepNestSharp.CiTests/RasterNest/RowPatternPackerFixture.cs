namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.IO;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// The row pattern: copies of one rounded rectangle laid out in bands by arithmetic, every copy sharing a cut.
  /// The numbers are the operator's test2 job: a 7 x 36 in part with R1 corners on a 120 x 60 in sheet, 0.25 in
  /// spacing and border, 0.010 in common-line gap. The search fits 26 a sheet there with no part on its own, but
  /// takes about a minute for 800 copies; the pattern has to match it in a fraction of that.
  /// </summary>
  public sealed class RowPatternPackerFixture : IDisposable
  {
    private const double Gap = 0.010D;
    private const double Tolerance = 0.0001D;
    private const double Spacing = 0.25D;
    private const double Margin = 0.25D;
    private const double UsableW = 120D - (2 * Margin);
    private const double UsableH = 60D - (2 * Margin);
    private const double QuarterCircleBulge = -0.4142135623730951D;

    // Standing is the drawing as it comes, 7 wide and 36 tall: side by side the long sides meet (34 in straight
    // between the R1 corners), one above the other the short ones do (5 in).
    private static readonly RowPatternPacker.Pose Standing = new(0, 7D, 36D, 34D, 5D, 0, 1, 2, 3);
    private static readonly RowPatternPacker.Pose Lying = new(90, 36D, 7D, 5D, 34D, 0, 1, 2, 3);
    private static readonly RowPatternPacker.Pose[] Both = { Standing, Lying };

    // A wider bar, 9 x 36 with R1 corners: 34 in straight down its long sides, 7 across its ends.
    private static readonly RowPatternPacker.Pose Tall = new(0, 9D, 36D, 34D, 7D, 0, 1, 2, 3);
    private static readonly RowPatternPacker.Pose Flat = new(90, 36D, 9D, 7D, 34D, 0, 1, 2, 3);

    private readonly ITestOutputHelper output;
    private readonly string directory;
    private readonly string test2Path;
    private readonly string barPath;
    private readonly string wideBarPath;

    public RowPatternPackerFixture(ITestOutputHelper output)
    {
      this.output = output;
      this.directory = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.directory);
      this.test2Path = Path.Combine(this.directory, "test2-copy.dxf");
      CreateTest2Copy(this.test2Path);
      this.barPath = Path.Combine(this.directory, "slotted-bar.dxf");
      CreateSlottedBar(this.barPath);
      this.wideBarPath = Path.Combine(this.directory, "wide-bar.dxf");
      CreateRoundedRectangle(this.wideBarPath, 9D, 36D);
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

    /// <summary>17 standing across the sheet (119.16 in) and three bands of three lying above them.</summary>
    [Fact]
    public void TwentySixCopiesFillTheSheetAndEveryOneSharesACut()
    {
      var layout = RowPatternPacker.Plan(UsableW, UsableH, Both, Gap, Spacing, 43, 100, true, Tolerance)!;

      layout.Placed.Should().Be(26);
      layout.Slots.Count(s => s.PoseIndex == 0).Should().Be(17);
      layout.Slots.Count(s => s.PoseIndex == 1).Should().Be(9);
      layout.Unshared.Should().Be(0);
      layout.Seams.Should().HaveCount(22, "16 between the standing parts and 2 in each band of three");
      layout.SharedLength.Should().BeApproximately((16 * 34) + (6 * 5), 1e-6);
      layout.ExtentX.Should().BeApproximately((17 * 7) + (16 * Gap), 1e-6);
      layout.ExtentY.Should().BeApproximately(36 + (3 * 7) + (3 * Spacing), 1e-6);
    }

    /// <summary>Stacked, the three lying bands close up into a grid and also share their long sides: the same
    /// 26 parts with six more seams, which is what the search's own sheet of this job reaches.</summary>
    [Fact]
    public void StackedBandsAlsoShareTheirLongSides()
    {
      var layout = RowPatternPacker.Plan(UsableW, UsableH, Both, Gap, Spacing, 43, 100, true, Tolerance, stack: true)!;

      layout.Placed.Should().Be(26);
      layout.Unshared.Should().Be(0);
      layout.Seams.Should().HaveCount(28);
      layout.SharedLength.Should().BeApproximately((16 * 34) + (6 * 5) + (6 * 34), 1e-6);
      layout.ExtentY.Should().BeApproximately(36 + Spacing + (3 * 7) + (2 * Gap), 1e-6);
    }

    /// <summary>The last sheet of the job. Three columns of lying parts (7, 7 and 6, long sides together) would
    /// share the most, 578 in, but reach 108.5 in along the sheet. Fourteen standing and three bands of two lying
    /// share 457 and stop at 98.13, and the length the sheet keeps comes first.</summary>
    [Fact]
    public void TheLastTwentyStillAllShareInTheShortestSplit()
    {
      var plain = RowPatternPacker.Plan(UsableW, UsableH, Both, Gap, Spacing, 20, 100, true, Tolerance)!;
      var stacked = RowPatternPacker.Plan(UsableW, UsableH, Both, Gap, Spacing, 20, 100, true, Tolerance, stack: true)!;

      plain.Placed.Should().Be(20);
      plain.Unshared.Should().Be(0);
      plain.Slots.Count(s => s.PoseIndex == 0).Should().Be(14);
      plain.ExtentX.Should().BeApproximately((14 * 7) + (13 * Gap), 1e-6);
      plain.SharedLength.Should().BeApproximately((13 * 34) + (3 * 5), 1e-6);
      stacked.Placed.Should().Be(20);
      stacked.Unshared.Should().Be(0);
      stacked.SharedLength.Should().BeGreaterThan(plain.SharedLength);
    }

    /// <summary>17 standing would leave one lying part in a band of its own. Of the splits that share the lot,
    /// the shortest is 12 standing and three bands of two lying, 84.11 in along the sheet. Sixteen standing and
    /// a band of two would share more, 515 in, but reach 112.15.</summary>
    [Fact]
    public void ABandOfOneIsAvoidedWhenAnotherSplitShares()
    {
      var layout = RowPatternPacker.Plan(UsableW, UsableH, Both, Gap, Spacing, 18, 100, true, Tolerance)!;

      layout.Placed.Should().Be(18);
      layout.Unshared.Should().Be(0);
      layout.Slots.Count(s => s.PoseIndex == 0).Should().Be(12);
      layout.ExtentX.Should().BeApproximately((12 * 7) + (11 * Gap), 1e-6);
      layout.SharedLength.Should().BeApproximately((11 * 34) + (3 * 5), 1e-6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoClusterIsLargerThanTheLimit(bool stack)
    {
      var layout = RowPatternPacker.Plan(UsableW, UsableH, Both, Gap, Spacing, 43, 10, true, Tolerance, stack)!;

      Clusters(layout).Max().Should().BeLessOrEqualTo(10);
    }

    [Fact]
    public void ATallSheetGetsTheSameSheetTurned()
    {
      var layout = RowPatternPacker.Plan(UsableH, UsableW, Both, Gap, Spacing, 43, 100, false, Tolerance)!;

      layout.Placed.Should().Be(26);
      layout.Unshared.Should().Be(0);
    }

    [Fact]
    public void OneCopyIsOnItsOwn()
    {
      var layout = RowPatternPacker.Plan(UsableW, UsableH, Both, Gap, Spacing, 1, 100, true, Tolerance)!;

      layout.Placed.Should().Be(1);
      layout.Unshared.Should().Be(1);
    }

    /// <summary>
    /// Nineteen of the wider bar. Without the bridge, 13 stand in a band and two rows of three lie above them,
    /// 117.12 in along the sheet: 12 seams down the band, two in each row and three between the rows, 19 in all,
    /// and the rows rest on the band at the spacing with nothing shared. Bridged, the standing part a column of
    /// its own would leave on its own shares with the lying parts beside it: three stacked columns of six lying
    /// and one standing part, 3 x 36 + 9 in and three gaps, shorter. Down each column 5 seams of 34 in, across the
    /// columns 2 x 6 of 7 in, and the ends of the four lying parts beside the standing part against its long
    /// side: 7, 7, 7 and 6.97 in, where the fourth runs past its straight edge. 31 seams, every part in one block.
    /// The pose with the most parts starts at the corner either way: the band of 13 under the rows, the columns
    /// before the standing part, which closes the layout next to the rest of the sheet.
    /// </summary>
    [Fact]
    public void NineteenWiderBarsShareInOneBlockWhenBandsOfTwoPosesAreBridged()
    {
      var poses = new[] { Tall, Flat };

      var plain = RowPatternPacker.Plan(UsableW, UsableH, poses, Gap, Spacing, 19, 100, true, Tolerance, stack: true)!;
      var bridged = RowPatternPacker.Plan(UsableW, UsableH, poses, Gap, Spacing, 19, 100, true, Tolerance, stack: true, bridge: true)!;

      plain.Bridged.Should().Be(0);
      plain.Seams.Should().HaveCount(19);
      plain.SharedLength.Should().BeApproximately((12 * 34) + (4 * 7) + (3 * 34), 1e-6);
      plain.ExtentX.Should().BeApproximately((13 * 9) + (12 * Gap), 1e-6);
      plain.Slots.Where(s => s.PoseIndex == 0).Should().OnlyContain(s => s.MinY == 0, "the band of 13 starts at the corner");
      plain.Slots.Where(s => s.PoseIndex == 1).Should().OnlyContain(s => s.MinY >= 36 + Spacing - 1e-6);

      bridged.Placed.Should().Be(19);
      bridged.Unshared.Should().Be(0);
      bridged.Bridged.Should().Be(1);
      bridged.Slots.Count(s => s.PoseIndex == 0).Should().Be(1);
      bridged.Seams.Should().HaveCount(31);
      bridged.SharedLength.Should().BeApproximately((15 * 34) + (12 * 7) + (3 * 7) + 6.97, 1e-6);
      bridged.ExtentX.Should().BeApproximately(9 + (3 * 36) + (3 * Gap), 1e-6);
      bridged.ExtentY.Should().BeApproximately((6 * 9) + (5 * Gap), 1e-6);
      Clusters(bridged).Should().Equal(19);
      bridged.Slots.Where(s => s.PoseIndex == 1).Min(s => s.MinX).Should().Be(0, "the lying columns start at the corner");
      var standing = bridged.Slots.Single(s => s.PoseIndex == 0);
      standing.MinX.Should().BeApproximately((3 * 36) + (3 * Gap), 1e-6, "the standing part closes the layout");
      standing.MinY.Should().Be(0);

      var across = bridged.Seams.Where(s => bridged.Slots[s.A].PoseIndex != bridged.Slots[s.B].PoseIndex).ToList();
      across.Should().HaveCount(4).And.OnlyContain(s => s.AlongX && bridged.Slots[s.B].PoseIndex == 0, "the standing part stands to the right of the lying column");
      across.Select(s => s.Length).Should().Equal(new[] { 7D, 7D, 7D, 6.97D }, (a, b) => Math.Abs(a - b) < 1e-6);

      // The common-line record reads each edge's stretch from 0 at its first point to 1 at its last. The fourth
      // lying part sits 3 x 9.01 = 27.03 in up: its end's straight edge runs from 28.03 to 35.03, the standing
      // part's long one from 1 to 35.
      var fourth = across[3].Edges.Should().ContainSingle().Subject;
      fourth.EdgeA.Should().Be(1, "the lying part's max-X side");
      fourth.EdgeB.Should().Be(0, "the standing part's min-X side");
      fourth.AStart.Should().BeApproximately(0, 1e-6);
      fourth.AEnd.Should().BeApproximately(6.97 / 7, 1e-6);
      fourth.BStart.Should().BeApproximately(27.03 / 34, 1e-6);
      fourth.BEnd.Should().BeApproximately(1, 1e-6);
    }

    /// <summary>The same bar with square corners: its straight edges run the full sides. A standing part joined to
    /// the lying ones beside it would lose both edges at its corners, where three seams would meet, which the
    /// common-line topology refuses (measured on the slotted bar's square ends). The bands keep the spacing.
    /// Made-up poses have no outline, so nothing else can refuse the bridge here.</summary>
    [Fact]
    public void BandsOfPartsWithSquareCornersAreNotBridged()
    {
      var squareTall = new RowPatternPacker.Pose(0, 9D, 36D, 36D, 9D, 0, 1, 2, 3);
      var squareFlat = new RowPatternPacker.Pose(90, 36D, 9D, 9D, 36D, 0, 1, 2, 3);

      var layout = RowPatternPacker.Plan(UsableW, UsableH, new[] { squareTall, squareFlat }, Gap, Spacing, 19, 100, true, Tolerance, stack: true, bridge: true)!;

      layout.Bridged.Should().Be(0);
      layout.Seams.Should().HaveCount(19);
    }

    /// <summary>A block held to six parts: the standing part and a column of six lying beside it would make
    /// seven, so the bridge stays out.</summary>
    [Fact]
    public void ABridgeNeverMakesABlockLargerThanTheLimit()
    {
      var layout = RowPatternPacker.Plan(UsableW, UsableH, new[] { Tall, Flat }, Gap, Spacing, 19, 6, true, Tolerance, stack: true, bridge: true)!;

      Clusters(layout).Max().Should().BeLessOrEqualTo(6);
    }

    /// <summary>Test2's ends are narrower. Laid from the corner on the band of 17 standing, the second lying part
    /// would end 0.09 in short of where the eleventh standing part's straight top begins, and their R1 corners
    /// would pass 0.012 in apart with nothing shared, which no cut can take; the rows kept the spacing and only 28
    /// seams came out. Slid 1.25 in along, five steps of the spacing, every end of the lying row rests on a
    /// standing part's straight top: each lying part shares with the six standing ones under it, 46 seams, all 26
    /// parts in one block, no longer than before.</summary>
    [Fact]
    public void ALyingRowSlidesAlongUntilEveryEndRestsOnAStandingPart()
    {
      var poses = RowPatternPacker.Poses(this.LoadTest2(), new[] { 0, 90 }, Gap, Tolerance);

      var layout = RowPatternPacker.Plan(UsableW, UsableH, poses, Gap, Spacing, 43, 100, true, Tolerance, stack: true, bridge: true)!;

      layout.Placed.Should().Be(26);
      layout.Unshared.Should().Be(0);
      layout.Bridged.Should().Be(1);
      layout.Seams.Should().HaveCount(46);
      Clusters(layout).Should().Equal(26);
      layout.Slots.Where(s => s.PoseIndex == 1).Min(s => s.MinX).Should().BeApproximately(5 * Spacing, 1e-9);
      layout.ExtentX.Should().BeApproximately((17 * 7) + (16 * Gap), 1e-6);
      layout.ExtentY.Should().BeApproximately(36 + Gap + (3 * 7) + (2 * Gap), 1e-6);
    }

    /// <summary>The wider bar's nineteen on a sheet: the bridged block passes every check a searched sheet passes,
    /// with 31 shared cuts, one cluster, no part on its own and no pair inside the spacing without a shared cut,
    /// from the corner of the sheet. Four-way rotation gives the same poses.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void NineteenWiderBarsPassTheCommonLineChecksAsOneBridgedBlock(int turns)
    {
      int[] angles = turns == 4 ? new[] { 0, 90, 180, 270 } : new[] { 0, 90 };

      var (placements, intents) = SparrowNestService.BuildRowPatternSheet(
        Load(this.wideBarPath),
        0,
        angles,
        false,
        (CommonCuttingMode.SamePart, 0, "direct", true, Spacing),
        19,
        120,
        60,
        Margin,
        Gap,
        Tolerance,
        100,
        null);

      placements.Should().NotBeNull("the bridged block must pass the checks");
      var sheet = SheetOf(placements!, intents!);
      var clusters = CommonLineGeometryResolver.Resolve(sheet).Clusters.Select(c => c.PartIds.Count).ToList();
      this.output.WriteLine($"placements={placements!.Count} intents={intents!.Count} clusters={string.Join(",", clusters)}");

      placements.Should().HaveCount(19);
      intents.Should().HaveCount(31, "the bridged block, not the fallback to bands at the spacing");
      clusters.Should().Equal(19);
      Isolated(sheet).Should().Be(0);
      UnsafePairs(sheet).Should().Be(0);
      placements.Min(p => p.PlacedPart.MinX).Should().BeApproximately(Margin, 1e-6);
      placements.Min(p => p.PlacedPart.MinY).Should().BeApproximately(Margin, 1e-6);
      var standing = placements.Single(p => p.PlacedPart.MaxX - p.PlacedPart.MinX < 10);
      standing.PlacedPart.MinX.Should().BeGreaterThan(placements.Where(p => p != standing).Max(p => p.PlacedPart.MaxX), "the standing part closes the layout");
      standing.PlacedPart.MinY.Should().BeApproximately(Margin, 1e-6);
    }

    [Fact]
    public void Test2SharesItsStraightSidesStandingAndLying()
    {
      var poses = RowPatternPacker.Poses(this.LoadTest2(), new[] { 0, 90, 180, 270 }, Gap, Tolerance);

      poses.Should().HaveCount(2, "at 180 and 270 degrees the box and the seams are the same as at 0 and 90");
      poses[0].Angle.Should().Be(0);
      poses[0].W.Should().BeApproximately(7, 1e-6);
      poses[0].H.Should().BeApproximately(36, 1e-6);
      poses[0].SeamAlongX.Should().BeApproximately(34, 1e-6);
      poses[0].SeamAlongY.Should().BeApproximately(5, 1e-6);
      poses[1].Angle.Should().Be(90);
      poses[1].SeamAlongX.Should().BeApproximately(5, 1e-6);
      poses[1].SeamAlongY.Should().BeApproximately(34, 1e-6);
    }

    /// <summary>A U: its two outer sides face each other in full, so the copies would share them. With a half
    /// turn allowed, one copy's arm fits into the other's notch, so laying it out by its box would throw that
    /// away and the search is left to it. With only 0 and 90 degrees nothing of another copy fits into the
    /// notch, and the box is as tight as any layout.</summary>
    [Theory]
    [InlineData(4, false)]
    [InlineData(2, true)]
    public void APartIsOnlyLaidOutByItsBoxWhenNoCopyReachesIntoIt(int turns, bool keeps)
    {
      var notched = new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(7, 10),
        new SvgPoint(7, 3),
        new SvgPoint(3, 3),
        new SvgPoint(3, 10),
        new SvgPoint(0, 10),
      });
      int[] angles = turns == 4 ? new[] { 0, 90, 180, 270 } : new[] { 0, 90 };
      var poses = RowPatternPacker.Poses(notched, angles, Gap, Tolerance);

      poses.Should().NotBeEmpty("the outer sides of the arms face each other in full");
      RowPatternPacker.CopiesKeepToTheirBoxes(notched, poses).Should().Be(keeps);
    }

    /// <summary>The U above, its notch 7 in deep, and one whose notch is only 3 in deep. With a half turn an arm
    /// of a copy pushes into either notch by more than a quarter of the box, but the shallow U fills 88 % of its
    /// box and the deep one 72 %: only the shallow one leaves so little air that its box gives little away.</summary>
    [Theory]
    [InlineData(3D, false)]
    [InlineData(7D, true)]
    public void APartFillsItsBoxWhenItsNotchIsShallow(double notchFloor, bool fills)
    {
      var notched = new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(7, 10),
        new SvgPoint(7, notchFloor),
        new SvgPoint(3, notchFloor),
        new SvgPoint(3, 10),
        new SvgPoint(0, 10),
      });
      var poses = RowPatternPacker.Poses(notched, new[] { 0, 90, 180, 270 }, Gap, Tolerance);

      poses.Should().NotBeEmpty("the outer sides of the arms face each other in full");
      RowPatternPacker.CopiesKeepToTheirBoxes(notched, poses).Should().BeFalse("an arm of a copy turned half a turn fits into the notch");
      RowPatternPacker.FillsItsBoxes(notched, poses).Should().Be(fills);
    }

    /// <summary>The same U with a half turn allowed, whose copies reach into each other, through the whole job
    /// without the engine. Ten fit on one sheet, so the box layout cannot cost a sheet and the pattern takes
    /// them; two hundred take several, where nesting the arms into the notches could save one, so the pattern
    /// leaves them to the search. With a shallow notch there is little to save, and the pattern takes two
    /// hundred too, eleven to a row.</summary>
    [Theory]
    [InlineData(3D, 10, 1)]
    [InlineData(3D, 200, 0)]
    [InlineData(7D, 200, 4)]
    public void APartThatNestsIntoItsCopiesIsOnlyLaidOutByItsBoxWhenTheJobFitsOnOneSheetOrItFillsTheBox(double notchFloor, int quantity, int sheets)
    {
      string notAnEngine = Path.Combine(this.directory, "sparrow.exe");
      File.WriteAllBytes(notAnEngine, Array.Empty<byte>());
      string uPath = Path.Combine(this.directory, "u.dxf");
      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2000;
      dxf.Entities.Add(new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = 0D },
        new DxfLwPolylineVertex { X = 10D, Y = 0D },
        new DxfLwPolylineVertex { X = 10D, Y = 10D },
        new DxfLwPolylineVertex { X = 7D, Y = 10D },
        new DxfLwPolylineVertex { X = 7D, Y = notchFloor },
        new DxfLwPolylineVertex { X = 3D, Y = notchFloor },
        new DxfLwPolylineVertex { X = 3D, Y = 10D },
        new DxfLwPolylineVertex { X = 0D, Y = 10D },
      })
      {
        IsClosed = true,
      });
      dxf.Save(uPath, true);
      var parts = new[]
      {
        new RasterPartInfo
        {
          Path = uPath,
          Quantity = quantity,
          Rotations = 4,
          Spacing = Spacing,
          Cc = CommonCuttingMode.SamePart,
          CommonCuttingDomain = "direct",
          RequiresMergedLines = true,
        },
      };

      INestResult result = SparrowNestService.NestByRowPattern(
        parts,
        new[] { (120, 60, 1, true) },
        4,
        Spacing,
        Margin,
        notAnEngine,
        out string error,
        commonLineGap: Gap,
        geometryTolerance: Tolerance);

      if (sheets > 0)
      {
        result.Should().NotBeNull(error);
        result.UsedSheets.Should().HaveCount(sheets);
        result.UsedSheets.Sum(s => s.PartPlacements.Count).Should().Be(quantity);
        result.UsedSheets.Should().OnlyContain(s => Isolated(s) == 0, "every part shares a cut");
      }
      else
      {
        result.Should().BeNull();
      }
    }

    /// <summary>Neither test2's rounded corners nor the slotted bar's slots let a copy in: a copy of the bar
    /// that pushed a finger into a slot would hit the long straight edge beside it.</summary>
    [Fact]
    public void Test2AndTheSlottedBarKeepToTheirBoxes()
    {
      int[] angles = { 0, 90, 180, 270 };
      INfp test2 = Load(this.test2Path);
      INfp bar = Load(this.barPath);

      RowPatternPacker.CopiesKeepToTheirBoxes(test2, RowPatternPacker.Poses(test2, angles, Gap, Tolerance)).Should().BeTrue();
      RowPatternPacker.CopiesKeepToTheirBoxes(bar, RowPatternPacker.Poses(bar, angles, Gap, Tolerance)).Should().BeTrue();
    }

    /// <summary>The slotted bar's flat back only meets the flat back of a copy turned half a turn, and its
    /// slotted side only the slotted side of one: three straight pieces, 1 + 8 + 1 in. Its ends meet in the same
    /// pose. Standing, it is the other way round.</summary>
    [Fact]
    public void TheSlottedBarMeetsItsTwinAcrossItsLongSides()
    {
      var poses = RowPatternPacker.Poses(Load(this.barPath), new[] { 0, 90, 180, 270 }, Gap, Tolerance);

      poses.Should().HaveCount(2, "at 180 and 270 degrees the bar meets its twin the same way");
      RowPatternPacker.Pose lying = poses[0];
      lying.Angle.Should().Be(0);
      lying.TwinAngle.Should().Be(180);
      lying.W.Should().BeApproximately(12, 1e-6);
      lying.H.Should().BeApproximately(1.5, 1e-6);
      lying.AlongX.Should().Be(RowPatternPacker.Link.Same);
      lying.SeamAlongX.Should().BeApproximately(1.5, 1e-6);
      lying.AlongY.Should().Be(RowPatternPacker.Link.Alternate);
      lying.Join(false, false, true)!.Length.Should().BeApproximately(12, 1e-6, "flat back against flat back");
      lying.Join(false, true, false)!.Length.Should().BeApproximately(10, 1e-6, "slotted side against slotted side");
      lying.Join(false, true, false)!.Edges.Should().HaveCount(3);
      lying.Join(false, false, false).Should().BeNull("a flat back against a slotted side leaves pieces at the gap with no seam");

      RowPatternPacker.Pose standing = poses[1];
      standing.Angle.Should().Be(90);
      standing.TwinAngle.Should().Be(270);
      standing.AlongX.Should().Be(RowPatternPacker.Link.Alternate);
      standing.AlongY.Should().Be(RowPatternPacker.Link.Same);
      standing.SeamAlongY.Should().BeApproximately(1.5, 1e-6);
    }

    /// <summary>
    /// 150 slotted bars on a 120 x 60 sheet, worked out by hand. Turned against each other by turns, two bars
    /// share 12 in (flat backs) or 10 in (slotted sides); their ends share 1.5 in. A block is held to 100 parts.
    /// The shortest is four columns of lying bars, 38, 38, 37 and 37 high, two to a block: 48.27 in along the
    /// sheet. Up the columns that is 2 x (19 x 12 + 18 x 10) + 2 x (18 x 12 + 18 x 10) = 1608 in, and across the
    /// ends of each block 38 + 37 ends, 112.5 in: 1720.5 in. Three columns of lying bars beside 21 columns of
    /// four standing ones would share more, 1736.5 in, but reach 68 in along the sheet.
    /// </summary>
    [Fact]
    public void AHundredAndFiftySlottedBarsAllShareInColumnsOfTwins()
    {
      var poses = RowPatternPacker.Poses(Load(this.barPath), new[] { 0, 90, 180, 270 }, Gap, Tolerance);

      var layout = RowPatternPacker.Plan(UsableW, UsableH, poses, Gap, Spacing, 150, 100, true, Tolerance, stack: true)!;

      layout.Placed.Should().Be(150);
      layout.Unshared.Should().Be(0);
      layout.SharedLength.Should().BeApproximately(1608 + 112.5, 1e-6);
      Clusters(layout).OrderBy(n => n).Should().Equal(74, 76);
      layout.Slots.Should().OnlyContain(slot => slot.PoseIndex == 0);
      layout.Slots.Count(slot => slot.Flipped).Should().Be((2 * 19) + (2 * 18), "every other bar up a column is turned");
      layout.ExtentX.Should().BeApproximately((4 * 12) + (2 * Gap) + Spacing, 1e-6);
      layout.ExtentY.Should().BeApproximately((38 * 1.5) + (37 * Gap), 1e-6);
    }

    /// <summary>Without stacking, the shortest is four rows of 32 standing bars, 48.31 in along the sheet, with
    /// the other 22 lying end to end in six rows under them. A row of 32 shares 31 seams of 10 and 12 in by turns,
    /// 16 x 10 + 15 x 12 = 340 in, and the lying rows of 4, 4, 4, 4, 3 and 3 share 16 ends, 24 in: 1384 in. Two
    /// rows of 75 standing bars would share more, 1628 in, but reach 113.24 in.</summary>
    [Fact]
    public void WithoutAGridTheBarsStandInFourShortRows()
    {
      var poses = RowPatternPacker.Poses(Load(this.barPath), new[] { 0, 90, 180, 270 }, Gap, Tolerance);

      var layout = RowPatternPacker.Plan(UsableW, UsableH, poses, Gap, Spacing, 150, 100, true, Tolerance)!;

      layout.Placed.Should().Be(150);
      layout.Unshared.Should().Be(0);
      layout.SharedLength.Should().BeApproximately((4 * 340) + (16 * 1.5), 1e-6);
      layout.Slots.Count(slot => slot.PoseIndex == 1).Should().Be(4 * 32);
      layout.ExtentX.Should().BeApproximately((32 * 1.5) + (31 * Gap), 1e-6);
      Clusters(layout).OrderBy(n => n).Should().Equal(3, 3, 4, 4, 4, 4, 32, 32, 32, 32);
    }

    /// <summary>The gate for the slotted bar: its sheet passes every check a searched sheet passes, with three
    /// seams between two copies whose slots face each other, and no part on its own. Up the four columns of lying
    /// bars, a back against a back is one shared edge and two slotted sides are three: 2 x (19 + 18 x 3) +
    /// 2 x (18 + 18 x 3) = 290. The ends across each block give 38 + 37 = 75 more.</summary>
    [Fact]
    public void ASheetOfSlottedBarsPassesTheCommonLineChecksWithEveryPartSharing()
    {
      var (placements, intents) = SparrowNestService.BuildRowPatternSheet(
        Load(this.barPath),
        0,
        new[] { 0, 90, 180, 270 },
        false,
        (CommonCuttingMode.SamePart, 0, "direct", true, Spacing),
        150,
        120,
        60,
        Margin,
        Gap,
        Tolerance,
        100,
        null);

      placements.Should().NotBeNull("the pattern must pass the checks with its slots facing each other");
      this.output.WriteLine($"placements={placements!.Count} intents={intents!.Count}");
      var sheet = SheetOf(placements, intents);
      var clusters = CommonLineGeometryResolver.Resolve(sheet).Clusters.Select(c => c.PartIds.Count).OrderBy(n => n).ToList();
      this.output.WriteLine($"clusters={string.Join(",", clusters)}");

      placements.Should().HaveCount(150);
      intents.Should().HaveCount(290 + 75, "the grid, not the fallback to plain rows");
      clusters.Should().Equal(74, 76);
      Isolated(sheet).Should().Be(0);
      UnsafePairs(sheet).Should().Be(0);
      placements.Count(p => p.Rotation == 180).Should().Be((2 * 19) + (2 * 18));
      placements.Should().OnlyContain(p => p.Rotation == 0 || p.Rotation == 180, "every bar lies");
    }

    /// <summary>The job end to end without the engine: 150 bars, one sheet, every bar sharing a cut.</summary>
    [Fact]
    public void AHundredAndFiftySlottedBarsComeOutOnOneSheetAllSharing()
    {
      string notAnEngine = Path.Combine(this.directory, "sparrow.exe");
      File.WriteAllBytes(notAnEngine, Array.Empty<byte>());
      var parts = new[]
      {
        new RasterPartInfo
        {
          Path = this.barPath,
          Quantity = 150,
          Rotations = 4,
          Spacing = Spacing,
          Cc = CommonCuttingMode.Unrestricted,
          CommonCuttingDomain = "direct",
          RequiresMergedLines = true,
        },
      };
      var clock = Stopwatch.StartNew();

      INestResult result = SparrowNestService.NestByRowPattern(
        parts,
        new[] { (120, 60, 1, false) },
        4,
        Spacing,
        Margin,
        notAnEngine,
        out string error,
        commonLineGap: Gap,
        geometryTolerance: Tolerance);
      clock.Stop();
      this.output.WriteLine($"row pattern, 150 bars: {clock.ElapsedMilliseconds} ms");

      result.Should().NotBeNull(error);
      var sheets = result.UsedSheets.ToList();
      sheets.Should().ContainSingle();
      sheets[0].PartPlacements.Should().HaveCount(150);
      result.UnplacedParts.Should().BeEmpty();
      Isolated(sheets[0]).Should().Be(0);
      UnsafePairs(sheets[0]).Should().Be(0);
    }

    /// <summary>
    /// The gate for the whole idea: a sheet of test2 copies laid out by the pattern passes the same checks a
    /// searched sheet passes before its seams are kept, resolves into cuttable clusters, and leaves no pair
    /// inside the spacing without a shared cut between them. The lying grid slid onto the standing band makes
    /// every part one cluster.
    /// </summary>
    [Fact]
    public void ASheetOfTest2PassesTheCommonLineChecksWithEveryPartSharing()
    {
      INfp outline = this.LoadTest2();

      var (placements, intents) = SparrowNestService.BuildRowPatternSheet(
        outline,
        0,
        new[] { 0, 90 },
        false,
        (CommonCuttingMode.SamePart, 0, "direct", true, Spacing),
        43,
        120,
        60,
        Margin,
        Gap,
        Tolerance,
        100,
        null);

      placements.Should().NotBeNull("the pattern must pass the checks on the operator's own part");
      placements!.Should().HaveCount(26);
      intents.Should().HaveCount(46, "the stacked grid slid onto the band, not the fallback to plain bands");
      var records = intents!.Select(intent => CommonLineGeometryResolver.CreateCommonEdge(
        intent.PartA,
        intent.EdgeAIndex,
        intent.EdgeAStart,
        intent.EdgeAEnd,
        intent.PartB,
        intent.EdgeBIndex,
        intent.EdgeBStart,
        intent.EdgeBEnd,
        Gap,
        Tolerance)).ToList();
      var sheet = new SheetPlacement(PlacementTypeEnum.BoundingBox, Sheet.NewSheet(1, 120, 60), placements, 0, true, records, Gap, Tolerance);

      var clusters = CommonLineGeometryResolver.Resolve(sheet).Clusters.Select(c => c.PartIds.Count).OrderByDescending(n => n).ToList();

      clusters.Should().Equal(26);
      Isolated(sheet).Should().Be(0);
      UnsafePairs(sheet).Should().Be(0);
    }

    /// <summary>
    /// The operator saved the drawing and nested it with the drawing still open in the CAD program, which
    /// holds it for writing. Every shared cut reads that drawing again for its exact geometry; when those
    /// reads refused the file, every seam failed and all the copies came out on their own.
    /// </summary>
    [Fact]
    public void TheSheetStillSharesEveryCutWhileTheDrawingIsOpenInTheCadProgram()
    {
      INfp outline = this.LoadTest2();

      using (new FileStream(this.test2Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
      {
        var (placements, intents) = SparrowNestService.BuildRowPatternSheet(
          outline,
          0,
          new[] { 0, 90 },
          false,
          (CommonCuttingMode.SamePart, 0, "direct", true, Spacing),
          43,
          120,
          60,
          Margin,
          Gap,
          Tolerance,
          100,
          null);

        placements.Should().NotBeNull("an open drawing is still readable");
        placements!.Should().HaveCount(26);
        intents.Should().HaveCount(46);
      }
    }

    /// <summary>The operator's job end to end, without the engine: 800 copies on unlimited 120 x 60 stock.</summary>
    [Fact]
    public void EightHundredCopiesComeOutAsThirtyFullSheetsAndTheRest()
    {
      string notAnEngine = Path.Combine(this.directory, "sparrow.exe");
      File.WriteAllBytes(notAnEngine, Array.Empty<byte>());
      var parts = new[]
      {
        new RasterPartInfo
        {
          Path = this.test2Path,
          Quantity = 800,
          Rotations = RotationCodes.RotZeroAnd90,
          Spacing = Spacing,
          Cc = CommonCuttingMode.SamePart,
          CommonCuttingDomain = "direct",
          RequiresMergedLines = true,
        },
      };
      var clock = Stopwatch.StartNew();

      INestResult result = SparrowNestService.NestByRowPattern(
        parts,
        new[] { (120, 60, 1, true) },
        RotationCodes.RotZeroAnd90,
        Spacing,
        Margin,
        notAnEngine,
        out string error,
        commonLineGap: Gap,
        geometryTolerance: Tolerance);
      clock.Stop();
      this.output.WriteLine($"row pattern, 800 copies: {clock.ElapsedMilliseconds} ms");

      result.Should().NotBeNull(error);
      var sheets = result.UsedSheets.ToList();
      sheets.Should().HaveCount(31);
      sheets.Take(30).Should().OnlyContain(s => s.PartPlacements.Count == 26);
      sheets[30].PartPlacements.Should().HaveCount(20);
      result.UnplacedParts.Should().BeEmpty();
      sheets.Sum(Isolated).Should().Be(0);
      UnsafePairs(sheets[0]).Should().Be(0);
      UnsafePairs(sheets[30]).Should().Be(0);
    }

    private static int[] Clusters(RowPatternPacker.Layout layout)
    {
      var parent = Enumerable.Range(0, layout.Placed).ToArray();
      int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
      foreach (var seam in layout.Seams)
      {
        parent[Find(seam.A)] = Find(seam.B);
      }

      return Enumerable.Range(0, layout.Placed).GroupBy(Find).Select(g => g.Count()).ToArray();
    }

    private static int Isolated(ISheetPlacement sheet)
    {
      var welded = new HashSet<int>((sheet.CommonEdges ?? Array.Empty<CommonEdge>()).SelectMany(e => new[] { e.PartAId, e.PartBId }));
      return sheet.PartPlacements.Count(p => !welded.Contains(p.Id));
    }

    /// <summary>Two cuts closer than the spacing with no shared cut recorded between them cannot be cut, unless
    /// they are the opposite corners of a cross where four seams meet: those two share two neighbours, and the
    /// common-line checks cut the cross as one junction.</summary>
    private static int UnsafePairs(ISheetPlacement sheet)
    {
      var welded = new HashSet<(int, int)>((sheet.CommonEdges ?? Array.Empty<CommonEdge>())
        .Select(e => (Math.Min(e.PartAId, e.PartBId), Math.Max(e.PartAId, e.PartBId))));
      var neighbours = welded
        .SelectMany(pair => new[] { pair, (pair.Item2, pair.Item1) })
        .GroupBy(pair => pair.Item1)
        .ToDictionary(group => group.Key, group => group.Select(pair => pair.Item2).ToHashSet());
      bool Across(int a, int b) => neighbours.TryGetValue(a, out var ofA) && neighbours.TryGetValue(b, out var ofB) && ofA.Count(ofB.Contains) >= 2;
      var on = sheet.PartPlacements.ToList();
      int unsafePairs = 0;
      for (int i = 0; i < on.Count; i++)
      {
        for (int j = i + 1; j < on.Count; j++)
        {
          if (!welded.Contains((Math.Min(on[i].Id, on[j].Id), Math.Max(on[i].Id, on[j].Id)))
              && !Across(on[i].Id, on[j].Id)
              && PlacementCollision.TooClose(on[i].PlacedPart, on[j].PlacedPart, Spacing - PlacementCollision.PlacementNoise, 0D))
          {
            unsafePairs++;
          }
        }
      }

      return unsafePairs;
    }

    /// <summary>The operator's part as its drawing has it: one closed polyline with four R1 bulges, and five
    /// holes that play no part in the outside seams.</summary>
    private static void CreateTest2Copy(string path)
    {
      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2000;
      dxf.Entities.Add(new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 7D, Y = 35D },
        new DxfLwPolylineVertex { X = 7D, Y = 1D, Bulge = QuarterCircleBulge },
        new DxfLwPolylineVertex { X = 6D, Y = 0D },
        new DxfLwPolylineVertex { X = 1D, Y = 0D, Bulge = QuarterCircleBulge },
        new DxfLwPolylineVertex { X = 0D, Y = 1D },
        new DxfLwPolylineVertex { X = 0D, Y = 35D, Bulge = QuarterCircleBulge },
        new DxfLwPolylineVertex { X = 1D, Y = 36D },
        new DxfLwPolylineVertex { X = 6D, Y = 36D, Bulge = QuarterCircleBulge },
      })
      {
        IsClosed = true,
      });
      dxf.Entities.Add(new DxfCircle(new DxfPoint(3.5D, 33D, 0D), 1D));
      dxf.Entities.Add(new DxfCircle(new DxfPoint(2.5D, 34D, 0D), 0.125D));
      dxf.Entities.Add(new DxfCircle(new DxfPoint(4.5D, 34D, 0D), 0.125D));
      dxf.Entities.Add(new DxfCircle(new DxfPoint(4.5D, 32D, 0D), 0.125D));
      dxf.Entities.Add(new DxfCircle(new DxfPoint(2.5D, 32D, 0D), 0.125D));
      dxf.Save(path, true);
    }

    /// <summary>An invented part: a <paramref name="w"/> x <paramref name="h"/> in rectangle with R1 corners, one
    /// closed polyline with four bulges.</summary>
    internal static void CreateRoundedRectangle(string path, double w, double h)
    {
      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2000;
      dxf.Entities.Add(new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = w, Y = h - 1 },
        new DxfLwPolylineVertex { X = w, Y = 1D, Bulge = QuarterCircleBulge },
        new DxfLwPolylineVertex { X = w - 1, Y = 0D },
        new DxfLwPolylineVertex { X = 1D, Y = 0D, Bulge = QuarterCircleBulge },
        new DxfLwPolylineVertex { X = 0D, Y = 1D },
        new DxfLwPolylineVertex { X = 0D, Y = h - 1, Bulge = QuarterCircleBulge },
        new DxfLwPolylineVertex { X = 1D, Y = h },
        new DxfLwPolylineVertex { X = w - 1, Y = h, Bulge = QuarterCircleBulge },
      })
      {
        IsClosed = true,
      });
      dxf.Save(path, true);
    }

    /// <summary>An invented part, not one of the operator's: a 12 x 1.5 in bar with two slots 1 in wide and
    /// 0.75 in deep (a 0.5 in radius at the end) opening from one long side, 1.5 in from each end.</summary>
    private static void CreateSlottedBar(string path)
    {
      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2000;
      dxf.Entities.Add(new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 12D, Y = 0D },
        new DxfLwPolylineVertex { X = 11D, Y = 0D },
        new DxfLwPolylineVertex { X = 11D, Y = 0.25D, Bulge = 1D },
        new DxfLwPolylineVertex { X = 10D, Y = 0.25D },
        new DxfLwPolylineVertex { X = 10D, Y = 0D },
        new DxfLwPolylineVertex { X = 2D, Y = 0D },
        new DxfLwPolylineVertex { X = 2D, Y = 0.25D, Bulge = 1D },
        new DxfLwPolylineVertex { X = 1D, Y = 0.25D },
        new DxfLwPolylineVertex { X = 1D, Y = 0D },
        new DxfLwPolylineVertex { X = 0D, Y = 0D },
        new DxfLwPolylineVertex { X = 0D, Y = 1.5D },
        new DxfLwPolylineVertex { X = 12D, Y = 1.5D },
      })
      {
        IsClosed = true,
      });
      dxf.Save(path, true);
    }

    private static INfp Load(string path)
    {
      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(path));
      detail.TryConvertToNfp(0, out INfp outline).Should().BeTrue();
      return outline;
    }

    private static SheetPlacement SheetOf(List<IPartPlacement> placements, List<SparrowNestService.CommonEdgeIntent> intents)
    {
      var records = intents.Select(intent => CommonLineGeometryResolver.CreateCommonEdge(
        intent.PartA,
        intent.EdgeAIndex,
        intent.EdgeAStart,
        intent.EdgeAEnd,
        intent.PartB,
        intent.EdgeBIndex,
        intent.EdgeBStart,
        intent.EdgeBEnd,
        Gap,
        Tolerance)).ToList();
      return new SheetPlacement(PlacementTypeEnum.BoundingBox, Sheet.NewSheet(1, 120, 60), placements, 0, true, records, Gap, Tolerance);
    }

    private INfp LoadTest2() => Load(this.test2Path);
  }
}
