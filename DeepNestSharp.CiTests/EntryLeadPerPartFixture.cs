namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// Which parts of a SheetCam job brought a way IN.
  /// <para>
  /// Parts sharing a common line are cut as one path, so the chain needs a single lead-in. The job is set
  /// up for that by carrying the drawing twice, one part with a lead and one without, and whether that
  /// setup took effect is decided in SheetCam by giving the second part its own operation with the
  /// lead-in off. Nothing in SheetNest could tell a job that was set up right from one that was not, so
  /// the import summary now says it per part - and this fixes what "has a lead-in" means.
  /// </para>
  /// <para>
  /// It means a lead that starts OUTSIDE the outline. A part can carry six leads and still have no way
  /// in, because five of them pierce inside its holes: those cut the holes and do nothing for the part's
  /// own contour. Counting leads instead of looking at where they start would call such a part ready to
  /// cut, which is the mistake this guards.
  /// </para>
  /// </summary>
  public class EntryLeadPerPartFixture
  {
    private readonly ITestOutputHelper output;

    public EntryLeadPerPartFixture(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void OnlyALeadThatStartsOutsideTheOutlineIsAWayIn()
    {
      string nestPath = ShopJob();
      if (nestPath == null)
      {
        this.output.WriteLine("ENTRY: no .nest with leads in the corpus - skipping.");
        return;
      }

      var nest = SheetCamNestFile.Load(nestPath);
      var part = nest.Parts.First(p => p.ToolPaths.Count > 0);
      var outline = new NoFitPolygon(part.External);

      int exterior = part.ToolPaths.Count(t => t.Any(v => !Inside(v, part.External)));
      int interior = part.ToolPaths.Count - exterior;
      this.output.WriteLine($"ENTRY: {part.Name} has {exterior} exterior and {interior} interior lead(s)");

      exterior.Should().BeGreaterThan(0, "the shop's own part is cut from the outside, so it has an entry lead");
      interior.Should().BeGreaterThan(0, "and it also pierces inside its holes, which is what makes this worth testing");

      SparrowNestService.HasEntryLead(outline, part.ToolPaths).Should().BeTrue(
        "one of its leads starts outside the outline");

      // The part as it would arrive with its lead-in turned off in SheetCam: the hole leads remain,
      // because those belong to the holes' own operations, and there is still no way into the contour.
      var interiorOnly = part.ToolPaths.Where(t => t.All(v => Inside(v, part.External))).ToList();
      interiorOnly.Should().NotBeEmpty("this is the case that must not be mistaken for having an entry");

      SparrowNestService.HasEntryLead(outline, interiorOnly).Should().BeFalse(
        "every remaining lead pierces inside a hole, so the part's own contour still has no way in - "
        + "counting leads rather than looking at where they start would call this ready to cut");
    }

    [Fact]
    public void APartWithNoLeadsAtAllHasNoWayIn()
    {
      var bar = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0), new SvgPoint(10, 0), new SvgPoint(10, 20), new SvgPoint(0, 20),
      });

      SparrowNestService.HasEntryLead(bar, null).Should().BeFalse();
      SparrowNestService.HasEntryLead(bar, new List<IReadOnlyList<SvgPoint>>()).Should().BeFalse();
    }

    private static bool Inside(SvgPoint p, IReadOnlyList<SvgPoint> poly)
    {
      bool inside = false;
      for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
      {
        double xi = poly[i].X, yi = poly[i].Y, xj = poly[j].X, yj = poly[j].Y;
        if (((yi > p.Y) != (yj > p.Y)) && (p.X < (((xj - xi) * (p.Y - yi)) / (yj - yi)) + xi))
        {
          inside = !inside;
        }
      }

      return inside;
    }

    /// <summary>A corpus job that actually carries leads, which is what makes the distinction testable.</summary>
    private static string ShopJob()
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
          if (nest.Parts.Any(p => p.ToolPaths.Count > 1))
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
