namespace DeepNestLib.CiTests.IO
{
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Reflection;
  using DeepNestLib.IO;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// A drawing holding several parts has to import as several parts. Until now the importer kept the
  /// contour with the biggest area and made every other one a HOLE, so nine parts read as one part with
  /// eight holes - and hole filling then drops leftover parts into those holes, on top of solid
  /// material. The distinction that fixes it is containment: a contour with nothing around it is a
  /// part, a contour inside one is a hole in it.
  /// </summary>
  public class DxfPartSplitterFixture
  {
    [Fact]
    public void TwoDisjointOutlinesAreTwoParts()
    {
      var parts = DxfPartSplitter.Split("two.dxf", new List<DxfEntity>
      {
        Rectangle(0, 0, 10, 10),
        Rectangle(30, 0, 10, 10),
      });

      parts.Should().HaveCount(2);
    }

    /// <summary>
    /// The half that makes the test worth anything, and the one a naive "every contour is a part" rule
    /// fails: an outline with a hole in it is ONE part, and the hole stays with it.
    /// </summary>
    [Fact]
    public void AnOutlineWithAHoleIsOnePartAndKeepsTheHole()
    {
      var parts = DxfPartSplitter.Split("holed.dxf", new List<DxfEntity>
      {
        Rectangle(0, 0, 20, 20),
        Rectangle(5, 5, 10, 10),
      });

      parts.Should().ContainSingle("the hole is not a second part");
      parts[0].HoleCount.Should().Be(1, "and the part keeps the hole it was drawn with");
    }

    /// <summary>
    /// Depth, not pairing. A part sitting inside another part's hole is a part in its own right; with a
    /// rule that only asked "is anything around it" it would vanish into its host.
    /// </summary>
    [Fact]
    public void APartInsideAnothersHoleIsItsOwnPart()
    {
      var parts = DxfPartSplitter.Split("nested.dxf", new List<DxfEntity>
      {
        Rectangle(0, 0, 40, 40),
        Rectangle(5, 5, 30, 30),
        Rectangle(10, 10, 20, 20),
      });

      parts.Should().HaveCount(2);
      parts[0].HoleCount.Should().Be(1, "the outer part owns the hole cut out of it");
      parts[1].HoleCount.Should().Be(0, "the part sitting in that hole is on its own");
    }

    /// <summary>Every hole joins the part it is actually cut out of, not the biggest one on the sheet.</summary>
    [Fact]
    public void EachHoleGoesToThePartItWasCutFrom()
    {
      var parts = DxfPartSplitter.Split("two-holed.dxf", new List<DxfEntity>
      {
        Rectangle(0, 0, 20, 20),
        Rectangle(5, 5, 10, 10),
        Rectangle(40, 0, 20, 20),
        Rectangle(45, 5, 10, 10),
      });

      parts.Should().HaveCount(2);
      parts.Should().OnlyContain(p => p.HoleCount == 1, "one outline and its own hole each");
    }

    [Fact]
    public void ALabelInsideAPartNamesItAndSaysHowMany()
    {
      var parts = DxfPartSplitter.Split("labelled.dxf", new List<DxfEntity>
      {
        Rectangle(0, 0, 10, 10),
        Text(5, 5, "BRACKET-01,4"),
        Rectangle(30, 0, 10, 10),
        Text(35, 5, "TAPA IZQUIERDA,12"),
      });

      parts.Should().HaveCount(2);
      parts.Select(p => p.Name).Should().BeEquivalentTo(new[] { "BRACKET-01", "TAPA IZQUIERDA" });
      parts.Single(p => p.Name == "BRACKET-01").Quantity.Should().Be(4);
      parts.Single(p => p.Name == "TAPA IZQUIERDA").Quantity.Should().Be(12);
    }

    /// <summary>
    /// The drawing in the report that asked for this carries no text at all, so an unlabelled part must
    /// still come out as a part. It says nothing about its name or quantity and lets the caller number it.
    /// </summary>
    [Fact]
    public void AnUnlabelledPartSaysNothingRatherThanGuessing()
    {
      var parts = DxfPartSplitter.Split("bare.dxf", new List<DxfEntity> { Rectangle(0, 0, 10, 10) });

      parts.Should().ContainSingle();
      parts[0].Name.Should().BeNull();
      parts[0].Quantity.Should().Be(0);
    }

    /// <summary>A label outside every part belongs to none of them: a title block or a note must not
    /// rename the nearest part.</summary>
    [Fact]
    public void ALabelOutsideEveryPartNamesNothing()
    {
      var parts = DxfPartSplitter.Split("stray.dxf", new List<DxfEntity>
      {
        Rectangle(0, 0, 10, 10),
        Text(100, 100, "NOTE,7"),
      });

      parts.Should().ContainSingle();
      parts[0].Name.Should().BeNull();
    }

