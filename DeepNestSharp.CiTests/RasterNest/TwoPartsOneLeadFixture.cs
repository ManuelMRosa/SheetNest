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
  /// Manuel's way of getting ONE lead in on a welded chain, through the only door SheetCam leaves open.
  /// <para>
  /// A .nest hands SheetCam nothing but positions and quantities, so we cannot ask it to drop a lead.
  /// What we CAN do is have the job carry the same drawing TWICE under two names: one part set up with a
  /// lead in, the other set up without. SheetCam then puts a lead only where the operator said to, and
  /// the .nest tells us which is which for free, because it exports the leads per part.
  /// </para>
  /// <para>
  /// They stay TWO parts and are not merged: the write-back has to return positions under each name that
  /// SheetCam asked for, and their footprints genuinely differ, which is the whole point. What they have
  /// to share is the SHAPE, so they weld to each other.
  /// </para>
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public class TwoPartsOneLeadFixture
  {
    private readonly ITestOutputHelper output;

    public TwoPartsOneLeadFixture(ITestOutputHelper output) => this.output = output;

    /// <summary>
    /// Does the two-part setup already weld with no new code? Unrestricted is what a PartSpacing="0" job
    /// imports as, and MayShare says yes to any Unrestricted pair whatever their ShareKey, so it should.
    /// Worth proving before building anything.
    /// </summary>
    [Fact]
    public void TwoNamesOneShapeWeldIntoOneChainWhenSharingIsUnrestricted()
    {
      var m = this.Nest(CommonCuttingMode.Unrestricted);
      if (m == null)
      {
        return;
      }

      this.output.WriteLine($"UNRESTRICTED: placed={m.Value.Placed} seams={m.Value.Seams} (max {m.Value.Placed - 1})");
      m.Value.Seams.Should().Be(
        m.Value.Placed - 1,
        "the leaded copy and the leadless ones are the same shape, so the sheet should weld into one chain");
    }

    /// <summary>
    /// THE trap, and the count is the least of it. Same part mode keys sharing on ShareKey, which is
    /// (path, mirrored), and two SheetCam part names are two different files. So the leadless copies
    /// weld happily to each other - they share a file - and the ONE leaded copy can weld to nothing.
    /// </summary>
    /// <remarks>
    /// Measured 22 seams of a possible 25, so it does not collapse to zero and a seam count alone would
    /// make this look like a rounding error. What it actually leaves is TWO chains: one of a single part
    /// that has the lead in, and one of twenty-five that has NO lead in at all. That second chain cannot
    /// be cut. Which is why the fix is to give the pair one share key, and why a sheet has to be able to
    /// say whether every chain on it has an entry.
    /// </remarks>
    [Fact]
    public void SamePartModeStrandsTheLeadedCopyInItsOwnChain()
    {
      var m = this.Nest(CommonCuttingMode.SamePart);
      if (m == null)
      {
        return;
      }

      this.output.WriteLine($"SAMEPART: placed={m.Value.Placed} seams={m.Value.Seams} (max {m.Value.Placed - 1})");
      m.Value.Seams.Should().BeLessThan(
        m.Value.Placed - 1,
        "the leaded copy is a different file, so Same part will not weld it to the leadless ones and the "
        + "sheet cannot come out as a single chain");
    }

    /// <summary>
    /// The fix: naming both members of the pair to one share group makes Same part see one drawing, so
    /// the leaded copy welds into the chain instead of being stranded with the entry nobody else can use.
    /// </summary>
    [Fact]
    public void OneShareGroupLetsSamePartWeldThePairIntoOneChain()
    {
      var m = this.Nest(CommonCuttingMode.SamePart, shareGroup: "the-same-drawing");
      if (m == null)
      {
        return;
      }

      this.output.WriteLine($"SHAREGROUP: placed={m.Value.Placed} seams={m.Value.Seams} (max {m.Value.Placed - 1})");
      m.Value.Seams.Should().Be(
        m.Value.Placed - 1,
        "told they are the same drawing, Same part welds the leaded copy to the leadless ones and the "
        + "sheet comes out as one chain with exactly one entry on it");
    }

    /// <summary>
    /// The safety net. A chain welded out of leadless copies alone has no way in and cannot be run, and
    /// the layout looks perfect either way - which is exactly how it would reach the machine. Naming the
    /// drawings is the point: the fix is in their SheetCam setup and nowhere else.
    /// </summary>
    [Fact]
    public void AChainWithNoLeadInAnywhereIsReported()
    {
      var m = this.Nest(CommonCuttingMode.Unrestricted, leadedCopies: 0);
      if (m == null)
      {
        return;
      }

      string[] noEntry;
      lock (SparrowNestService.DiagNoEntry)
      {
        noEntry = SparrowNestService.DiagNoEntry.ToArray();
      }

      this.output.WriteLine($"NOENTRY: placed={m.Value.Placed} seams={m.Value.Seams} reported=[{string.Join(", ", noEntry)}]");
      noEntry.Should().NotBeEmpty(
        "every copy on this sheet was set up without a lead-in, so the welded chain has no way into it "
        + "and the operator has to be told before it reaches the machine");
    }

    /// <summary>And the control: the same sheet WITH its one leaded copy says nothing.</summary>
    [Fact]
    public void AChainThatHasItsOneLeadInIsNotReported()
    {
      var m = this.Nest(CommonCuttingMode.Unrestricted);
      if (m == null)
      {
        return;
      }

      string[] noEntry;
      lock (SparrowNestService.DiagNoEntry)
      {
        noEntry = SparrowNestService.DiagNoEntry.ToArray();
      }

      this.output.WriteLine($"HASENTRY: placed={m.Value.Placed} seams={m.Value.Seams} reported=[{string.Join(", ", noEntry)}]");
      noEntry.Should().BeEmpty("one lead-in on the chain is all the cut needs, so there is nothing to warn about");
    }

    /// <summary>Nests the shop's job as TWO parts: one leaded copy and the rest leadless.</summary>
    private (int Placed, int Seams, int GivenUp)? Nest(CommonCuttingMode cc, string shareGroup = null, int leadedCopies = 1)
    {
      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("PAIR: SPARROW_EXE not set - skipping.");
        return null;
      }

      string nestPath = CommonLineJob();
      if (nestPath == null)
      {
        this.output.WriteLine("PAIR: no common-line .nest in the corpus - skipping.");
        return null;
      }

      const double Scale = 1d / 25.4d;
      var nest = SheetCamNestFile.Load(nestPath);
      var written = SheetCamNestPartWriter.WriteAll(nest, nestPath, Scale).First();
      var part = written.Part;
      double kerf = nest.DeriveKerfMm() * Scale;
      var sheet = nest.Sheets.First();

      // The second SheetCam part is the SAME drawing under another name, so it is another file on disk.
      // That is what makes it a different ShareKey, which is the whole point of the Same part case.
      string leadless = Path.Combine(
        Path.GetDirectoryName(written.DxfPath),
        Path.GetFileNameWithoutExtension(written.DxfPath) + "-nolead.dxf");
      File.Copy(written.DxfPath, leadless, true);

      IReadOnlyList<IReadOnlyList<SvgPoint>> Scaled(IEnumerable<IReadOnlyList<SvgPoint>> paths)
        => paths.Select(p => (IReadOnlyList<SvgPoint>)p.Select(v => new SvgPoint(v.X * Scale, v.Y * Scale)).ToList()).ToList();

      var withLead = Scaled(part.ToolPaths);
      var interiorOnly = Scaled(part.ToolPaths.Where(t => LiesInside(t, part.External)));

      var parts = new List<RasterPartInfo>();
      if (leadedCopies > 0)
      {
        parts.Add(new RasterPartInfo
        {
          Path = written.DxfPath, Quantity = leadedCopies, Rotations = 4, Spacing = 0.25,
          Cc = cc, CommonCuttingDomain = "sheetcam:" + nestPath, ValidateEntryLead = true,
          Kerf = kerf, ToolPaths = withLead, ShareGroup = shareGroup,
        });
      }

      parts.Add(new RasterPartInfo
      {
        Path = leadless, Quantity = Math.Max(1, part.Quantity - leadedCopies), Rotations = 4, Spacing = 0.25,
        Cc = cc, CommonCuttingDomain = "sheetcam:" + nestPath, ValidateEntryLead = true,
        Kerf = kerf, ToolPaths = interiorOnly.Count == 0 ? null : interiorOnly,
        ShareGroup = shareGroup,
      });

      var result = SparrowNestService.Nest(
        parts,
        new List<(int, int, int)> { ((int)Math.Round(sheet.Width * Scale), (int)Math.Round(sheet.Height * Scale), 1) },
        4,
        0.25,
        nest.EdgeSpacing * nest.SettingsToDrawingUnits(false),
        15,
        exe,
        out string err,
        default,
        null,
        null,
        NestEffort.Normal);

      result.Should().NotBeNull($"nest must return a result (err={err})");
      return (
        result.UsedSheets.Sum(s => s.PartPlacements.Count),
        SparrowNestService.DiagSeamsSnapped,
        SparrowNestService.DiagSeamsGivenUp);
    }

    private static bool LiesInside(IReadOnlyList<SvgPoint> path, IReadOnlyList<SvgPoint> contour)
    {
      if (contour == null || contour.Count < 3)
      {
        return false;
      }

      foreach (var p in path)
      {
        bool inside = false;
        for (int i = 0, j = contour.Count - 1; i < contour.Count; j = i++)
        {
          double xi = contour[i].X, yi = contour[i].Y, xj = contour[j].X, yj = contour[j].Y;
          if (((yi > p.Y) != (yj > p.Y)) && (p.X < (((xj - xi) * (p.Y - yi)) / (yj - yi)) + xi))
          {
            inside = !inside;
          }
        }

        if (!inside)
        {
          return false;
        }
      }

      return true;
    }

    private static string CommonLineJob()
    {
      string dir = Environment.GetEnvironmentVariable("SHEETNEST_NEST_CORPUS");
      if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
      {
        dir = @"C:\Users\rosam\Documents\SheetCam";
      }

      if (!Directory.Exists(dir))
      {
        return null;
      }

      foreach (string path in Directory.GetFiles(dir, "*.nest").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
      {
        try
        {
          var nest = SheetCamNestFile.Load(path);
          if (nest.PartSpacing * nest.SettingsToDrawingUnits(false) <= 0
              && nest.Parts.Count == 1
              && nest.Parts[0].Quantity > 2)
          {
            return path;
          }
        }
        catch (InvalidDataException)
        {
        }
      }

      return null;
    }
  }
}
