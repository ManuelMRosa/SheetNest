namespace DeepNestLib.CiTests.IO
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Threading.Tasks;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Blocks;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// What the parser does with a drawing that is not plain flat geometry. Reported as issue #3: it knew five
  /// entity types and threw on everything else, so a part drawn as a BLOCK crashed the nest - and so did a
  /// stray dimension or title block.
  /// <para>A part drawn as a block is REFUSED by decision, with a message telling the operator to explode
  /// it: that is what every nesting package in the trade expects. Annotation, by contrast, is dropped -
  /// nobody cuts a dimension - and real geometry that cannot be measured is always reported, never lost.</para>
  /// </summary>
  public class DxfBlockAndAnnotationFixture : IDisposable
  {
    private const double Tol = 0.06; // chord tolerance of the tessellator (0.05) plus float slack

    private readonly string tempDir;

    public DxfBlockAndAnnotationFixture()
    {
      this.tempDir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
      try
      {
        Directory.Delete(this.tempDir, true);
      }
      catch (IOException)
      {
        // Leftover temp files must never fail the suite.
      }
    }

    /// <summary>The reported crash, through the exact call in its stack trace - now a message, not a crash.</summary>
    [Fact]
    public void APartDrawnAsABlockIsRefusedWithSomethingTheOperatorCanActOn()
    {
      string path = this.WriteDxf("block.dxf", dxf =>
      {
        dxf.Blocks.Add(SquareBlock("SQ", 10));
        dxf.Entities.Add(new DxfInsert { Name = "SQ" });
      });

      Action load = () => new NestExecutionHelper().LoadRawDetail(new FileInfo(path));

      load.Should().Throw<Exception>()
        .WithMessage("*not compatible*")
        .WithMessage("*explode the blocks*");
    }

    /// <summary>
    /// The guard that keeps the refusal from swallowing ordinary files. Nearly every real DXF DEFINES blocks
    /// it never places - *MODEL_SPACE, *PAPER_SPACE, a text style, the anonymous *D1 block AutoCAD builds to
    /// draw a dimension. Refusing those would reject almost every drawing in the shop.
    /// </summary>
    [Fact]
    public void ADrawingThatDefinesBlocksButPlacesNoneStillNests()
    {
      string path = this.WriteDxf("defines-only.dxf", dxf =>
      {
        dxf.Blocks.Add(SquareBlock("UNUSED_SYMBOL", 3));
        var dimensionBlock = new DxfBlock { Name = "*D1" };
        dimensionBlock.Entities.Add(new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(1, 0, 0)));
        dxf.Blocks.Add(dimensionBlock);

        foreach (var line in SquareLines(10))
        {
          dxf.Entities.Add(line);
        }
      });

      var nfp = LoadNfp(path);

      nfp.MinX.Should().BeApproximately(0, Tol);
      nfp.MaxX.Should().BeApproximately(10, Tol);
      nfp.MaxY.Should().BeApproximately(10, Tol);
    }

    /// <summary>The wider half of the reported crash: any entity the parser did not know took the nest down.</summary>
    [Fact]
    public void AnnotationIsIgnoredInsteadOfCrashing()
    {
      string path = this.WriteDxf("annotated.dxf", dxf =>
      {
        foreach (var line in SquareLines(10))
        {
          dxf.Entities.Add(line);
        }

        dxf.Entities.Add(new DxfText { Location = new DxfPoint(2, 2, 0), Value = "PART 42", TextHeight = 1 });
        dxf.Entities.Add(new DxfMText { InsertionPoint = new DxfPoint(2, 4, 0), Text = "1/4 MILD STEEL" });
        dxf.Entities.Add(new DxfModelPoint(new DxfPoint(5, 5, 0)));
      });

      var nfp = LoadNfp(path);

      nfp.MinX.Should().BeApproximately(0, Tol);
      nfp.MaxX.Should().BeApproximately(10, Tol);
      nfp.MaxY.Should().BeApproximately(10, Tol);
    }

    [Fact]
    public void ADrawingWithNoCuttableGeometrySaysSo()
    {
      Action load = () => DxfParser.ConvertDxfToRawDetail(
        "notes.dxf",
        new List<DxfEntity> { new DxfText { Value = "just a note", TextHeight = 1 } });

      load.Should().Throw<Exception>().WithMessage("*no geometry*");
    }

    [Fact]
    public void UnmeasurableGeometryIsReportedNotDropped()
    {
      // A part could really be drawn as a filled SOLID; dropping it in silence would nest a wrong shape.
      var entities = new List<DxfEntity>(SquareLines(10)) { new DxfSolid() };

      Action load = () => DxfParser.ConvertDxfToRawDetail("solid.dxf", entities);

      load.Should().Throw<Exception>().WithMessage("*SOLID*");
    }

    [Fact]
    public void EllipseIsTessellated()
    {
      var ellipse = new DxfEllipse(new DxfPoint(0, 0, 0), new DxfVector(10, 0, 0), 0.5)
      {
        StartParameter = 0,
        EndParameter = 2 * Math.PI,
      };

      var raw = DxfParser.ConvertDxfToRawDetail("ellipse.dxf", new List<DxfEntity> { ellipse });
      var points = raw.Outers.SelectMany(o => o.Points).ToList();

      points.Min(p => (double)p.X).Should().BeApproximately(-10, Tol);
      points.Max(p => (double)p.X).Should().BeApproximately(10, Tol);
      points.Min(p => (double)p.Y).Should().BeApproximately(-5, Tol);
      points.Max(p => (double)p.Y).Should().BeApproximately(5, Tol);
    }

    [Fact]
    public void SplineOfDegreeOneFollowsItsControlPoints()
    {
      var spline = new DxfSpline { DegreeOfCurve = 1 };
      foreach (var p in new[]
      {
        new DxfPoint(0, 0, 0), new DxfPoint(10, 0, 0), new DxfPoint(10, 10, 0), new DxfPoint(0, 10, 0), new DxfPoint(0, 0, 0),
      })
      {
        spline.ControlPoints.Add(new DxfControlPoint(p, 1));
      }

      foreach (var k in new double[] { 0, 0, 1, 2, 3, 4, 4 })
      {
        spline.KnotValues.Add(k);
      }

      var raw = DxfParser.ConvertDxfToRawDetail("spline1.dxf", new List<DxfEntity> { spline });
      var points = raw.Outers.SelectMany(o => o.Points).ToList();

      points.Min(p => (double)p.X).Should().BeApproximately(0, Tol);
      points.Max(p => (double)p.X).Should().BeApproximately(10, Tol);
      points.Min(p => (double)p.Y).Should().BeApproximately(0, Tol);
      points.Max(p => (double)p.Y).Should().BeApproximately(10, Tol);
    }

    /// <summary>A quarter circle is exactly representable as a rational quadratic spline (weights
    /// 1, sqrt(2)/2, 1) - so every tessellated point must land on radius 10, which only holds if the
    /// weights are honoured. Ignoring them bows the curve visibly inwards.</summary>
    [Fact]
    public void RationalSplineTracesTheCircleItEncodes()
    {
      var spline = new DxfSpline { DegreeOfCurve = 2, IsRational = true };
      spline.ControlPoints.Add(new DxfControlPoint(new DxfPoint(10, 0, 0), 1));
      spline.ControlPoints.Add(new DxfControlPoint(new DxfPoint(10, 10, 0), Math.Sqrt(2) / 2));
      spline.ControlPoints.Add(new DxfControlPoint(new DxfPoint(0, 10, 0), 1));
      foreach (var k in new double[] { 0, 0, 0, 1, 1, 1 })
      {
        spline.KnotValues.Add(k);
      }

      var raw = DxfParser.ConvertDxfToRawDetail("spline2.dxf", new List<DxfEntity> { spline });
      var points = raw.Outers.SelectMany(o => o.Points).ToList();

      points.Should().HaveCountGreaterThan(4);
      foreach (var p in points)
      {
        Math.Sqrt((p.X * p.X) + (p.Y * p.Y)).Should().BeApproximately(10, Tol);
      }
    }

    /// <summary>A hole must reach the cut file as one arc, not as a polygon: the exporter writes the
    /// parser's own source entities, and sixty short moves instead of one arc is a worse cut.</summary>
    [Fact]
    public async Task ExportKeepsAHoleAsOneCircle()
    {
      string partPath = this.WriteDxf("holed.dxf", dxf =>
      {
        foreach (var line in SquareLines(10))
        {
          dxf.Entities.Add(line);
        }

        dxf.Entities.Add(new DxfCircle(new DxfPoint(5, 5, 0), 1));
      });

      var det = new NestExecutionHelper().LoadRawDetail(new FileInfo(partPath));
      det.TryConvertToNfp(0, out INfp nfp).Should().BeTrue();

      var sheet = Sheet.NewSheet(1, 96, 48);
      var placements = new List<IPartPlacement> { new PartPlacement(nfp) { X = 0, Y = 0, Rotation = 0, Id = 0, Source = 0 } };
      var sheetPlacement = new SheetPlacement(PlacementTypeEnum.BoundingBox, sheet, placements, 0, SvgNest.Config.ClipperScale);
      string outPath = Path.Combine(this.tempDir, "out.dxf");

      await new DxfExporter().Export(outPath, sheetPlacement, false, false);

      var written = DxfFile.Load(outPath).Entities.ToList();
      written.OfType<DxfCircle>().Should().ContainSingle("the hole stays one arc, not a polygon")
        .Which.Radius.Should().BeApproximately(1, 1e-6);
    }

    private static DxfBlock SquareBlock(string name, double side)
    {
      var block = new DxfBlock { Name = name };
      foreach (var line in SquareLines(side))
      {
        block.Entities.Add(line);
      }

      return block;
    }

    private static IEnumerable<DxfLine> SquareLines(double side)
    {
      yield return new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(side, 0, 0));
      yield return new DxfLine(new DxfPoint(side, 0, 0), new DxfPoint(side, side, 0));
      yield return new DxfLine(new DxfPoint(side, side, 0), new DxfPoint(0, side, 0));
      yield return new DxfLine(new DxfPoint(0, side, 0), new DxfPoint(0, 0, 0));
    }

    private static INfp LoadNfp(string path)
    {
      var det = new NestExecutionHelper().LoadRawDetail(new FileInfo(path));
      det.Should().NotBeNull();
      det.TryConvertToNfp(0, out INfp nfp).Should().BeTrue();
      return nfp;
    }

    /// <summary>Writes a drawing to a temp file, declaring a BLOCK_RECORD for every block the test added so
    /// the file is as well-formed as one AutoCAD writes (R13+ requires the table entry).</summary>
    private string WriteDxf(string name, Action<DxfFile> build)
    {
      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2000;
      build(dxf);
      var declared = new HashSet<string>(dxf.BlockRecords.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
      foreach (var block in dxf.Blocks.Where(b => !b.Name.StartsWith("*") && !declared.Contains(b.Name)).ToList())
      {
        dxf.BlockRecords.Add(new DxfBlockRecord(block.Name));
      }

      string path = Path.Combine(this.tempDir, name);
      dxf.Save(path);
      return path;
    }
  }
}