    [Theory]
    [InlineData("BRACKET-01,4", "BRACKET-01", 4)]
    [InlineData("  TAPA IZQUIERDA , 12 ", "TAPA IZQUIERDA", 12)]
    [InlineData("PART,A,3", "PART,A", 3)]                 // a name may hold commas; the LAST one splits
    [InlineData("BRACKET-01", "BRACKET-01", 0)]           // name only, quantity left to the caller
    [InlineData("SHELF,MIDDLE", "SHELF,MIDDLE", 0)]       // tail is not a number, so it is part of the name
    [InlineData("PART,0", "PART,0", 0)]                   // zero is not a quantity anyone asked to cut
    [InlineData("PART,-2", "PART,-2", 0)]
    [InlineData("PART,2.5", "PART,2.5", 0)]               // half a part is not a thing
    public void ALabelIsReadWithoutGuessing(string text, string name, int quantity)
    {
      var parsed = DxfPartSplitter.ParseLabel(text);

      parsed.Name.Should().Be(name);
      parsed.Quantity.Should().Be(quantity);
    }

    /// <summary>
    /// The arcs have to survive the split. The common-line planner reads an ARC out of the source
    /// schema, so a part written back as tessellated segments would silently lose the ability to share
    /// a cut on a radius.
    /// </summary>
    [Fact]
    public void ARoundPartKeepsItsArcsRatherThanItsTessellation()
    {
      var parts = DxfPartSplitter.Split("round.dxf", new List<DxfEntity>
      {
        new DxfCircle(new DxfPoint(0, 0, 0), 10),
        Rectangle(100, 0, 10, 10),
      });

      parts.Should().HaveCount(2);
      parts.Should().Contain(p => p.Entities.Any(e => e is DxfCircle),
        "the circle is carried through as a circle, not as the points it was measured with");
    }

    /// <summary>
    /// The drawing from the report, on geometry drawn by somebody in a CAD program rather than by this
    /// fixture: three parts side by side, twelve lines, no holes. It imported as one part before.
    /// </summary>
    [Fact]
    public void TheReportedDrawingOfThreePartsSplitsIntoThree()
    {
      using (var stream = Assembly.GetExecutingAssembly().GetEmbeddedResourceStream("Dxfs.issue6_3parts.dxf"))
      {
        var parts = DxfPartSplitter.Split("issue6.dxf", DxfFile.Load(stream).Entities);

        parts.Should().HaveCount(3, "the drawing holds three separate parts");
        parts.Should().OnlyContain(p => p.HoleCount == 0, "none of which has a hole in it");
      }
    }

    /// <summary>
    /// The other direction, and the one that costs material if it goes wrong: a single real part with
    /// holes in it must NOT be split. This L-bracket has three holes and is one part, whatever its file
    /// is called - the nine in the name is how many to cut, not how many are drawn.
    /// </summary>
    [Fact]
    public void ARealPartWithHolesIsNotSplitIntoItsHoles()
    {
      using (var stream = Assembly.GetExecutingAssembly().GetEmbeddedResourceStream("Dxfs.9parts_3holes.dxf"))
      {
        var parts = DxfPartSplitter.Split("bracket.dxf", DxfFile.Load(stream).Entities);

        parts.Should().ContainSingle("one bracket, however many holes are cut in it");
        parts[0].HoleCount.Should().Be(3);
      }
    }

    /// <summary>
    /// The written files have to stand on their own: each one is a real DXF holding exactly the part it
    /// names, so everything downstream that reads a part by its path - thumbnails, the contour catalog,
    /// the exporter - sees one part and not the whole drawing.
    /// </summary>
    [Fact]
    public void EachWrittenPartIsADrawingOfExactlyThatPart()
    {
      string source = WriteSource("two-parts", new List<DxfEntity>
      {
        Rectangle(0, 0, 10, 10),
        Rectangle(30, 0, 20, 20),
        Rectangle(35, 5, 5, 5),
      });

      try
      {
        var written = DxfPartSplitter.WriteParts(source);

        written.Should().HaveCount(2);
        foreach (var part in written)
        {
          File.Exists(part.Path).Should().BeTrue();
          DxfPartSplitter.Split(part.Path, DxfFile.Load(part.Path).Entities)
            .Should().ContainSingle("a part written on its own is one part when read back");
        }
      }
      finally
      {
        File.Delete(source);
      }
    }

