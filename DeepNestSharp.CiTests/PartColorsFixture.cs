namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using FakeItEasy;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Colour coding per part type (issue #2: "different part types had different colors, making them easier
  /// to identify when multiple parts are nested together"). The viewer and the PDF report both read this
  /// map, so it has to be keyed and ordered the same way for both.
  /// </summary>
  public class PartColorsFixture
  {
    // The colours that already MEAN something in the viewer or the offcut dialog; a part must never be
    // mistaken for a state.
    private static readonly (byte R, byte G, byte B)[] Reserved =
    {
      (0x00, 0x00, 0x80), // navy   - selected
      (0xD3, 0x2F, 0x2F), // red    - invalid position
      (0xC6, 0x28, 0x28), // red    - lead-in / kerf band
      (0x2E, 0x7D, 0x32), // green  - the remnant tint on the offcut dialog's preview cards
      (0x00, 0x00, 0x00), // black  - the offcut cut line and its size label
    };

    [Fact]
    public void EachPartTypeGetsItsOwnColour()
    {
      var map = PartColors.Build(new[]
      {
        @"C:\jobs\bracket.dxf",
        @"C:\jobs\bracket.dxf", // the same part listed twice
        @"C:\jobs\rail.dxf",
        @"C:\jobs\gusset.dxf",
      });

      map.Should().HaveCount(3, "three distinct files are in the job");
      map.Values.Distinct().Should().HaveCount(3, "each type needs a colour of its own");
    }

    /// <summary>
    /// The parts list is the legend: one row, one thumbnail, one colour. A mirrored copy has no row of its
    /// own, so giving it a colour of its own would put a colour on the sheet that nothing explains.
    /// </summary>
    [Fact]
    public void AMirroredCopySharesItsOriginalsColour()
    {
      var map = PartColors.Build(new[] { @"C:\jobs\bracket.dxf" });

      PartColors.For(map, Placement(@"C:\jobs\bracket.dxf", mirrored: true))
        .Should().Be(PartColors.For(map, Placement(@"C:\jobs\bracket.dxf")));
    }

    /// <summary>The report still COUNTS a mirrored copy separately — a left-hand and a right-hand part are
    /// different physical parts on the shop floor — it just draws them in the same colour. Label and colour
    /// key are deliberately different things; this pins them apart.</summary>
    [Fact]
    public void LabelKeepsMirroredButTheColourKeyDoesNot()
    {
      var mirrored = Placement(@"C:\jobs\bracket.dxf", mirrored: true);

      PartColors.LabelFor(mirrored).Should().Be("bracket.dxf (mirrored)");
      PartColors.ColourKeyFor(mirrored).Should().Be("bracket.dxf");
      PartColors.LabelFor(Placement(@"C:\jobs\bracket.dxf")).Should().Be("bracket.dxf");
    }

    /// <summary>
    /// "Que sean en orden, part 1-1000": the colour comes from the part's PLACE IN THE LIST, so the first
    /// part always gets the first colour. (An earlier version assigned them alphabetically by file name;
    /// that is what this replaces.) The same file listed twice is one part and keeps its first row's place.
    /// </summary>
    [Fact]
    public void ColoursFollowTheOrderOfThePartList()
    {
      var map = PartColors.Build(new[] { "zulu.dxf", "alpha.dxf", "zulu.dxf", "mike.dxf" });

      map["zulu.dxf"].Should().Be(PartColors.PaletteAt(0));
      map["alpha.dxf"].Should().Be(PartColors.PaletteAt(1));
      map["mike.dxf"].Should().Be(PartColors.PaletteAt(2));
    }

    [Fact]
    public void AChosenColourWinsOverTheOneForItsPlace()
    {
      var map = PartColors.Build(new[] { ("a.dxf", -1), ("b.dxf", 0x123456), ("c.dxf", -1) });

      map["b.dxf"].Should().Be(((byte)0x12, (byte)0x34, (byte)0x56));
    }

    /// <summary>Recolouring one part must not move anyone else's: part 3 keeps the third colour whether or
    /// not part 2 was overridden.</summary>
    [Fact]
    public void AChosenColourDoesNotShiftTheOtherParts()
    {
      var untouched = PartColors.Build(new[] { ("a.dxf", -1), ("b.dxf", -1), ("c.dxf", -1) });
      var overridden = PartColors.Build(new[] { ("a.dxf", -1), ("b.dxf", 0x123456), ("c.dxf", -1) });

      overridden["a.dxf"].Should().Be(untouched["a.dxf"]);
      overridden["c.dxf"].Should().Be(untouched["c.dxf"]);
    }

    [Fact]
    public void OnePartIsStillColoured()
    {
      var map = PartColors.Build(new[] { "solo.dxf", "solo.dxf" });

      PartColors.For(map, Placement("solo.dxf")).Should().NotBe(PartColors.Default, "a one-part job must not come out grey");
      PartColors.For(map, Placement("solo.dxf")).Should().Be(PartColors.PaletteAt(0));
    }

    /// <summary>
    /// The requirement in Manuel's words: "colores no grises". A muted palette on a near-white sheet reads
    /// as dirty grey, which is what the first attempt at this got wrong - every entry has to be a real
    /// colour. Saturation is what says so; lightness does not (a saturated violet is darker than the muddy
    /// brown this rejects, and the violet is fine).
    /// </summary>
    [Fact]
    public void EveryPaletteEntryIsARealColourNotAGrey()
    {
      for (int i = 0; i < PartColors.PaletteLength; i++)
      {
        var colour = PartColors.PaletteAt(i);
        Saturation(colour).Should().BeGreaterThan(
          0.6, $"palette entry {i} ({Hex(colour)}) is washed out enough to read as grey on the sheet");
      }
    }

    [Fact]
    public void AnUnknownPlacementFallsBackToTheClassicFill()
    {
      var map = PartColors.Build(new[] { "a.dxf", "b.dxf" });

      PartColors.For(map, Placement("never-nested.dxf")).Should().Be(PartColors.Default);
      PartColors.For(map, (IPartPlacement)null).Should().Be(PartColors.Default);
      PartColors.For(null, Placement("a.dxf")).Should().Be(PartColors.Default);
    }

    [Fact]
    public void MorePartsThanColoursCyclesWithoutFailing()
    {
      var many = Enumerable.Range(0, PartColors.PaletteLength * 2 + 3)
        .Select(i => $"part{i:00}.dxf")
        .ToList();

      var map = PartColors.Build(many);

      map.Should().HaveCount(many.Count);
      map.Values.Distinct().Should().HaveCount(PartColors.PaletteLength, "the palette repeats rather than inventing colours");
    }

    /// <summary>No part colour may look like the selection, the invalid-position flash, the cut band or the
    /// offcut outline - otherwise a part reads as a state.</summary>
    [Fact]
    public void NoPaletteColourIsMistakableForAViewerState()
    {
      for (int i = 0; i < PartColors.PaletteLength; i++)
      {
        var colour = PartColors.PaletteAt(i);
        foreach (var reserved in Reserved)
        {
          Distance(colour, reserved).Should().BeGreaterThan(
            75, $"palette entry {i} ({Hex(colour)}) is too close to the reserved {Hex(reserved)}");
        }
      }
    }

    [Fact]
    public void PaletteColoursAreDistinguishableFromEachOther()
    {
      for (int i = 0; i < PartColors.PaletteLength; i++)
      {
        for (int j = i + 1; j < PartColors.PaletteLength; j++)
        {
          Distance(PartColors.PaletteAt(i), PartColors.PaletteAt(j)).Should().BeGreaterThan(
            40, $"{Hex(PartColors.PaletteAt(i))} and {Hex(PartColors.PaletteAt(j))} would look like the same part");
        }
      }
    }

    [Fact]
    public void TheKeyIsTheFileNameNotItsWholePath()
    {
      var map = PartColors.Build(new[] { @"C:\one\folder\bracket.dxf", @"D:\another\bracket.dxf" });

      map.Should().HaveCount(1, "the same part file name is the same part wherever it sits on disk");
      map.Keys.Single().Should().Be("bracket.dxf");
    }

    [Fact]
    public void APlacementWithNoFileStillHasAKey()
    {
      PartColors.LabelFor(Placement(null)).Should().Be("(part)");
      PartColors.LabelFor(null).Should().Be("(part)");
      PartColors.ColourKeyFor(Placement(null)).Should().Be("(part)");
    }

    /// <summary>The sheet light is light on the part's own colour, never another hue, or the thumbnail beside
    /// it in the parts list would stop naming it.</summary>
    [Fact]
    public void TheSheetLightKeepsThePartsColour()
    {
      for (int i = 0; i < PartColors.PaletteLength; i++)
      {
        var colour = PartColors.PaletteAt(i);
        foreach (var (offset, stop) in PartColors.SheetLightStops(colour))
        {
          HueGap(Hue(stop), Hue(colour)).Should().BeLessThan(
            4, $"the stop at {offset} ({Hex(stop)}) has left the hue of {Hex(colour)}");
        }
      }
    }

    /// <summary>What makes it read as a lit sheet: the full colour at both edges, palest in the middle, and
    /// the same on either side of it.</summary>
    [Fact]
    public void TheSheetLightIsFullColourAtTheEdgesAndPalestInTheMiddle()
    {
      for (int i = 0; i < PartColors.PaletteLength; i++)
      {
        var colour = PartColors.PaletteAt(i);
        var stops = PartColors.SheetLightStops(colour);

        stops.First().Colour.Should().Be(colour, $"{Hex(colour)} shows its full colour at the left edge");
        stops.Last().Colour.Should().Be(colour, $"{Hex(colour)} shows its full colour at the right edge");
        stops.OrderByDescending(s => Luminance(s.Colour)).First().Offset.Should().BeApproximately(
          0.5, 1e-9, $"{Hex(colour)} is palest in the middle of the sheet");
        for (int s = 0; s < stops.Count; s++)
        {
          stops[s].Colour.Should().Be(stops[stops.Count - 1 - s].Colour, $"{Hex(colour)} is lit the same on both halves");
        }
      }
    }

    /// <summary>
    /// The near-black outline is what separates two touching parts of the same type, and the engraving is
    /// drawn in it too. Nothing in the light may come out darker than the part's colour, or it swallows both;
    /// a dark colour somebody picked is held to the same rule.
    /// </summary>
    [Fact]
    public void TheSheetLightNeverDarkensAPart()
    {
      var colours = Enumerable.Range(0, PartColors.PaletteLength).Select(PartColors.PaletteAt)
        .Append(((byte)0x30, (byte)0x20, (byte)0x60)); // a dark indigo somebody picked

      foreach (var colour in colours)
      {
        foreach (var (offset, stop) in PartColors.SheetLightStops(colour))
        {
          Luminance(stop).Should().BeGreaterOrEqualTo(
            Luminance(colour) - 1e-9, $"the stop at {offset} ({Hex(stop)}) is darker than {Hex(colour)}");
        }
      }
    }

    /// <summary>The same rule as the palette itself: no part of the light may look like a state.</summary>
    [Fact]
    public void NoSheetLightStopIsMistakableForAViewerState()
    {
      for (int i = 0; i < PartColors.PaletteLength; i++)
      {
        var colour = PartColors.PaletteAt(i);
        foreach (var (offset, stop) in PartColors.SheetLightStops(colour))
        {
          foreach (var reserved in Reserved)
          {
            Distance(stop, reserved).Should().BeGreaterThan(
              75, $"the stop at {offset} ({Hex(stop)}) of {Hex(colour)} is too close to the reserved {Hex(reserved)}");
          }
        }
      }
    }

    /// <summary>Relative luminance: how light a colour looks against black.</summary>
    private static double Luminance((byte R, byte G, byte B) c)
    {
      static double Linear(byte v)
      {
        double s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
      }

      return (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));
    }

    /// <summary>HSV hue in degrees.</summary>
    private static double Hue((byte R, byte G, byte B) c)
    {
      double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
      double max = Math.Max(r, Math.Max(g, b));
      double delta = max - Math.Min(r, Math.Min(g, b));
      if (delta <= 0)
      {
        return 0;
      }

      double h = max == r ? ((g - b) / delta) % 6 : max == g ? ((b - r) / delta) + 2 : ((r - g) / delta) + 4;
      return ((h * 60) + 360) % 360;
    }

    private static double HueGap(double a, double b)
    {
      double gap = Math.Abs(a - b) % 360;
      return Math.Min(gap, 360 - gap);
    }

    /// <summary>HSV saturation: how far the colour is from a grey of the same brightness.</summary>
    private static double Saturation((byte R, byte G, byte B) c)
    {
      int max = Math.Max(c.R, Math.Max(c.G, c.B));
      int min = Math.Min(c.R, Math.Min(c.G, c.B));
      return max == 0 ? 0 : (max - min) / (double)max;
    }

    private static double Distance((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
      double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
      return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }

    private static string Hex((byte R, byte G, byte B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static IPartPlacement Placement(string path, bool mirrored = false)
    {
      var part = A.Fake<INfp>();
      A.CallTo(() => part.Name).Returns(path);
      var placement = A.Fake<IPartPlacement>();
      A.CallTo(() => placement.Part).Returns(part);
      A.CallTo(() => placement.IsMirrored).Returns(mirrored);
      return placement;
    }
  }
}
