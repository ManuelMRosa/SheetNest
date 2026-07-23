namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using ClipperLib;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// PoC ONLY — exercises the real product-code <see cref="SparrowNestService"/> end to end: real app
  /// parts → sparrow → mapped <see cref="INestResult"/>. Asserts it returns a renderable result with
  /// every part placed and ~0 pairwise overlap (a correct mapping of sparrow's feasible layout).
  /// Set env SPARROW_EXE to run; otherwise no-ops.
  /// </summary>
  public class SparrowNestServiceFixture
  {
    private readonly ITestOutputHelper output;

    public SparrowNestServiceFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    [Fact]
    public void ProducesRenderableOverlapFreeResult()
    {
      string exe = Environment.GetEnvironmentVariable("SPARROW_EXE");
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("SVC: SPARROW_EXE not set — skipping.");
        return;
      }

      string dxfDir = FindDxfDir();
      dxfDir.Should().NotBeNull();

      // Size the single sheet so any part fits in any 4-way orientation.
      var helper = new NestExecutionHelper();
      double maxExtent = 0;
      var parts = new List<RasterPartInfo>();
      for (int i = 1; i <= 6; i++)
      {
        string path = Path.Combine(dxfDir, $"_{i}.dxf");
        if (File.Exists(path) && helper.LoadRawDetail(new FileInfo(path)) is { } det
            && det.TryConvertToNfp(0, out INfp nfp) && nfp.Points.Length > 2)
        {
          maxExtent = Math.Max(maxExtent, Math.Max(nfp.MaxX - nfp.MinX, nfp.MaxY - nfp.MinY));

          // Winding guard: dilation must GROW every part, whatever its point orientation. If a CW-wound
          // part shrank instead, its neighbours would end up touching (the "some joined, some not" bug).
          AbsArea(Inflate(nfp, 1.0)).Should().BeGreaterThan(AbsArea(nfp), $"dilating _{i}.dxf must grow it");

          parts.Add(new RasterPartInfo { Path = path, Quantity = 3 });
        }
      }

      parts.Should().NotBeEmpty();
      double sheetH = Math.Ceiling(maxExtent);
      double sheetW = sheetH * 10;
      const double spacing = 2.0;

      var result = SparrowNestService.Nest(parts, sheetW, sheetH, 4, spacing, 0.5, 15, exe, out string err);

      result.Should().NotBeNull($"the service must return a result (err={err})");
      int placed = result.UsedSheets.Sum(s => s.PartPlacements.Count);
      placed.Should().Be(parts.Count * 3, "sparrow places every part");

      // The pack must be compacted to the bottom-left corner (min ≈ margin), like the raster.
      var pls = result.UsedSheets.SelectMany(s => s.PartPlacements).ToList();
      pls.Min(p => p.MinX).Should().BeApproximately(0.5, 0.6, "pack anchored to the left edge (margin)");
      pls.Min(p => p.MinY).Should().BeApproximately(0.5, 0.6, "pack anchored to the bottom edge (margin)");

      // Placed ORIGINAL parts must never overlap.
      double overlap = TotalOverlapArea(result.UsedSheets.SelectMany(s => s.PartPlacements).Select(pp => pp.PlacedPart).ToList());
      // Grow each placed part by spacing/2: if the real parts are truly >= spacing apart, the grown
      // versions barely touch (~0 overlap). If spacing were ignored (parts touching), this balloons.
      double gapOverlap = TotalOverlapArea(result.UsedSheets.SelectMany(s => s.PartPlacements)
        .Select(pp => Inflate(pp.PlacedPart, spacing / 2.0)).ToList());

      this.output.WriteLine(FormattableString.Invariant(
        $"SVC: placed={placed} sheets={result.UsedSheets.Count} overlap={overlap:F3} gapOverlap(spacing={spacing})={gapOverlap:F3}"));
      overlap.Should().BeLessThan(sheetW * sheetH * 0.001, "a correctly mapped sparrow layout is overlap-free");
      gapOverlap.Should().BeLessThan(sheetW * sheetH * 0.01, "parts must be kept ~spacing apart, so spacing/2-inflated parts barely touch");
    }

    [Fact]
    public void DilationGrowsRegardlessOfWinding()
    {
      // The real cause of "some parts joined, others not": ClipperOffset shrinks a CW ring on a +delta.
      // A correct dilation must GROW both a CCW and a CW polygon of the same shape. (The repo sample
      // parts happen to be uniformly wound, so this synthetic CW case is what actually guards the fix.)
      var ccw = new NoFitPolygon(new[] { new SvgPoint(0, 0), new SvgPoint(10, 0), new SvgPoint(10, 10), new SvgPoint(0, 10) });
      var cw = new NoFitPolygon(new[] { new SvgPoint(0, 0), new SvgPoint(0, 10), new SvgPoint(10, 10), new SvgPoint(10, 0) });

      AbsArea(Inflate(ccw, 1.0)).Should().BeGreaterThan(AbsArea(ccw), "CCW part must grow");
      AbsArea(Inflate(cw, 1.0)).Should().BeGreaterThan(AbsArea(cw), "CW part must ALSO grow (the bug)");
    }

    private static INfp Inflate(INfp poly, double offset)
    {
      double scale = SvgNest.Config.ClipperScale;
      var path = DeepNestClipper.ScaleUpPath(poly.Points, scale);
      if (Clipper.Area(path) < 0)
      {
        path.Reverse(); // force CCW so a +delta always grows (same as the service)
      }

      var co = new ClipperOffset(4, SvgNest.Config.CurveTolerance * scale);
      co.AddPath(path, JoinType.jtMiter, EndType.etClosedPolygon);
      var sol = new List<List<IntPoint>>();
      co.Execute(ref sol, offset * scale);
      var biggest = sol.OrderByDescending(p => Math.Abs(Clipper.Area(p))).First();
      return new NoFitPolygon(biggest.Select(ip => new SvgPoint(ip.X / scale, ip.Y / scale)));
    }

    private static double AbsArea(INfp p) => Math.Abs(ClipperLib.Clipper.Area(DeepNestClipper.ScaleUpPath(p.Points, SvgNest.Config.ClipperScale))) / (SvgNest.Config.ClipperScale * SvgNest.Config.ClipperScale);

    [Fact]
    public void FreeRotationIsContinuous()
    {
      string exe = Environment.GetEnvironmentVariable("SPARROW_EXE");
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("CONT: SPARROW_EXE not set — skipping.");
        return;
      }

      string dxfDir = FindDxfDir();
      dxfDir.Should().NotBeNull();

      var helper = new NestExecutionHelper();
      double maxExtent = 0;
      var parts = new List<RasterPartInfo>();
      for (int i = 1; i <= 6; i++)
      {
        string path = Path.Combine(dxfDir, $"_{i}.dxf");
        if (File.Exists(path) && helper.LoadRawDetail(new FileInfo(path)) is { } det
            && det.TryConvertToNfp(0, out INfp nfp) && nfp.Points.Length > 2)
        {
          maxExtent = Math.Max(maxExtent, Math.Max(nfp.MaxX - nfp.MinX, nfp.MaxY - nfp.MinY));
          parts.Add(new RasterPartInfo { Path = path, Quantity = 2 });
        }
      }

      parts.Should().NotBeEmpty();
      int side = (int)Math.Ceiling(maxExtent * 3);
      var stock = new List<(int, int, int)> { (side, side, 1) };

      // rotations = 36 == "Free" → the service omits allowed_orientations, so sparrow rotates continuously.
      var result = SparrowNestService.Nest(parts, stock, 36, 1.0, 0.5, 8, exe, out string err);

      result.Should().NotBeNull($"nest must return a result (err={err})");
      var rots = result.UsedSheets.SelectMany(s => s.PartPlacements).Select(p => ((p.Rotation % 360) + 360) % 360).ToList();
      this.output.WriteLine("CONT angles: " + string.Join(", ", rots.Select(r => r.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));

      // Continuous rotation must produce at least one angle that is NOT a multiple of 15° (a discrete
      // "Free" set could only ever land on 0/15/30/…). >1° off a 15° step proves it's continuous.
      rots.Any(r =>
      {
        double m = ((r % 15) + 15) % 15;
        return Math.Min(m, 15 - m) > 1.0;
      }).Should().BeTrue("Free rotation must be continuous (non-15°-step angles present)");

      var polys = result.UsedSheets.SelectMany(s => s.PartPlacements).Select(p => p.PlacedPart).ToList();
      TotalOverlapArea(polys).Should().BeLessThan(side * side * 0.001, "continuous-rotation layout must be overlap-free");
    }

    [Fact]
    public void ZeroAnd90ModeRotatesOnlyByZeroOr90()
    {
      string exe = Environment.GetEnvironmentVariable("SPARROW_EXE");
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("0/90: SPARROW_EXE not set — skipping.");
        return;
      }

      string dxfDir = FindDxfDir();
      dxfDir.Should().NotBeNull();

      var helper = new NestExecutionHelper();
      double maxExtent = 0;
      var parts = new List<RasterPartInfo>();
      for (int i = 1; i <= 6; i++)
      {
        string path = Path.Combine(dxfDir, $"_{i}.dxf");
        if (File.Exists(path) && helper.LoadRawDetail(new FileInfo(path)) is { } det
            && det.TryConvertToNfp(0, out INfp nfp) && nfp.Points.Length > 2)
        {
          maxExtent = Math.Max(maxExtent, Math.Max(nfp.MaxX - nfp.MinX, nfp.MaxY - nfp.MinY));
          parts.Add(new RasterPartInfo { Path = path, Quantity = 2 });
        }
      }

      parts.Should().NotBeEmpty();
      int side = (int)Math.Ceiling(maxExtent * 3);
      var stock = new List<(int, int, int)> { (side, side, 1) };

      // rotations = 1002 == "0°+90°" (RotationCodes.RotZeroAnd90) → the service must emit
      // allowed_orientations=[0,90], so sparrow may only place parts at 0° or 90°.
      var result = SparrowNestService.Nest(parts, stock, 1002, 1.0, 0.5, 8, exe, out string err);

      result.Should().NotBeNull($"nest must return a result (err={err})");
      var rots = result.UsedSheets.SelectMany(s => s.PartPlacements).Select(p => ((p.Rotation % 360) + 360) % 360).ToList();
      rots.Should().NotBeEmpty();
      this.output.WriteLine("0/90 angles: " + string.Join(", ", rots.Select(r => r.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));

      // Every placement must be 0° or 90° (within f32 round-trip tolerance). A 180°/270° here is the bug.
      foreach (double r in rots)
      {
        double toZero = Math.Min(r, 360 - r);
        double to90 = Math.Abs(r - 90);
        Math.Min(toZero, to90).Should().BeLessThan(0.5, $"0°+90° mode must never rotate a part to {r:F1}°");
      }
    }

    [Fact]
    public void MultiSheetFillsSeveralSheets()
    {
      string exe = Environment.GetEnvironmentVariable("SPARROW_EXE");
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("MS: SPARROW_EXE not set — skipping.");
        return;
      }

      string dxfDir = FindDxfDir();
      dxfDir.Should().NotBeNull();
      string one = Path.Combine(dxfDir, "_1.dxf");
      File.Exists(one).Should().BeTrue();

      // Size the sheet from the part's real extent so a few fit per sheet; 12 copies then need several
      // sheets. Stock (8) is more than enough, so all must be placed across multiple sheets, none overlapping.
      var helper = new NestExecutionHelper();
      helper.LoadRawDetail(new FileInfo(one)).TryConvertToNfp(0, out INfp nfp).Should().BeTrue();
      int side = (int)Math.Ceiling(Math.Max(nfp.MaxX - nfp.MinX, nfp.MaxY - nfp.MinY) * 2.3);

      var parts = new List<RasterPartInfo> { new RasterPartInfo { Path = one, Quantity = 12 } };
      var stock = new List<(int, int, int)> { (side, side, 8) };

      var result = SparrowNestService.Nest(parts, stock, 4, 1.0, 0.5, 5, exe, out string err);

      result.Should().NotBeNull($"multi-sheet nest must return a result (err={err})");
      int placed = result.UsedSheets.Sum(s => s.PartPlacements.Count);
      this.output.WriteLine(FormattableString.Invariant(
        $"MS: sheets={result.UsedSheets.Count} placed={placed}/12 unplaced={result.UnplacedParts.Count()}"));

      result.UsedSheets.Count.Should().BeGreaterThan(1, "12 parts don't fit one 90×90 sheet");
      placed.Should().Be(12, "the 8-sheet stock is enough to place all 12");
      const double margin = 0.5;
      foreach (var sheet in result.UsedSheets)
      {
        var pls = sheet.PartPlacements.ToList();
        TotalOverlapArea(pls.Select(p => p.PlacedPart).ToList())
          .Should().BeLessThan(side * side * 0.001, "each sheet must be overlap-free");

        // Every part must stay inside the usable area [margin, side-margin]; parts filling the short
        // axis used to poke past the far edge (strip_height didn't reserve the margins) and get dropped.
        foreach (var p in pls)
        {
          p.MinX.Should().BeGreaterThanOrEqualTo(margin - 0.1);
          p.MinY.Should().BeGreaterThanOrEqualTo(margin - 0.1);
          p.MaxX.Should().BeLessThanOrEqualTo(side - margin + 0.1);
          p.MaxY.Should().BeLessThanOrEqualTo(side - margin + 0.1);
        }
      }
    }

    [Fact]
    public void NestsPartsIntoHoles()
    {
      string exe = Environment.GetEnvironmentVariable("SPARROW_EXE");
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("HOLE: SPARROW_EXE not set — skipping.");
        return;
      }

      string dxfDir = FindDxfDir();
      dxfDir.Should().NotBeNull();
      string frame = Path.Combine(dxfDir, "200x100With160x80Hole.dxf"); // outer 200×100, hole 160×80
      File.Exists(frame).Should().BeTrue();

      // Confirm the importer actually gives the frame a hole — otherwise the test proves nothing.
      var helper = new NestExecutionHelper();
      helper.LoadRawDetail(new FileInfo(frame)).TryConvertToNfp(0, out INfp frameNfp).Should().BeTrue();
      (frameNfp.Children?.Count ?? 0).Should().BeGreaterThan(0, "the frame part must load with a hole");

      var dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
        string square = WriteSquare(dir, 15);
        // Sheet ≈ the frame size → nothing fits OUTSIDE the frame; the only room is the 160×80 HOLE.
        var parts = new List<RasterPartInfo>
        {
          new RasterPartInfo { Path = frame, Quantity = 1 },
          new RasterPartInfo { Path = square, Quantity = 12 },
        };
        var stock = new List<(int, int, int)> { (204, 104, 1) };

        var result = SparrowNestService.Nest(parts, stock, 4, 1.0, 0.5, 6, exe, out string err);

        result.Should().NotBeNull($"nest must return a result (err={err})");
        int squares = result.UsedSheets.SelectMany(s => s.PartPlacements).Count(p => p.Source == 1);
        this.output.WriteLine(FormattableString.Invariant(
          $"HOLE: squares placed inside the frame's hole = {squares}/12"));

        // Without the hole-fill pass these squares could ONLY be unplaced (no room outside the frame).
        squares.Should().BeGreaterThan(0, "small squares must nest into the frame's hole");

        // The hole-nested squares must not overlap EACH OTHER (their outers legitimately sit inside the
        // frame's outer, so only square-vs-square overlap is meaningful).
        var sq = result.UsedSheets.SelectMany(s => s.PartPlacements).Where(p => p.Source == 1).Select(p => p.PlacedPart).ToList();
        TotalOverlapArea(sq).Should().BeLessThan(15.0 * 15 * 0.05, "hole-nested squares must not overlap each other");
      }
      finally
      {
        try
        {
          Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
      }
    }

    [Fact]
    public void HighPriorityPartsAreNestedBeforeLowPriorityWhenMaterialRunsShort()
    {
      string exe = Environment.GetEnvironmentVariable("SPARROW_EXE");
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("PRIO: SPARROW_EXE not set — skipping.");
        return;
      }

      var dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      string lowDir = Path.Combine(dir, "low");
      string highDir = Path.Combine(dir, "high");
      Directory.CreateDirectory(lowDir);
      Directory.CreateDirectory(highDir);
      try
      {
        string low = WriteSquare(lowDir, 15);
        string high = WriteSquare(highDir, 15);

        // LOW-priority part is FIRST in the list (source 0) — so plain declaration order would nest it
        // first. Only a working priority feature flips that. HIGH qty exceeds one sheet, so the batch is
        // pure high-priority and sparrow never even sees the low part until the high demand is met.
        var parts = new List<RasterPartInfo>
        {
          new RasterPartInfo { Path = low, Quantity = 4, Priority = 10 },  // source 0, lowest priority
          new RasterPartInfo { Path = high, Quantity = 8, Priority = 1 },  // source 1, highest priority
        };

        // 34×34 fits only 2×2 = 4 squares (spacing 1, margin 0.5). One sheet of stock → 8 can't all fit,
        // so something must be left off. Priority must make the LOW-priority part the one left off.
        var stock = new List<(int, int, int)> { (34, 34, 1) };

        var result = SparrowNestService.Nest(parts, stock, 4, 1.0, 0.5, 5, exe, out string err);

        result.Should().NotBeNull($"nest must return a result (err={err})");
        var placed = result.UsedSheets.SelectMany(s => s.PartPlacements).ToList();
        int placedHigh = placed.Count(p => p.Source == 1);
        int placedLow = placed.Count(p => p.Source == 0);
        this.output.WriteLine(FormattableString.Invariant(
          $"PRIO: placed high(src1)={placedHigh} low(src0)={placedLow} unplaced={result.UnplacedParts.Count()}"));

        placed.Should().NotBeEmpty("the sheet must hold some parts");
        placedHigh.Should().BeGreaterThan(0, "high-priority parts must be nested");
        placedLow.Should().Be(0, "no low-priority part may be nested while high-priority parts are left off");

        TotalOverlapArea(placed.Select(p => p.PlacedPart).ToList())
          .Should().BeLessThan(34.0 * 34 * 0.001, "the sheet must be overlap-free");
      }
      finally
      {
        try
        {
          Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
      }
    }

    [Fact]
    public void MirroredPartsCarryTheMirrorFlagForExport()
    {
      string exe = Environment.GetEnvironmentVariable("SPARROW_EXE");
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("MIR: SPARROW_EXE not set — skipping.");
        return;
      }

      var dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
        // An asymmetric L — a mirror is a real, different shape (matters for left/right-hand parts).
        string lshape = WritePoly(dir, "L", new[] { (0.0, 0.0), (30.0, 0.0), (30.0, 10.0), (10.0, 10.0), (10.0, 30.0), (0.0, 30.0) });
        var parts = new List<RasterPartInfo>
        {
          new RasterPartInfo { Path = lshape, Quantity = 1, Mirrored = false },
          new RasterPartInfo { Path = lshape, Quantity = 1, Mirrored = true },
        };
        var stock = new List<(int, int, int)> { (120, 80, 1) };

        var result = SparrowNestService.Nest(parts, stock, 4, 0.5, 0.5, 5, exe, out string err);

        result.Should().NotBeNull($"nest must return a result (err={err})");
        var pls = result.UsedSheets.SelectMany(s => s.PartPlacements).ToList();
        pls.Count.Should().Be(2);

        // Exactly one placement is flagged mirrored — that flag is what the DXF exporter uses to reload
        // the ORIGINAL file and mirror it (rendering already bakes the mirror into the geometry).
        pls.Count(p => p.IsMirrored).Should().Be(1, "the mirrored population must carry IsMirrored");
        pls.Count(p => !p.IsMirrored).Should().Be(1);

        // The exporter reloads by Part.Name, so it must still point at the original file.
        pls.Should().OnlyContain(p => p.Part.Name == lshape);
      }
      finally
      {
        try
        {
          Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
      }
    }

    [Fact]
    public void PickBestPrefersMostPlacedThenDensityThenLowestSeed()
    {
      // Most parts placed wins even if a rival is denser.
      SparrowNestService.PickBest(new[] { (Count: 25, Density: 0.93, Seed: 1), (26, 0.90, 2), (24, 0.95, 3) })
        .Should().Be(1, "26 placed beats 25/24 regardless of strip density");

      // Tie on count → higher density wins.
      SparrowNestService.PickBest(new[] { (26, 0.90, 1), (26, 0.92, 2) })
        .Should().Be(1, "same count → the denser candidate (index 1) wins");

      // Tie on count and density → lowest seed wins (stable).
      SparrowNestService.PickBest(new[] { (26, 0.92, 5), (26, 0.92, 2) })
        .Should().Be(1, "full tie → lowest seed (index 1, seed 2)");

      SparrowNestService.PickBest(System.Array.Empty<(int, double, int)>()).Should().Be(-1);
    }

    [Fact]
    public void TriesForReplicasScalesWithCloneCountAndClampsTo16()
    {
      // A batch replicated onto many identical sheets earns more best-of-K tries (amortized over the clones),
      // capped at 16; a one-off sheet (replicas 1) stays at the base. NestTries() is machine-dependent
      // (≤8), so assert bounds/monotonicity rather than an absolute base.
      SparrowNestService.TriesForReplicas(100).Should().Be(16, "very replicated templates cap at 16 tries");
      SparrowNestService.TriesForReplicas(16).Should().Be(16);
      SparrowNestService.TriesForReplicas(10).Should().Be(10, "10 is above any base (≤8) and below the cap");

      int baseTries = SparrowNestService.TriesForReplicas(1);
      baseTries.Should().BeGreaterThanOrEqualTo(2, "best-of never drops below 2");
      baseTries.Should().BeLessThanOrEqualTo(10);
      SparrowNestService.TriesForReplicas(0).Should().Be(baseTries, "replicas below the base clamp up to the base");
      SparrowNestService.TriesForReplicas(5).Should().BeGreaterThanOrEqualTo(baseTries, "tries are monotonic in replicas");
    }

    [Fact]
    public void TriesForGivesTheTailSheetEnoughAttemptsToBeConsistent()
    {
      int baseTries = SparrowNestService.TriesForReplicas(1);

      // A one-off, non-final sheet stays at the base.
      SparrowNestService.TriesFor(1, false).Should().Be(baseTries);

      // The final/tail sheet (replicas 1) is boosted to at least 8 so it lands consistently, not 2-3.
      SparrowNestService.TriesFor(1, true).Should().Be(8, "the single tail sheet is cheap to optimize well");
      SparrowNestService.TriesFor(1, true).Should().BeGreaterThanOrEqualTo(baseTries);

      // A heavily-replicated body stays at the 16 cap whether or not it's final.
      SparrowNestService.TriesFor(30, false).Should().Be(16);
      SparrowNestService.TriesFor(30, true).Should().Be(16);
    }

    [Fact]
    public void Test2StripPacks26InTheChosenOrientationConsistently()
    {
      string exe = Environment.GetEnvironmentVariable("SPARROW_EXE");
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("T2: SPARROW_EXE not set — skipping.");
        return;
      }

      var dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
        // The exact test2.dxf outer contour: a 7×36 strip with chamfered corners. In a 120×60 sheet it packs
        // 26 with common line (spacing 0) + 0/90 (a row of 17 upright + 3×3 laid flat above). The sheet is
        // packed in the CHOSEN orientation — 120 wide × 60 tall — and must NOT be auto-rotated to 60×120.
        string strip = WritePoly(dir, "test2strip",
          new[] { (7.0, 35.0), (7.0, 1.0), (6.0, 0.0), (1.0, 0.0), (0.0, 1.0), (0.0, 35.0), (1.0, 36.0), (6.0, 36.0) });

        // Quantity must exceed the ~1.3-sheet batch cap (~37) so a full sheet is fed a full batch (a pool of
        // only ~30 would hand sparrow the whole pool and pack it loosely). 60 → sheets ≈ 26, 26, 8.
        var parts = new List<RasterPartInfo> { new RasterPartInfo { Path = strip, Quantity = 60 } };
        var stock = new List<(int, int, int)> { (120, 60, 3) };

        // rotations 1002 = "0°+90°", spacing 0 = common line, margin 0 = edge-to-edge.
        (int[] Counts, INestResult Result) Run()
        {
          var r = SparrowNestService.Nest(parts, stock, 1002, 0.0, 0.0, 5, exe, out string err);
          r.Should().NotBeNull($"nest must return a result (err={err})");
          return (r.UsedSheets.Select(s => s.PartPlacements.Count).ToArray(), r);
        }

        var a = Run();
        var b = Run();
        this.output.WriteLine("T2 run A counts: " + string.Join(",", a.Counts));
        this.output.WriteLine("T2 run B counts: " + string.Join(",", b.Counts));

        a.Counts[0].Should().BeGreaterThanOrEqualTo(26, "the full sheet must fit 26 in the chosen 120×60 orientation");

        // The sheet must NOT be auto-rotated: it stays 120 wide × 60 tall (the chosen preset orientation).
        var sheet = a.Result.UsedSheets[0].Sheet;
        (sheet.MaxX - sheet.MinX).Should().BeApproximately(120, 0.01, "the sheet keeps the chosen width (120)");
        (sheet.MaxY - sheet.MinY).Should().BeApproximately(60, 0.01, "the sheet keeps the chosen height (60), not rotated to 120");

        // The full first sheet must be overlap-free.
        var pls = a.Result.UsedSheets[0].PartPlacements.Select(p => p.PlacedPart).ToList();
        TotalOverlapArea(pls).Should().BeLessThan(120.0 * 60 * 0.001, "the packed sheet must be overlap-free");

        // Consistency: the full-sheet count is the same across runs.
        b.Counts[0].Should().Be(a.Counts[0], "the same job must fill the full sheet with the same count each run");
      }
      finally
      {
        try
        {
          Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
      }
    }

    private static string WritePoly(string dir, string name, (double X, double Y)[] pts)
    {
      var file = new IxMilia.Dxf.DxfFile();
      var verts = pts.Concat(new[] { pts[0] }).Select(p => new IxMilia.Dxf.Entities.DxfVertex(new IxMilia.Dxf.DxfPoint(p.X, p.Y, 0)));
      file.Entities.Add(new IxMilia.Dxf.Entities.DxfPolyline(verts) { IsClosed = true });
      string path = Path.Combine(dir, name + ".dxf");
      file.Save(path);
      return path;
    }

    private static string WriteSquare(string dir, double side)
    {
      var file = new IxMilia.Dxf.DxfFile();
      var pts = new[]
      {
        new IxMilia.Dxf.DxfPoint(0, 0, 0), new IxMilia.Dxf.DxfPoint(side, 0, 0),
        new IxMilia.Dxf.DxfPoint(side, side, 0), new IxMilia.Dxf.DxfPoint(0, side, 0), new IxMilia.Dxf.DxfPoint(0, 0, 0),
      };
      file.Entities.Add(new IxMilia.Dxf.Entities.DxfPolyline(pts.Select(p => new IxMilia.Dxf.Entities.DxfVertex(p))) { IsClosed = true });
      string path = Path.Combine(dir, "sq.dxf");
      file.Save(path);
      return path;
    }

    private static double TotalOverlapArea(List<INfp> parts)
    {
      double scale = SvgNest.Config.ClipperScale;
      var polys = parts.Select(p => DeepNestClipper.ScaleUpPath(p.Points, scale)).ToList();

      double total = 0;
      for (int i = 0; i < polys.Count; i++)
      {
        for (int j = i + 1; j < polys.Count; j++)
        {
          var clipper = new Clipper();
          clipper.AddPath(polys[i], PolyType.ptSubject, true);
          clipper.AddPath(polys[j], PolyType.ptClip, true);
          var solution = new List<List<IntPoint>>();
          clipper.Execute(ClipType.ctIntersection, solution, PolyFillType.pftNonZero, PolyFillType.pftNonZero);
          total += solution.Sum(p => Math.Abs(Clipper.Area(p))) / (scale * scale);
        }
      }

      return total;
    }

    private static string FindDxfDir()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null)
      {
        string candidate = Path.Combine(dir.FullName, "DeepNestPort", "dxfs");
        if (Directory.Exists(candidate))
        {
          return candidate;
        }

        dir = dir.Parent;
      }

      return null;
    }
  }
}