    /// <summary>A drawing that already holds one part is left completely alone: no copy is written and
    /// the operator keeps their own file, which is what every import did before this existed.</summary>
    [Fact]
    public void ADrawingOfOnePartIsNotCopiedAnywhere()
    {
      string source = WriteSource("one-part", new List<DxfEntity> { Rectangle(0, 0, 10, 10) });

      try
      {
        DxfPartSplitter.WriteParts(source).Should().BeEmpty();
      }
      finally
      {
        File.Delete(source);
      }
    }

    /// <summary>The label names the file, because a part row is named after its file.</summary>
    [Fact]
    public void TheLabelNamesTheFileAndSaysHowMany()
    {
      string source = WriteSource("labelled-parts", new List<DxfEntity>
      {
        Rectangle(0, 0, 10, 10),
        Text(5, 5, "BRACKET-01,4"),
        Rectangle(30, 0, 10, 10),
        Text(35, 5, "TAPA IZQUIERDA,12"),
      });

      try
      {
        var written = DxfPartSplitter.WriteParts(source);

        written.Select(p => Path.GetFileNameWithoutExtension(p.Path))
          .Should().BeEquivalentTo(new[] { "BRACKET-01", "TAPA IZQUIERDA" });
        written.Select(p => p.Quantity).Should().BeEquivalentTo(new[] { 4, 12 });
      }
      finally
      {
        File.Delete(source);
      }
    }

    /// <summary>
    /// Two parts labelled the same must not become one file. Overwriting would leave the second part a
    /// copy of the first, and a sheet would be cut with the wrong shape on it.
    /// </summary>
    [Fact]
    public void TwoPartsLabelledTheSameGetTheirOwnFiles()
    {
      string source = WriteSource("same-label", new List<DxfEntity>
      {
        Rectangle(0, 0, 10, 10),
        Text(5, 5, "PLATE,2"),
        Rectangle(30, 0, 20, 20),
        Text(35, 5, "PLATE,2"),
      });

      try
      {
        var written = DxfPartSplitter.WriteParts(source);

        written.Should().HaveCount(2);
        written.Select(p => p.Path).Distinct().Should().HaveCount(2, "one file each, whatever they are called");
      }
      finally
      {
        File.Delete(source);
      }
    }

    /// <summary>A label is drawn by a person, so it can hold anything. A name Windows will not store
    /// must not stop the import.</summary>
    [Fact]
    public void ALabelThatCannotBeAFileNameStillImports()
    {
      string source = WriteSource("bad-label", new List<DxfEntity>
      {
        Rectangle(0, 0, 10, 10),
        Text(5, 5, @"A/B:C*?,3"),
        Rectangle(30, 0, 20, 20),
      });

      try
      {
        var written = DxfPartSplitter.WriteParts(source);

        written.Should().HaveCount(2);
        written.Should().OnlyContain(p => File.Exists(p.Path));
      }
      finally
      {
        File.Delete(source);
      }
    }

    /// <summary>
    /// The parts live in temp so importing never writes into the operator's own folders, and Windows
    /// clears temp. A project saved last month has to open anyway, so a part whose file has gone is put
    /// back from the drawing it came out of.
    /// </summary>
    [Fact]
    public void APartIsPutBackAfterItsCopyHasBeenCleanedAway()
    {
      string source = WriteSource("rebuildable", new List<DxfEntity>
      {
        Rectangle(0, 0, 10, 10),
        Rectangle(30, 0, 20, 20),
      });

      try
      {
        var written = DxfPartSplitter.WriteParts(source);
        string gone = written[1].Path;
        File.Delete(gone);

        string rebuilt = DxfPartSplitter.Rebuild(source, written[1].PartIndex);

        rebuilt.Should().Be(gone, "the same part comes back in the same place");
        File.Exists(rebuilt).Should().BeTrue();
      }
      finally
      {
        File.Delete(source);
      }
    }

    /// <summary>And a drawing that has gone for good says so rather than throwing on project open.</summary>
    [Fact]
    public void AVanishedDrawingCannotBeRebuiltAndSaysSo()
    {
      DxfPartSplitter.Rebuild(Path.Combine(Path.GetTempPath(), "no-such-drawing.dxf"), 0)
        .Should().BeNull();
    }

