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
  /// THE test that was missing while common-line cutting quietly produced nothing.
  /// <para>
  /// The shop's own common-line job came out of a full nest with ZERO shared cuts, and no test noticed
  /// because every common-line test here builds two or three parts by hand and checks the snap in
  /// isolation, where it works perfectly. The defect only appears on a packed sheet: lining two faces up
  /// slides a part ALONG the seam, the slide was unbounded, on a full sheet it hit some other neighbour,
  /// and since a group is applied all-or-nothing the entire chain was reverted. Isolated pairs never
  /// have a third part to collide with, so they never saw it.
  /// </para>
  /// <para>
  /// So this one nests a REAL sheet and asserts the thing the operator actually wants: the parts end up
  /// welded into one chain. It needs the engine and the shop's own job file, and says so when it cannot
  /// run rather than passing quietly.
  /// </para>
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public class CommonLineActuallyWeldsTheSheetFixture
  {
    private readonly ITestOutputHelper output;

    public CommonLineActuallyWeldsTheSheetFixture(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void AFullCommonLineSheetWeldsIntoOneChain()
    {
      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("WELD: SPARROW_EXE not set - skipping.");
        return;
      }

      string nestPath = CommonLineJob();
      if (nestPath == null)
      {
        this.output.WriteLine("WELD: no common-line .nest in the corpus - skipping.");
        return;
      }

      const double Scale = 1d / 25.4d;
      var nest = SheetCamNestFile.Load(nestPath);
      var written = SheetCamNestPartWriter.WriteAll(nest, nestPath, Scale);
      var sheet = nest.Sheets.First();
      double kerf = nest.DeriveKerfMm() * Scale;

      var parts = written.Select(w => new RasterPartInfo
      {
        Path = w.DxfPath,
        Quantity = w.Part.Quantity,
        Rotations = 4,
        Spacing = 0.25,
        Cc = CommonCuttingMode.Unrestricted,
        CommonCuttingDomain = "sheetcam:" + nestPath,
        ValidateEntryLead = true,
        Kerf = kerf,
        ToolPaths = w.Part.ToolPaths
          .Select(p => (IReadOnlyList<SvgPoint>)p.Select(v => new SvgPoint(v.X * Scale, v.Y * Scale)).ToList())
          .ToList(),
      }).ToList();

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
        null, // the SHIPPED tolerances: that is what is on trial here
        NestEffort.Normal);

      result.Should().NotBeNull($"nest must return a result (err={err})");

      int placed = result.UsedSheets.Sum(s => s.PartPlacements.Count);
      int seams = SparrowNestService.DiagSeamsSnapped;
      int givenUp = SparrowNestService.DiagSeamsGivenUp;
      this.output.WriteLine($"WELD: placed={placed} seams={seams} givenup={givenUp} (max possible {placed - 1})");

      placed.Should().BeGreaterThan(1, "there has to be more than one part before anything can be shared");

      // The real assertion. Not "some seams": a sheet of one repeated part, every copy allowed to share
      // with every other, has no reason to come apart into separate chains. It measured 25 of a possible
      // 25 once the snap travel was bounded, and 0 before that.
      seams.Should().Be(
        placed - 1,
        "every part on a single-drawing common-line sheet should end up in ONE welded chain; anything "
        + "less means seams are being found and then thrown away, which is what shipped and what made "
        + "common-line cutting produce nothing at all");

      givenUp.Should().Be(0, "no chain should have to be abandoned on a job where every part may share with every other");
    }

    /// <summary>The corpus job that actually uses common line: PartSpacing="0" is SheetCam's flag for it.</summary>
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
              && nest.Parts.Sum(p => p.Quantity) > 2
              && nest.Sheets.Count > 0)
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
