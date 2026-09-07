namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
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
  /// A curve reaches the nester as straight chords, and a chord lies INSIDE its arc. Pulling a
  /// neighbour up to the common-line gap from a chord therefore parks it closer than that from the real
  /// curve, and the cut eats the difference. Nothing catches it downstream either: every later check
  /// measures the same polygon, which has the same chord in it.
  /// <para>
  /// What keeps a chord out is the DXF the part came from: the planner reads the source, sees an ARC,
  /// and refuses to treat its chords as drawn faces. So these tests load real files, exactly as a job
  /// does. Hand-built points with no source would be a different question and would prove nothing here.
  /// </para>
  /// </summary>
  public sealed class ArcChordIsNeverACommonCutFixture : IDisposable
  {
    private const double NormalGap = 0.3;
    private const double CommonLineGap = 0.01;
    private const double Radius = 13.139;

    private readonly ITestOutputHelper output;
    private readonly string directory;

    public ArcChordIsNeverACommonCutFixture(ITestOutputHelper output)
    {
      this.output = output;
      this.directory = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.directory);
    }

    public void Dispose()
    {
      try
      {
        Directory.Delete(this.directory, true);
      }
      catch (IOException)
      {
        // Test cleanup must not hide a geometry failure.
      }
    }

    /// <summary>
    /// The arc runs 39 to 141 degrees, which with the parser's 6 degree ceiling on arc steps samples
    /// 39, 45 ... 87, 93 ... So the chord from 87 to 93 straddles the top dead centre symmetrically and
    /// comes out EXACTLY horizontal: an axis-aligned face, long enough to qualify, that no draughtsman
    /// ever drew.
    /// </summary>
    [Fact]
    public void ABarIsNotWeldedOntoTheChordOfAnArc()
    {
      IPartPlacement arc = this.LoadArcTopped(0);
      SvgPoint[] pts = arc.PlacedPart.Points;
      double apexY = arc.PlacedPart.MaxY;

      // Premise, asserted rather than assumed: if the tessellation ever changes, this fixture stops
      // reproducing the case and has to be rebuilt rather than quietly passing.
      int flat = -1;
      for (int i = 0; i < pts.Length; i++)
      {
        SvgPoint a = pts[i];
        SvgPoint b = pts[(i + 1) % pts.Length];
        if (Math.Abs(a.Y - b.Y) < 1e-9 && Math.Abs(a.X - b.X) > 2 * NormalGap && a.Y > apexY * 0.9)
        {
          flat = i;
          break;
        }
      }

      flat.Should().BeGreaterOrEqualTo(0, "the fixture needs a horizontal chord near the top of the arc to reproduce this");
      double chordY = pts[flat].Y;
      this.output.WriteLine(FormattableString.Invariant(
        $"chord at y={chordY:0.####}, true arc apex y={Radius:0.####}, so the curve bulges {Radius - chordY:0.####} past it"));

      // A bar parked just over the normal gap above that chord, overlapping it well.
      double chordMidX = (pts[flat].X + pts[(flat + 1) % pts.Length].X) / 2D;
      IPartPlacement bar = this.LoadBar("over-the-chord", 1D, 4D, 1);
      bar.X = chordMidX - 0.5D;
      bar.Y = chordY + 0.35D;
      double barBottomBefore = bar.PlacedPart.MinY;

      Plan(arc, bar).Edges.Should().BeEmpty(
        "a chord is not a face, so there is nothing here to share a cut with");
      bar.PlacedPart.MinY.Should().Be(barBottomBefore, "and nothing moved to meet one");
    }

    /// <summary>
    /// The control, and the half that makes the test worth anything: the SAME part, the same distance,
    /// but the neighbour offered a real straight edge instead. That one still welds, so the test above
    /// is not passing merely because this part never welds to anything.
    /// </summary>
    [Fact]
    public void ARealStraightEdgeOfTheSamePartStillWelds()
    {
      IPartPlacement arc = this.LoadArcTopped(0);

      // Its right-hand side is a genuine LINE, the tallest straight run on the part.
      double sideX = arc.PlacedPart.MaxX;
      IPartPlacement bar = this.LoadBar("beside-the-line", 3D, 5D, 1);
      bar.X = sideX + 0.35D;
      bar.Y = arc.PlacedPart.MinY + 1D;

      Plan(arc, bar).Edges.Should().ContainSingle(
        "the side of the part is a real drawn line and still shares its cut");
      (bar.PlacedPart.MinX - sideX).Should().BeApproximately(CommonLineGap, 1e-6,
        "a shared cut closes to the configured common-line gap");
    }

    /// <summary>Runs the planner that actually decides this, on a sheet big enough not to interfere.</summary>
    private static SparrowNestService.DirectCommonLineSnapResult Plan(IPartPlacement first, IPartPlacement second)
      => SparrowNestService.SnapDirectCommonLineEdges(
        new List<IPartPlacement> { first, second },
        new Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)>
        {
          { 0, (CommonCuttingMode.Unrestricted, 0, "direct", true, NormalGap) },
          { 1, (CommonCuttingMode.Unrestricted, 1, "direct", true, NormalGap) },
        },
        sheetW: 200D,
        sheetH: 200D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: 0.0001D);

    /// <summary>A D-shape: an arc across the top, three real lines closing it underneath. Written to a
    /// real file, because the planner resolves a part's source geometry through its path.</summary>
    private IPartPlacement LoadArcTopped(int source)
    {
      double x = Radius * Math.Cos(39 * Math.PI / 180.0);
      double y = Radius * Math.Sin(39 * Math.PI / 180.0);

      // Drawn with its left edge on the origin, the way a part comes off a CAD sheet, so the placement's
      // X really is where its left edge lands.
      var file = new DxfFile();
      file.Entities.Add(new DxfArc(new DxfPoint(x, 0, 0), Radius, 39, 141));
      file.Entities.Add(new DxfLine(new DxfPoint(0, y, 0), new DxfPoint(0, 0, 0)));
      file.Entities.Add(new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(2 * x, 0, 0)));
      file.Entities.Add(new DxfLine(new DxfPoint(2 * x, 0, 0), new DxfPoint(2 * x, y, 0)));

      return this.Load(file, "arc-topped", source);
    }

    /// <summary>The neighbour, also a real file: a part with no readable source has no drawn faces to
    /// offer, so a hand-built polygon here would make both tests agree for the wrong reason.</summary>
    private IPartPlacement LoadBar(string name, double w, double h, int source)
    {
      var file = new DxfFile();
      file.Entities.Add(new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(w, 0, 0)));
      file.Entities.Add(new DxfLine(new DxfPoint(w, 0, 0), new DxfPoint(w, h, 0)));
      file.Entities.Add(new DxfLine(new DxfPoint(w, h, 0), new DxfPoint(0, h, 0)));
      file.Entities.Add(new DxfLine(new DxfPoint(0, h, 0), new DxfPoint(0, 0, 0)));
      return this.Load(file, name, source);
    }

    private IPartPlacement Load(DxfFile file, string name, int source)
    {
      string path = Path.Combine(this.directory, name + ".dxf");
      file.Save(path);

      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(path));
      detail.TryConvertToNfp(source, out INfp part).Should().BeTrue();
      part.Name.Should().Be(path, "the planner resolves the source catalog through Part.Name");
      return new PartPlacement(part) { Id = source, Source = source, X = 0, Y = 0, Rotation = 0 };
    }
  }
}