    /// <summary>
    /// A split part is the drawing it came out of with only its own geometry left, so it has to keep
    /// that drawing's header. This is not cosmetic: DxfContourCatalog scales the contour-joining
    /// tolerance by the drawing's units, so a metric part that came back saying nothing would be
    /// measured in inches and its tolerance would be 25.4 times too tight. Common cutting would then
    /// fail on split parts only, silently, and only on metric jobs.
    /// </summary>
    [Theory]
    [InlineData(DxfUnits.Millimeters)]
    [InlineData(DxfUnits.Inches)]
    public void ASplitPartDeclaresTheSameUnitsAsTheDrawingItCameFrom(DxfUnits units)
    {
      string source = WriteSource(
        "units",
        new List<DxfEntity> { Rectangle(0, 0, 10, 10), Rectangle(30, 0, 20, 20) },
        file =>
        {
          file.Header.DefaultDrawingUnits = units;
          file.Header.Version = DxfAcadVersion.R2013;
        });

      try
      {
        var written = DxfPartSplitter.WriteParts(source);

        written.Should().HaveCount(2);
        foreach (var part in written)
        {
          DxfFile.Load(part.Path).Header.DefaultDrawingUnits.Should().Be(units);
        }
      }
      finally
      {
        File.Delete(source);
      }
    }

    /// <summary>And the drawing's version survives too, rather than everything coming back as R12.</summary>
    [Fact]
    public void ASplitPartKeepsTheDrawingVersionItCameFrom()
    {
      string source = WriteSource(
        "version",
        new List<DxfEntity> { Rectangle(0, 0, 10, 10), Rectangle(30, 0, 20, 20) },
        file => file.Header.Version = DxfAcadVersion.R2013);

      try
      {
        var written = DxfPartSplitter.WriteParts(source);

        written.Should().OnlyContain(p => DxfFile.Load(p.Path).Header.Version == DxfAcadVersion.R2013);
      }
      finally
      {
        File.Delete(source);
      }
    }

    /// <summary>
    /// The one that reproduces what a real batch drawing did: eight labelled parts drawn as LWPOLYLINE.
    /// Seven of the eight came out with an EMPTY entities section, so nesting refused them one by one
    /// with "has no geometry to nest". The earlier tests missed it twice over - they built two parts,
    /// not eight, and they used the old POLYLINE and plain LINE, never LWPOLYLINE.
    /// </summary>
    [Fact]
    public void EveryOneOfManyLwPolylinePartsIsWrittenWithItsGeometry()
    {
      var entities = new List<DxfEntity>();
      for (int i = 0; i < 8; i++)
      {
        double x = i * 30;
        entities.Add(LwRectangle(x, 0, 20, 20));
        entities.Add(LwRectangle(x + 5, 5, 5, 5));
        entities.Add(Text(x + 12, 15, $"A{i}-4,2"));
      }

      // R12 has no LWPOLYLINE at all, so a source written as R12 would lose them before the split ever
      // ran. Any drawing that actually holds LWPOLYLINEs is R14 or later.
      string source = WriteSource("many-lwpolyline", entities, file => file.Header.Version = DxfAcadVersion.R2013);

      try
      {
        var written = DxfPartSplitter.WriteParts(source);

        written.Should().HaveCount(8);
        foreach (var part in written)
        {
          DxfFile.Load(part.Path).Entities.Should().NotBeEmpty(
            $"{Path.GetFileName(part.Path)} has to hold the geometry it was written for");
          DxfPartSplitter.Split(part.Path, DxfFile.Load(part.Path).Entities)
            .Should().ContainSingle("and read back as exactly the one part it names");
        }
      }
      finally
      {
        File.Delete(source);
      }
    }

    private static DxfLwPolyline LwRectangle(double x, double y, double width, double height)
      => new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = x, Y = y },
        new DxfLwPolylineVertex { X = x + width, Y = y },
        new DxfLwPolylineVertex { X = x + width, Y = y + height },
        new DxfLwPolylineVertex { X = x, Y = y + height },
      })
      {
        IsClosed = true,
      };

    private static string WriteSource(string stem, List<DxfEntity> entities)
      => WriteSource(stem, entities, null);

    private static string WriteSource(string stem, List<DxfEntity> entities, System.Action<DxfFile> configure)
    {
      var file = new DxfFile();
      configure?.Invoke(file);
      foreach (var entity in entities)
      {
        file.Entities.Add(entity);
      }

      string path = Path.Combine(Path.GetTempPath(), $"sheetnest-{stem}-{System.Guid.NewGuid():N}.dxf");
      file.Save(path);
      return path;
    }

    private static DxfPolyline Rectangle(double x, double y, double width, double height)
    {
      var polyline = new DxfPolyline(new[]
      {
        new DxfVertex(new DxfPoint(x, y, 0)),
        new DxfVertex(new DxfPoint(x + width, y, 0)),
        new DxfVertex(new DxfPoint(x + width, y + height, 0)),
        new DxfVertex(new DxfPoint(x, y + height, 0)),
        new DxfVertex(new DxfPoint(x, y, 0)),
      });

      return polyline;
    }

    private static DxfText Text(double x, double y, string value)
      => new DxfText(new DxfPoint(x, y, 0), 1D, value);
  }
}
