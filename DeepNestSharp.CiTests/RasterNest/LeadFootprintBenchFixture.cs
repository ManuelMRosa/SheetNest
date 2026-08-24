namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestLib.NestProject;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// MEASUREMENT, not a promise: what the exterior lead costs the nest. Set SHEETNEST_BENCH=1 to run.
  /// <para>
  /// A SheetCam .nest describes a part that is already toolpathed, and <see cref="SparrowNestService"/>
  /// folds its leads into the packing footprint, so an outer lead is a blister on the part's silhouette.
  /// Along that side the part no longer presents a straight edge, which is exactly what the seam finder
  /// needs, so the lead is what stops common cutting from closing. This bench puts a number on that
  /// before anything is built to fix it.
  /// </para>
  /// <para>
  /// Three footprints over the same job: every lead (what ships today), interior leads only, and no
  /// leads at all. The middle one is the interesting one — a lead inside a hole cannot reach past the
  /// outline, so it should measure IDENTICAL to no leads at all. Measuring it rather than assuming it is
  /// the point: if it does not, the interior/exterior split below is wrong and phase 2 is built on sand.
  /// </para>
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public class LeadFootprintBenchFixture
  {
    /// <summary>Where SheetCam writes the jobs this bench reads. Override with SHEETNEST_NEST_CORPUS.</summary>
    private const string DefaultCorpus = @"C:\Users\rosam\Documents\SheetCam";

    /// <summary>Gap kept to a neighbour the part may NOT share a cut with, when the job states none.</summary>
    /// <remarks>
    /// A common-line job carries PartSpacing="0", which is a flag and not a measurement, so it says
    /// nothing about the gap to keep where a seam does NOT form. The app leaves its own spacing alone in
    /// that case; the bench has no app, so it states a value and prints it rather than nesting at zero.
    /// </remarks>
    private const double FallbackSpacingIn = 0.25;

    private readonly ITestOutputHelper output;

    public LeadFootprintBenchFixture(ITestOutputHelper output) => this.output = output;

    /// <summary>How the leads of a part are handed to the packer.</summary>
    private enum LeadMode
    {
      /// <summary>Every lead the file carries. What ships today.</summary>
      All,

      /// <summary>Only leads that stay inside the outer contour. The outer lead is freed.</summary>
      InteriorOnly,

      /// <summary>No leads at all. The ceiling: what freeing the lead could reach at best.</summary>
      None,
    }

    [Fact]
    public void MeasureWhatTheExteriorLeadCosts()
    {
      if (Environment.GetEnvironmentVariable("SHEETNEST_BENCH") != "1")
      {
        this.output.WriteLine("BENCH: set SHEETNEST_BENCH=1 to run.");
        return;
      }

      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("BENCH: SPARROW_EXE not set - skipping.");
        return;
      }

      var files = CorpusFiles();
      if (files.Count == 0)
      {
        this.output.WriteLine("BENCH: no .nest files found - skipping.");
        return;
      }

      foreach (string path in files)
      {
        this.MeasureOneJob(path, exe);
      }
    }

    /// <summary>
    /// THE measurement, once the question was put right. Set SHEETNEST_BENCH=1 to run.
    /// <para>
    /// Manuel's correction: parts that share a common line are ONE cut, so the group needs ONE lead in,
    /// not one per part. That is not a preference, and it exposes a mechanical fault. Welded parts sit
    /// one kerf apart (0.34mm on this corpus) while an exterior lead reaches 3.72mm past the outline, so
    /// an INTERIOR part's lead has nowhere to go but into its neighbour. There is no scrap around an
    /// interior part, only more parts. <c>ToolingClearsEveryone</c> catches exactly that and throws the
    /// seam away.
    /// </para>
    /// <para>
    /// So SheetNest reserves N-1 leads the cut does not need, and then abandons seams because of them.
    /// This measures that: seams FORMED and seams GIVEN UP, which is the metric that matches the logic.
    /// Extent and fill, which the earlier benches here report, answer a different question (material
    /// yield) and answered it correctly - freeing the lead wins no parts. This one is about the cut.
    /// </para>
    /// <para>
    /// "One lead for the group" is expressed as two populations of the SAME drawing: one copy carrying
    /// the lead, the rest carrying none. That is the shape the engine already supports (it is how a
    /// mirrored population works) and it models a fully welded sheet, where all but one part is interior.
    /// </para>
    /// </summary>
    [Fact]
    public void MeasureTheSeamsLostToLeadsTheCutDoesNotNeed()
    {
      if (Environment.GetEnvironmentVariable("SHEETNEST_BENCH") != "1")
      {
        this.output.WriteLine("BENCH: set SHEETNEST_BENCH=1 to run.");
        return;
      }

      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("BENCH: SPARROW_EXE not set - skipping.");
        return;
      }

      foreach (string path in CorpusFiles())
      {
        this.MeasureSeamsForOneJob(path, exe);
      }
    }

    /// <summary>
    /// The same question asked so the answer can show. Set SHEETNEST_BENCH=1 to run.
    /// <para>
    /// <see cref="MeasureWhatTheExteriorLeadCosts"/> could not answer it for a common-line job: the job
    /// asks for 26 pieces, all 26 fit in every mode, and a metric that is already at "everything placed"
    /// cannot report an improvement. So here the sheet is deliberately OVERSUBSCRIBED - more pieces are
    /// offered than can possibly fit - and the measure becomes how many landed on one sheet. That is the
    /// number that turns into material either way.
    /// </para>
    /// <para>
    /// Only two modes: <see cref="LeadMode.InteriorOnly"/> measured identical to <see cref="LeadMode.None"/>
    /// on all four jobs of the corpus, so running it again would only cost minutes to reprint a known
    /// result. Only jobs that HAVE an exterior lead are run: the rest have nothing to free.
    /// </para>
    /// </summary>
    [Fact]
    public void MeasureLeadCostOnAnOversubscribedSheet()
    {
      if (Environment.GetEnvironmentVariable("SHEETNEST_BENCH") != "1")
      {
        this.output.WriteLine("BENCH: set SHEETNEST_BENCH=1 to run.");
        return;
      }

      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("BENCH: SPARROW_EXE not set - skipping.");
        return;
      }

      foreach (string path in CorpusFiles())
      {
        this.MeasureOversubscribed(path, exe);
      }
    }

    /// <summary>
    /// Guards the split the bench rests on: a lead that lives inside a hole is invisible to the packer,
    /// because the outer contour already claims that ground. Runs in the normal suite - it is geometry,
    /// not a nest, so it needs no engine.
    /// </summary>
    [Fact]
    public void AnInteriorLeadDoesNotWidenTheFootprint()
    {
      // A 20 x 40 bar with a lead running inside it, nowhere near the outline.
      var bar = new NoFitPolygon(new[]
      {
        new SvgPoint(-10, -20), new SvgPoint(10, -20), new SvgPoint(10, 20), new SvgPoint(-10, 20),
      });

      var interior = new List<IReadOnlyList<SvgPoint>>
      {
        new List<SvgPoint> { new SvgPoint(0, 0), new SvgPoint(0, 5) },
      };

      const double Kerf = 0.34;
      double plain = AbsArea(SparrowNestService.ToolingFootprint(bar, null, Kerf, 0));
      double withInterior = AbsArea(SparrowNestService.ToolingFootprint(bar, interior, Kerf, 0));

      withInterior.Should().BeApproximately(
        plain,
        plain * 1e-6,
        "a lead that never leaves the part cannot make the part any wider, so freeing it would buy nothing");

      // And the control: a lead that DOES reach outside has to grow the footprint, or the bench is
      // measuring a difference that does not exist.
      var exterior = new List<IReadOnlyList<SvgPoint>>
      {
        new List<SvgPoint> { new SvgPoint(-10, 15), new SvgPoint(-14, 15) },
      };

      AbsArea(SparrowNestService.ToolingFootprint(bar, exterior, Kerf, 0)).Should().BeGreaterThan(
        plain * 1.001,
        "an outer lead reaches past the outline and takes material the neighbour cannot have");
    }

    private static double AbsArea(INfp poly)
    {
      double sum = 0;
      var pts = poly.Points;
      for (int i = 0; i < pts.Length; i++)
      {
        var a = pts[i];
        var b = pts[(i + 1) % pts.Length];
        sum += (a.X * b.Y) - (b.X * a.Y);
      }

      return Math.Abs(sum) / 2.0;
    }

    private static List<string> CorpusFiles()
    {
      string dir = Environment.GetEnvironmentVariable("SHEETNEST_NEST_CORPUS");
      if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
      {
        dir = DefaultCorpus;
      }

      return Directory.Exists(dir)
        ? Directory.GetFiles(dir, "*.nest").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
        : new List<string>();
    }

    /// <summary>True when every point of the path lies inside the closed contour.</summary>
    private static bool LiesInside(IReadOnlyList<SvgPoint> path, IReadOnlyList<SvgPoint> contour)
    {
      if (contour == null || contour.Count < 3)
      {
        return false;
      }

      foreach (var p in path)
      {
        if (!PointInPolygon(p, contour))
        {
          return false;
        }
      }

      return true;
    }

    private static bool PointInPolygon(SvgPoint p, IReadOnlyList<SvgPoint> poly)
    {
      bool inside = false;
      for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
      {
        double xi = poly[i].X, yi = poly[i].Y, xj = poly[j].X, yj = poly[j].Y;
        if (((yi > p.Y) != (yj > p.Y)) &&
            (p.X < (((xj - xi) * (p.Y - yi)) / (yj - yi)) + xi))
        {
          inside = !inside;
        }
      }

      return inside;
    }

    /// <summary>The part's leads for one mode, scaled into drawing units.</summary>
    private static IReadOnlyList<IReadOnlyList<SvgPoint>> LeadsFor(SheetCamNestPart part, LeadMode mode, double scale)
    {
      if (mode == LeadMode.None)
      {
        return null;
      }

      var kept = mode == LeadMode.All
        ? part.ToolPaths
        : part.ToolPaths.Where(p => LiesInside(p, part.External)).ToList();

      if (kept.Count == 0)
      {
        return null;
      }

      return kept
        .Select(p => (IReadOnlyList<SvgPoint>)p.Select(v => new SvgPoint(v.X * scale, v.Y * scale)).ToList())
        .ToList();
    }

    /// <summary>
    /// Isolates the MECHANISM, with the job's own geometry taken out of the way. Set SHEETNEST_BENCH=1.
    /// <para>
    /// Both corpus jobs answered "freeing the lead buys nothing", but neither could have answered
    /// otherwise: their pieces are 7 in and 10 in wide against a 120 in sheet, so the count is quantized
    /// by the piece, and a lead sticking out 0.146 in cannot push a 17-column layout to 18. That is a
    /// fact about those jobs, not about the idea.
    /// </para>
    /// <para>
    /// So: one square part, one sheet, and the lead swept from nothing up to half the part. If the count
    /// falls as the lead grows, the mechanism is real and the corpus simply has no lead big enough to
    /// matter. If the count does not move even at half a part, the idea is dead and no plugin can save
    /// it. Either answer is worth having before writing a line of it.
    /// </para>
    /// </summary>
    [Fact]
    public void MeasureHowMuchAnExteriorLeadCostsAsItGrows()
    {
      if (Environment.GetEnvironmentVariable("SHEETNEST_BENCH") != "1")
      {
        this.output.WriteLine("BENCH: set SHEETNEST_BENCH=1 to run.");
        return;
      }

      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("BENCH: SPARROW_EXE not set - skipping.");
        return;
      }

      string dxfDir = FindDxfDir();
      string part = dxfDir == null ? null : Path.Combine(dxfDir, "10x10.dxf");
      if (part == null || !File.Exists(part))
      {
        this.output.WriteLine("BENCH: 10x10.dxf not found - skipping.");
        return;
      }

      var det = new NestExecutionHelper().LoadRawDetail(new FileInfo(part));
      det.Should().NotBeNull();
      det.TryConvertToNfp(0, out INfp nfp).Should().BeTrue();

      double w = nfp.MaxX - nfp.MinX;
      double h = nfp.MaxY - nfp.MinY;
      double midY = (nfp.MinY + nfp.MaxY) / 2.0;

      // EIGHT parts to a side, not five. A coarse grid hides the effect behind quantization: 20 x 20 parts
      // on a 120 sheet hold five columns whatever the lead does, and the count cannot move even while the
      // area it occupies grows 14%. At eight columns of a 10 unit part, a lead of half a unit is enough to
      // cost a whole column, so the count becomes a measurement again.
      int sheetW = (int)Math.Ceiling(w * 8);
      int sheetH = (int)Math.Ceiling(h * 8);
      const int Offer = 80;
      const double Kerf = 0.2;

      this.output.WriteLine(string.Empty);
      this.output.WriteLine($"===== mechanism: {w:F1} x {h:F1} part on a {sheetW} x {sheetH} sheet =====");
      this.output.WriteLine(FormattableString.Invariant(
        $"  offering {Offer}, kerf {Kerf}, common cutting on, lead leaves the LEFT edge at mid height"));
      this.output.WriteLine(string.Format(
        "  {0,-12} {1,-8} {2,9} {3,9} {4,8} {5,7}", "lead", "placed", "extentX", "extentY", "fill", "sec"));

      foreach (double frac in new[] { 0d, 0.05d, 0.10d, 0.20d })
      {
        double lead = w * frac;
        var paths = frac <= 0
          ? null
          : new List<IReadOnlyList<SvgPoint>>
          {
            new List<SvgPoint> { new SvgPoint(nfp.MinX, midY), new SvgPoint(nfp.MinX - lead, midY) },
          };

        var parts = new List<RasterPartInfo>
        {
          new RasterPartInfo
          {
            Path = part,
            Quantity = Offer,
            Rotations = 4,
            Spacing = Kerf,
            Cc = CommonCuttingMode.Unrestricted,
            Kerf = Kerf,
            ToolPaths = paths,
          },
        };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = SparrowNestService.Nest(
          parts,
          new List<(int, int, int)> { (sheetW, sheetH, 1) },
          4,
          Kerf,
          Kerf,
          15,
          exe,
          out string err,
          default,
          null,
          null,
          NestEffort.Normal);
        watch.Stop();

        if (result == null)
        {
          this.output.WriteLine($"  {lead,-12:F2} FAILED: {err}");
          continue;
        }

        var placements = result.UsedSheets.SelectMany(s => s.PartPlacements).ToList();
        var (ex, ey) = SparrowNestService.Extent(placements);
        double placedArea = placements.Sum(p => Math.Abs(p.PlacedPart.Area));
        double fill = ex > 0 && ey > 0 ? placedArea / (ex * ey) : 0;
        double secs = watch.Elapsed.TotalSeconds;

        this.output.WriteLine(FormattableString.Invariant(
          $"  {lead,-12:F2} {placements.Count,-8} {ex,9:F1} {ey,9:F1} {fill,8:P1} {secs,7:F1}"));
      }
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

    /// <summary>Signed shoelace area of a closed contour.</summary>
    private static double SignedArea(IReadOnlyList<SvgPoint> pts)
    {
      double sum = 0;
      for (int i = 0; i < pts.Count; i++)
      {
        var a = pts[i];
        var b = pts[(i + 1) % pts.Count];
        sum += (a.X * b.Y) - (b.X * a.Y);
      }

      return sum / 2.0;
    }

    /// <summary>Material one piece takes: outline less its holes, in drawing units squared.</summary>
    private static double NetArea(SheetCamNestPart part, double scale)
    {
      double area = Math.Abs(SignedArea(part.External));
      foreach (var hole in part.Internals)
      {
        area -= Math.Abs(SignedArea(hole));
      }

      return Math.Max(0, area) * scale * scale;
    }

    private void MeasureOversubscribed(string nestPath, string exe)
    {
      // Bounds the run: every extra piece is engine time, and past a point it measures patience.
      const int MaxPieces = 80;

      SheetCamNestFile nest;
      try
      {
        nest = SheetCamNestFile.Load(nestPath);
      }
      catch (InvalidDataException)
      {
        return;
      }

      int exteriorLeads = nest.Parts.Sum(p => p.ToolPaths.Count(t => !LiesInside(t, p.External)));
      if (exteriorLeads == 0)
      {
        return; // nothing to free
      }

      const double Scale = 1d / 25.4d;
      var written = SheetCamNestPartWriter.WriteAll(nest, nestPath, Scale);
      var sheet = nest.Sheets.FirstOrDefault();
      if (sheet == null || sheet.Width <= 0 || sheet.Height <= 0)
      {
        return;
      }

      int sheetW = (int)Math.Round(sheet.Width * Scale);
      int sheetH = (int)Math.Round(sheet.Height * Scale);
      double sheetArea = (double)sheetW * sheetH;

      double kerf = nest.DeriveKerfMm() * Scale;
      double settings = nest.SettingsToDrawingUnits(false);
      var cc = nest.PartSpacing * settings <= 0 ? CommonCuttingMode.Unrestricted : CommonCuttingMode.None;
      double spacing = nest.PartSpacing * settings > 0 ? nest.PartSpacing * settings : FallbackSpacingIn;
      double margin = nest.EdgeSpacing * settings;

      // Offer half again as much as the sheet could hold even at a perfect fill, so the engine has to
      // leave some behind and the count becomes a measurement instead of a foregone conclusion.
      double partArea = written.Sum(w => NetArea(w.Part, Scale) * w.Part.Quantity) / Math.Max(1, written.Sum(w => w.Part.Quantity));
      int wanted = partArea > 0 ? (int)Math.Ceiling(sheetArea / partArea * 1.5) : 0;
      wanted = Math.Min(MaxPieces, Math.Max(nest.Parts.Sum(p => p.Quantity) + 4, wanted));

      this.output.WriteLine(string.Empty);
      this.output.WriteLine($"===== {Path.GetFileName(nestPath)} (oversubscribed) =====");
      this.output.WriteLine(FormattableString.Invariant(
        $"  sheet {sheetW}x{sheetH} in | offering {wanted} piece(s) of {partArea:F1} in2 each | cc {cc} | kerf {kerf:F4} in"));
      this.output.WriteLine(string.Format(
        "  {0,-14} {1,-7} {2,9} {3,9} {4,8} {5,7} {6,7} {7,7}",
        "leads", "placed", "extentX", "extentY", "fill", "seams", "givenup", "sec"));

      var placedBy = new Dictionary<LeadMode, int>();
      foreach (var mode in new[] { LeadMode.All, LeadMode.None })
      {
        int share = (int)Math.Ceiling(wanted / (double)Math.Max(1, written.Count));
        var parts = written.Select(w => new RasterPartInfo
        {
          Path = w.DxfPath,
          Quantity = share,
          Rotations = 4,
          Spacing = spacing,
          Cc = cc,
          Kerf = kerf,
          ToolPaths = LeadsFor(w.Part, mode, Scale),
        }).ToList();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = SparrowNestService.Nest(
          parts,
          new List<(int, int, int)> { (sheetW, sheetH, 1) },
          4,
          spacing,
          margin,
          15,
          exe,
          out string err,
          default,
          null,
          null,
          NestEffort.Normal);
        watch.Stop();

        if (result == null)
        {
          this.output.WriteLine($"  {mode,-14} FAILED: {err}");
          continue;
        }

        var placements = result.UsedSheets.SelectMany(s => s.PartPlacements).ToList();
        var (ex, ey) = SparrowNestService.Extent(placements);
        double placedArea = placements.Sum(p => Math.Abs(p.PlacedPart.Area));
        double fill = ex > 0 && ey > 0 ? placedArea / (ex * ey) : 0;
        placedBy[mode] = placements.Count;

        int snapped = SparrowNestService.DiagSeamsSnapped;
        int givenUp = SparrowNestService.DiagSeamsGivenUp;
        double secs = watch.Elapsed.TotalSeconds;
        this.output.WriteLine(FormattableString.Invariant(
          $"  {mode,-14} {placements.Count,-7} {ex,9:F1} {ey,9:F1} {fill,8:P1} {snapped,7} {givenUp,7} {secs,7:F1}"));
      }

      if (placedBy.Count == 2)
      {
        int all = placedBy[LeadMode.All];
        int none = placedBy[LeadMode.None];
        this.output.WriteLine(
          all >= wanted && none >= wanted
            ? "  INCONCLUSIVE: both modes placed everything offered, so the count saturated again."
            : FormattableString.Invariant($"  freeing the exterior lead: {none - all:+#;-#;0} piece(s) on one sheet"));
      }
    }

    /// <summary>
    /// Seams formed and abandoned for one job, under three lead models. Skips jobs that cannot answer:
    /// a job with no common cutting has no seams to win, and one with no exterior lead has nothing to
    /// free. Saying so is the point - a silent skip reads as "no gain".
    /// </summary>
    /// <summary>
    /// Why NO seam forms at all, which is the thing to settle before building anything. Set
    /// SHEETNEST_BENCH=1 to run.
    /// <para>
    /// The shop's own common-line job produces zero shared cuts in every lead model, with one group found
    /// and then abandoned. Two mechanisms could do that and they have to be told apart:
    /// </para>
    /// <para>
    /// 1. THE LEAD HOLDS THEM APART. Compaction closes sharing pairs until their FOOTPRINTS touch
    /// (RasterCompact.CommonLineGap = 0), and the footprint swallows the lead. The lead reaches 0.146in
    /// past the outline against a 0.0134in kerf, so what meets is lead against neighbour and the outlines
    /// stop 0.146in apart. The seam finder only accepts a gap inside [0.4, 1.6] kerfs, so 0.146in is ten
    /// times too far and not one candidate is even generated. This should keep "one per part" at zero no
    /// matter what else is swept, which makes it the control row.
    /// </para>
    /// <para>
    /// 2. THE ALIGNMENT SLIDE IS UNBOUNDED. MaxSnapTravelKerfs ships as PositiveInfinity, so lining two
    /// faces up end to end may slide a part as far as it likes. On a sheet packed to 96% that runs into
    /// another neighbour, and the whole group is reverted rather than the one pair. This one is only a
    /// hypothesis, which is what this sweep is for.
    /// </para>
    /// <para>
    /// No production code is touched: Nest already takes the tolerances as a parameter.
    /// </para>
    /// </summary>
    [Fact]
    public void SweepTheSnapTravelLimitToSeeWhatIsKillingTheSeams()
    {
      if (Environment.GetEnvironmentVariable("SHEETNEST_BENCH") != "1")
      {
        this.output.WriteLine("BENCH: set SHEETNEST_BENCH=1 to run.");
        return;
      }

      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("BENCH: SPARROW_EXE not set - skipping.");
        return;
      }

      foreach (string path in CorpusFiles())
      {
        this.SweepOneJob(path, exe);
      }
    }

    private void SweepOneJob(string nestPath, string exe)
    {
      SheetCamNestFile nest;
      try
      {
        nest = SheetCamNestFile.Load(nestPath);
      }
      catch (InvalidDataException)
      {
        return;
      }

      const double Scale = 1d / 25.4d;
      double settings = nest.SettingsToDrawingUnits(false);
      if (nest.PartSpacing * settings > 0)
      {
        return; // common cutting is off on this job: nothing to sweep
      }

      int exteriorLeads = nest.Parts.Sum(p => p.ToolPaths.Count(t => !LiesInside(t, p.External)));
      var written = SheetCamNestPartWriter.WriteAll(nest, nestPath, Scale);
      var sheet = nest.Sheets.FirstOrDefault();
      if (sheet == null || sheet.Width <= 0 || sheet.Height <= 0)
      {
        return;
      }

      int sheetW = (int)Math.Round(sheet.Width * Scale);
      int sheetH = (int)Math.Round(sheet.Height * Scale);
      double kerf = nest.DeriveKerfMm() * Scale;
      double margin = nest.EdgeSpacing * settings;
      var defaults = CommonCuttingTolerances.Default;

      this.output.WriteLine(string.Empty);
      this.output.WriteLine($"===== {Path.GetFileName(nestPath)} : snap travel sweep =====");
      this.output.WriteLine(FormattableString.Invariant(
        $"  kerf {kerf:F5} in | {exteriorLeads} exterior lead(s) | seam gap window [{defaults.GapMinKerfs * kerf:F5}, {defaults.GapMaxKerfs * kerf:F5}] in"));
      this.output.WriteLine(string.Format(
        "  {0,-16} {1,-14} {2,-7} {3,7} {4,8} {5,7}", "lead model", "maxTravel", "placed", "seams", "givenup", "sec"));

      foreach (string model in new[] { "one per part", "one per group" })
      {
        foreach (double travel in new[] { double.PositiveInfinity, 8d, 4d, 2d, 1d })
        {
          var parts = new List<RasterPartInfo>();
          foreach (var w in written)
          {
            var withLead = LeadsFor(w.Part, LeadMode.All, Scale);
            var interior = LeadsFor(w.Part, LeadMode.InteriorOnly, Scale);
            if (model == "one per part")
            {
              parts.Add(Population(w, w.Part.Quantity, withLead, FallbackSpacingIn, CommonCuttingMode.Unrestricted, kerf));
            }
            else
            {
              parts.Add(Population(w, 1, withLead, FallbackSpacingIn, CommonCuttingMode.Unrestricted, kerf));
              if (w.Part.Quantity > 1)
              {
                parts.Add(Population(w, w.Part.Quantity - 1, interior, FallbackSpacingIn, CommonCuttingMode.Unrestricted, kerf));
              }
            }
          }

          var tol = new CommonCuttingTolerances { MaxSnapTravelKerfs = travel };
          var watch = System.Diagnostics.Stopwatch.StartNew();
          var result = SparrowNestService.Nest(
            parts,
            new List<(int, int, int)> { (sheetW, sheetH, 1) },
            4,
            FallbackSpacingIn,
            margin,
            15,
            exe,
            out string err,
            default,
            null,
            tol,
            NestEffort.Normal);
          watch.Stop();

          string travelText = double.IsPositiveInfinity(travel) ? "infinite" : travel.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " kerfs";
          if (result == null)
          {
            this.output.WriteLine($"  {model,-16} {travelText,-14} FAILED: {err}");
            continue;
          }

          int placed = result.UsedSheets.Sum(s => s.PartPlacements.Count);
          int snapped = SparrowNestService.DiagSeamsSnapped;
          int givenUp = SparrowNestService.DiagSeamsGivenUp;
          double secs = watch.Elapsed.TotalSeconds;
          this.output.WriteLine(FormattableString.Invariant(
            $"  {model,-16} {travelText,-14} {placed,-7} {snapped,7} {givenUp,8} {secs,7:F1}"));
        }
      }

      this.output.WriteLine("  (one per part is the CONTROL: mechanism 1 should hold it at zero seams whatever the travel does)");
    }

    private void MeasureSeamsForOneJob(string nestPath, string exe)
    {
      SheetCamNestFile nest;
      try
      {
        nest = SheetCamNestFile.Load(nestPath);
      }
      catch (InvalidDataException)
      {
        return;
      }

      const double Scale = 1d / 25.4d;
      double settings = nest.SettingsToDrawingUnits(false);
      var cc = nest.PartSpacing * settings <= 0 ? CommonCuttingMode.Unrestricted : CommonCuttingMode.None;
      int exteriorLeads = nest.Parts.Sum(p => p.ToolPaths.Count(t => !LiesInside(t, p.External)));

      this.output.WriteLine(string.Empty);
      this.output.WriteLine($"===== {Path.GetFileName(nestPath)} : seams =====");
      if (cc == CommonCuttingMode.None)
      {
        this.output.WriteLine("  SKIPPED: the job states a part spacing, so common cutting is off and there are no seams to win.");
        return;
      }

      if (exteriorLeads == 0)
      {
        this.output.WriteLine("  SKIPPED: no lead reaches past an outline, so there is no phantom lead to remove.");
        return;
      }

      var written = SheetCamNestPartWriter.WriteAll(nest, nestPath, Scale);
      var sheet = nest.Sheets.FirstOrDefault();
      if (sheet == null || sheet.Width <= 0 || sheet.Height <= 0)
      {
        this.output.WriteLine("  SKIPPED: no usable sheet.");
        return;
      }

      int sheetW = (int)Math.Round(sheet.Width * Scale);
      int sheetH = (int)Math.Round(sheet.Height * Scale);
      double kerf = nest.DeriveKerfMm() * Scale;
      double spacing = FallbackSpacingIn;
      double margin = nest.EdgeSpacing * settings;

      this.output.WriteLine(FormattableString.Invariant(
        $"  sheet {sheetW}x{sheetH} in | kerf {kerf:F4} in | {exteriorLeads} exterior lead(s) | spacing {spacing:F4} in (job states none: it is a common-line flag)"));
      this.output.WriteLine(string.Format(
        "  {0,-22} {1,-7} {2,7} {3,8} {4,7}", "lead model", "placed", "seams", "givenup", "sec"));

      var seamsBy = new Dictionary<string, int>();
      foreach (var model in new[] { "one per part", "one per group", "none at all" })
      {
        var parts = new List<RasterPartInfo>();
        foreach (var w in written)
        {
          var withLead = LeadsFor(w.Part, LeadMode.All, Scale);
          var interior = LeadsFor(w.Part, LeadMode.InteriorOnly, Scale);

          if (model == "one per part")
          {
            parts.Add(Population(w, w.Part.Quantity, withLead, spacing, cc, kerf));
          }
          else if (model == "none at all")
          {
            parts.Add(Population(w, w.Part.Quantity, null, spacing, cc, kerf));
          }
          else
          {
            // One copy keeps the lead the cut actually needs; the rest are interior to the weld and
            // keep only the leads that never leave the part.
            parts.Add(Population(w, 1, withLead, spacing, cc, kerf));
            if (w.Part.Quantity > 1)
            {
              parts.Add(Population(w, w.Part.Quantity - 1, interior, spacing, cc, kerf));
            }
          }
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = SparrowNestService.Nest(
          parts,
          new List<(int, int, int)> { (sheetW, sheetH, 1) },
          4,
          spacing,
          margin,
          15,
          exe,
          out string err,
          default,
          null,
          null,
          NestEffort.Normal);
        watch.Stop();

        if (result == null)
        {
          this.output.WriteLine($"  {model,-22} FAILED: {err}");
          continue;
        }

        int placed = result.UsedSheets.Sum(s => s.PartPlacements.Count);
        int snapped = SparrowNestService.DiagSeamsSnapped;
        int givenUp = SparrowNestService.DiagSeamsGivenUp;
        double secs = watch.Elapsed.TotalSeconds;
        seamsBy[model] = snapped;

        this.output.WriteLine(FormattableString.Invariant(
          $"  {model,-22} {placed,-7} {snapped,7} {givenUp,8} {secs,7:F1}"));
      }

      if (seamsBy.TryGetValue("one per part", out int perPart) && seamsBy.TryGetValue("one per group", out int perGroup))
      {
        this.output.WriteLine(
          perGroup == perPart
            ? "  NO CHANGE: the same seams form either way, so on this job the leads were not what blocked them."
            : FormattableString.Invariant($"  one lead per group wins {perGroup - perPart:+#;-#;0} seam(s) against one per part"));
      }
    }

    /// <summary>One population of a drawing: same file, its own copy count and its own tooling.</summary>
    private static RasterPartInfo Population(
      (SheetCamNestPart Part, string DxfPath) w,
      int quantity,
      IReadOnlyList<IReadOnlyList<SvgPoint>> toolPaths,
      double spacing,
      CommonCuttingMode cc,
      double kerf)
      => new RasterPartInfo
      {
        Path = w.DxfPath,
        Quantity = quantity,
        Rotations = 4,
        Spacing = spacing,
        Cc = cc,
        Kerf = kerf,
        ToolPaths = toolPaths,
      };

    private void MeasureOneJob(string nestPath, string exe)
    {
      SheetCamNestFile nest;
      try
      {
        nest = SheetCamNestFile.Load(nestPath);
      }
      catch (InvalidDataException ex)
      {
        this.output.WriteLine($"{Path.GetFileName(nestPath)}: not readable ({ex.Message})");
        return;
      }

      // Drawing units are inches here, and the file's geometry is always millimetres.
      const double Scale = 1d / 25.4d;
      var written = SheetCamNestPartWriter.WriteAll(nest, nestPath, Scale);

      double kerf = nest.DeriveKerfMm() * Scale;
      var cc = nest.PartSpacing * nest.SettingsToDrawingUnits(false) <= 0
        ? CommonCuttingMode.Unrestricted
        : CommonCuttingMode.None;

      double spacing = nest.PartSpacing * nest.SettingsToDrawingUnits(false);
      bool spacingStated = spacing > 0;
      if (!spacingStated)
      {
        spacing = FallbackSpacingIn;
      }

      double margin = nest.EdgeSpacing * nest.SettingsToDrawingUnits(false);
      var sheet = nest.Sheets.FirstOrDefault();
      if (sheet == null || sheet.Width <= 0 || sheet.Height <= 0)
      {
        this.output.WriteLine($"{Path.GetFileName(nestPath)}: no usable sheet - skipping.");
        return;
      }

      int sheetW = (int)Math.Round(sheet.Width * Scale);
      int sheetH = (int)Math.Round(sheet.Height * Scale);
      int total = nest.Parts.Sum(p => p.Quantity);

      int exteriorLeads = nest.Parts.Sum(p => p.ToolPaths.Count(t => !LiesInside(t, p.External)));
      int interiorLeads = nest.Parts.Sum(p => p.ToolPaths.Count) - exteriorLeads;

      this.output.WriteLine(string.Empty);
      this.output.WriteLine($"===== {Path.GetFileName(nestPath)} =====");
      string spacingNote = spacingStated ? "stated" : "fallback, job states none";
      this.output.WriteLine(FormattableString.Invariant(
        $"  sheet {sheetW}x{sheetH} in | {total} piece(s) | kerf {kerf:F4} in | cc {cc} | spacing {spacing:F4} in ({spacingNote}) | margin {margin:F4} in"));
      this.output.WriteLine(FormattableString.Invariant(
        $"  leads: {exteriorLeads} exterior (these are the ones that block a seam), {interiorLeads} interior"));

      if (exteriorLeads == 0)
      {
        this.output.WriteLine("  nothing to free: no lead reaches past an outline, so every mode must measure the same.");
      }

      this.output.WriteLine(string.Format(
        "  {0,-14} {1,-7} {2,-7} {3,9} {4,9} {5,8} {6,7} {7,7} {8,7}",
        "leads", "placed", "sheets", "extentX", "extentY", "fill", "seams", "givenup", "sec"));
      this.output.WriteLine(
        "  (seams/givenup are the KEPT sheet's own, since the counter was fixed. This bench is about"
        + " material yield though, so judge it on placed/extent/fill; seams are measured properly in"
        + " MeasureTheSeamsLostToLeadsTheCutDoesNotNeed.)");

      foreach (var mode in new[] { LeadMode.All, LeadMode.InteriorOnly, LeadMode.None })
      {
        var parts = written.Select(w => new RasterPartInfo
        {
          Path = w.DxfPath,
          Quantity = w.Part.Quantity,
          Rotations = 4,
          Spacing = spacing,
          Cc = cc,
          Kerf = kerf,
          ToolPaths = LeadsFor(w.Part, mode, Scale),
        }).ToList();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = SparrowNestService.Nest(
          parts,
          new List<(int, int, int)> { (sheetW, sheetH, Math.Max(1, sheet.Quantity)) },
          4,
          spacing,
          margin,
          15,
          exe,
          out string err,
          default,
          null,
          null,
          NestEffort.Normal);
        watch.Stop();

        if (result == null)
        {
          this.output.WriteLine($"  {mode,-14} FAILED: {err}");
          continue;
        }

        var placements = result.UsedSheets.SelectMany(s => s.PartPlacements).ToList();
        var (ex, ey) = SparrowNestService.Extent(placements);
        double placedArea = placements.Sum(p => Math.Abs(p.PlacedPart.Area));
        double fill = ex > 0 && ey > 0 ? placedArea / (ex * ey) : 0;

        int snapped = SparrowNestService.DiagSeamsSnapped;
        int givenUp = SparrowNestService.DiagSeamsGivenUp;
        double secs = watch.Elapsed.TotalSeconds;
        this.output.WriteLine(FormattableString.Invariant(
          $"  {mode,-14} {placements.Count,-7} {result.UsedSheets.Count,-7} {ex,9:F1} {ey,9:F1} {fill,8:P1} {snapped,7} {givenUp,7} {secs,7:F1}"));
      }

      this.output.WriteLine($"  ({total} piece(s) wanted)");
    }
  }
}
