namespace DeepNestLib.CiTests.IO
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib.IO;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// A drawing says what each line is for by the colour it is drawn in, the way the machines these parts
  /// are cut on already work: yellow engraves, blue says what the part is called, everything else gets
  /// cut. Until now nothing read a colour at all, so a part number scribed inside a part was read as a
  /// hole, cut out, and the part came off the sheet with a piece missing.
  /// </summary>
  public class DxfColorRolesFixture
  {
    private const int Yellow = 2;
    private const int Blue = 5;
    private const int Red = 1;
    private const int White = 7;

    /// <summary>
    /// The one that costs material: read as a hole, the mark is cut out and dropped, and the part comes
    /// off the sheet with a piece missing where its number should have been.
    /// </summary>
    [Fact]
    public void AMarkInsideAPartIsNotAHoleInIt()
    {
      var parts = DxfPartSplitter.Split("marked.dxf", new List<DxfEntity>
      {
        InColor(Rectangle(0, 0, 20, 20), White),
        InColor(Rectangle(5, 5, 4, 4), Yellow),
      });

      parts.Should().ContainSingle();
      parts[0].HoleCount.Should().Be(0, "the mark is engraved on the part, not cut out of it");
    }

    /// <summary>And a mark drawn beside the part is not a part of its own, which is what it would be
    /// nested and cut as.</summary>
    [Fact]
    public void AMarkBesideAPartIsNotASecondPart()
    {
      var parts = DxfPartSplitter.Split("marked.dxf", new List<DxfEntity>
      {
        InColor(Rectangle(0, 0, 20, 20), White),
        InColor(Rectangle(40, 0, 4, 4), Yellow),
      });

      parts.Should().ContainSingle();
    }

    /// <summary>
    /// A shop usually sets the colour on the LAYER and leaves every line saying "whatever my layer is".
    /// Reading only the colour on the line would miss every drawing that works that way, which is most
    /// of them.
    /// </summary>
    [Fact]
    public void AMarkThatTakesItsColourFromItsLayerIsStillAMark()
    {
      var drawing = new DxfFile();
      drawing.Layers.Add(new DxfLayer("PROFILE", DxfColor.FromIndex(White)));
      drawing.Layers.Add(new DxfLayer("ENGRAVE", DxfColor.FromIndex(Yellow)));

      var parts = DxfPartSplitter.Split(
        "marked.dxf",
        new List<DxfEntity>
        {
          OnLayer(Rectangle(0, 0, 20, 20), "PROFILE"),
          OnLayer(Rectangle(5, 5, 4, 4), "ENGRAVE"),
        },
        DxfLayerColors.Of(drawing));

      parts.Should().ContainSingle();
      parts[0].HoleCount.Should().Be(0);
    }

    /// <summary>Blue says a TEXT names the part. A line names nothing, so a blue line is cut like any
    /// other: only yellow changes what happens to geometry.</summary>
    [Fact]
    public void ABlueLineIsCutLikeAnyOther()
    {
      var parts = DxfPartSplitter.Split("blue.dxf", new List<DxfEntity>
      {
        InColor(Rectangle(0, 0, 20, 20), Blue),
        InColor(Rectangle(40, 0, 20, 20), Blue),
      });

      parts.Should().HaveCount(2, "the reporter's drawings are blue from end to end");
    }

    /// <summary>
    /// A drawing drawn entirely in the marking colour has to say so. Turned away further down it would
    /// be reported as holding "only text, dimensions or other annotation", which is not true and leaves
    /// the operator with nothing to act on.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task ADrawingDrawnAllInTheMarkingColourSaysSo()
    {
      string path = WriteDrawing(InColor(Rectangle(0, 0, 20, 20), Yellow));

      try
      {
        System.Func<System.Threading.Tasks.Task> load = () => DxfParser.LoadDxfFile(path);

        (await load.Should().ThrowAsync<ArgumentException>()).WithMessage("*nothing left to cut*");
      }
      finally
      {
        File.Delete(path);
      }
    }

    /// <summary>A drawing that says nothing about colour is all cut, which is every drawing anybody is
    /// importing today.</summary>
    [Fact]
    public void ADrawingThatSaysNothingAboutColourIsAllCut()
    {
      var parts = DxfPartSplitter.Split("plain.dxf", new List<DxfEntity>
      {
        Rectangle(0, 0, 20, 20),
        Rectangle(40, 0, 20, 20),
      });

      parts.Should().HaveCount(2);
    }

    /// <summary>
    /// A drawing that writes its part names in the naming colour is taken at its word: nothing written
    /// in any other colour names a part, however near the middle of it somebody wrote it. This is what
    /// ends the guessing for good, rather than guessing better.
    /// </summary>
    [Fact]
    public void WhenNamesAreWrittenInTheNamingColourNothingElseNames()
    {
      var parts = DxfPartSplitter.Split("named.dxf", new List<DxfEntity>
      {
        InColor(Rectangle(0, 0, 20, 20), White),
        InColor(new DxfText(new DxfPoint(10, 10, 0), 1D, "SCRAP,9"), White),
        InColor(new DxfText(new DxfPoint(10, 3, 0), 1D, "PLATE,4"), Blue),
        InColor(Rectangle(40, 0, 20, 20), White),
      });

      parts.Should().HaveCount(2);
      parts.Single(p => p.Name != null).Name.Should().Be("PLATE");
      parts.Single(p => p.Name != null).Quantity.Should().Be(4);
    }

    /// <summary>A part with nothing written on it in that colour is left to be numbered rather than
    /// taking whatever else is drawn inside it. Falling back part by part would bring the guessing
    /// straight back for the one part that is missing its label, which is the part it would hurt.
    /// </summary>
    [Fact]
    public void APartWithNoNameInThatColourIsLeftToBeNumbered()
    {
      var parts = DxfPartSplitter.Split("named.dxf", new List<DxfEntity>
      {
        InColor(Rectangle(0, 0, 20, 20), White),
        InColor(new DxfText(new DxfPoint(10, 10, 0), 1D, "PLATE,4"), Blue),
        InColor(Rectangle(40, 0, 20, 20), White),
        InColor(new DxfText(new DxfPoint(50, 10, 0), 1D, "A NOTE ABOUT THIS ONE"), White),
      });

      parts.Should().HaveCount(2);
      parts.Count(p => p.Name == null).Should().Be(1, "a note written in the cutting colour is a note");
    }

    /// <summary>A text in the marking colour is what gets engraved on the part. It never names it,
    /// whatever else the drawing holds.</summary>
    [Fact]
    public void AMarkedTextNeverNamesAPart()
    {
      var parts = DxfPartSplitter.Split("marked.dxf", new List<DxfEntity>
      {
        InColor(Rectangle(0, 0, 20, 20), White),
        InColor(new DxfText(new DxfPoint(10, 10, 0), 1D, "SN-4400,7"), Yellow),
        InColor(Rectangle(40, 0, 20, 20), White),
      });

      parts.Should().HaveCount(2);
      parts.Should().OnlyContain(p => p.Name == null, "the mark is engraved on the part, not its name");
    }

    /// <summary>
    /// What switches a drawing over is a TEXT written in the naming colour, not a line in it. The
    /// reporter's own drawings are blue from end to end; a drawing whose outlines are blue and whose
    /// labels are black has to keep reading its labels.
    /// </summary>
    [Fact]
    public void ABlueOutlineDoesNotStopABlackLabelNaming()
    {
      var parts = DxfPartSplitter.Split("blue.dxf", new List<DxfEntity>
      {
        InColor(Rectangle(0, 0, 20, 20), Blue),
        InColor(new DxfText(new DxfPoint(10, 10, 0), 1D, "PLATE,4"), White),
      });

      parts.Should().ContainSingle();
      parts[0].Name.Should().Be("PLATE");
      parts[0].Quantity.Should().Be(4);
    }

    /// <summary>Yellow is a family, not one number: these three are all yellow in the default table.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(50)]
    [InlineData(52)]
    public void TheYellowsAreRecognised(int aci)
    {
      DxfColorRoles.IsYellowish(DxfColor.DefaultColors[aci]).Should().BeTrue();
    }

    /// <summary>The half that says the colour rule is a rule rather than something that matches
    /// anything: amber and chartreuse sit either side of yellow and are not it.</summary>
    [Theory]
    [InlineData(1)]    // red
    [InlineData(3)]    // green
    [InlineData(5)]    // blue
    [InlineData(7)]    // white
    [InlineData(9)]    // grey
    [InlineData(40)]   // amber
    [InlineData(60)]   // chartreuse
    public void AColourThatIsNotYellowIsNotAMark(int aci)
    {
      DxfColorRoles.IsYellowish(DxfColor.DefaultColors[aci]).Should().BeFalse();
    }

    /// <summary>Blue is a family too: pure blue, a dark blue and a pale one all name a part.</summary>
    [Theory]
    [InlineData(5)]    // blue
    [InlineData(170)]  // blue again, further down the table
    [InlineData(174)]  // a dark blue
    [InlineData(171)]  // a pale one
    public void TheBluesAreRecognised(int aci)
    {
      DxfColorRoles.IsBluish(DxfColor.DefaultColors[aci]).Should().BeTrue();
    }

    /// <summary>Cyan and magenta both hold as much blue as blue does. What tells blue from them is that
    /// nothing else comes near it.</summary>
    [Theory]
    [InlineData(4)]    // cyan
    [InlineData(6)]    // magenta
    [InlineData(7)]    // white
    [InlineData(9)]    // grey
    [InlineData(3)]    // green
    [InlineData(1)]    // red
    [InlineData(2)]    // yellow
    public void AColourThatIsNotBlueDoesNotName(int aci)
    {
      DxfColorRoles.IsBluish(DxfColor.DefaultColors[aci]).Should().BeFalse();
    }

    /// <summary>What the drawing says on the line itself beats what it says on the layer, which is the
    /// order a CAD program draws it in.</summary>
    [Fact]
    public void AColourOnTheLineBeatsTheOneOnItsLayer()
    {
      var drawing = new DxfFile();
      drawing.Layers.Add(new DxfLayer("ENGRAVE", DxfColor.FromIndex(Yellow)));
      DxfEntity line = OnLayer(InColor(new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(10, 0, 0)), Red), "ENGRAVE");

      DxfColorRoles.RoleOf(line, DxfLayerColors.Of(drawing)).Should().Be(DxfColorRole.Cut);
    }

    /// <summary>A true colour is what the drawing means when it carries one, whatever index sits beside
    /// it. The reporter's own text is written that way: index 5 and true colour 0000FF.</summary>
    [Fact]
    public void ATrueColourIsReadBeforeTheIndex()
    {
      var line = new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(10, 0, 0))
      {
        Color = DxfColor.FromIndex(Red),
        Color24Bit = 0x00FFFF00,
      };

      DxfColorRoles.RoleOf(line, DxfLayerColors.None).Should().Be(DxfColorRole.PartMark);
    }

    /// <summary>
    /// The mark has to stay with the part. It is not a contour and nothing claims it, so without being
    /// put there on purpose it is left out of the file the part is written to, and there is nothing left
    /// to engrave.
    /// </summary>
    [Fact]
    public void AMarkInsideAPartGoesWithThePart()
    {
      DxfText mark = InColor(new DxfText(new DxfPoint(10, 10, 0), 1D, "A1-1"), Yellow);

      var parts = DxfPartSplitter.Split("marked.dxf", new List<DxfEntity>
      {
        InColor(Rectangle(0, 0, 20, 20), White),
        mark,
        InColor(Rectangle(40, 0, 20, 20), White),
      });

      parts.Should().HaveCount(2);
      parts.Single(p => p.Entities.Contains(mark)).HoleCount.Should().Be(0, "a mark is not a hole in it");
      parts.Count(p => p.Entities.Contains(mark)).Should().Be(1, "it goes with one part, not both");
    }

    /// <summary>A mark drawn outside every part is a note on the drawing and belongs to none of them.
    /// </summary>
    [Fact]
    public void AMarkOutsideEveryPartGoesWithNone()
    {
      DxfText note = InColor(new DxfText(new DxfPoint(100, 100, 0), 1D, "CUT 2 OFF"), Yellow);

      var parts = DxfPartSplitter.Split("marked.dxf", new List<DxfEntity>
      {
        InColor(Rectangle(0, 0, 20, 20), White),
        note,
        InColor(Rectangle(40, 0, 20, 20), White),
      });

      parts.Should().HaveCount(2);
      parts.Should().OnlyContain(p => !p.Entities.Contains(note));
    }

    /// <summary>A mark on a part sitting inside another part's hole is the small part's mark. Engraved
    /// on the big one it would be on the wrong piece of metal.</summary>
    [Fact]
    public void AMarkOnAPartInsideAnothersHoleGoesWithTheSmallOne()
    {
      DxfText mark = InColor(new DxfText(new DxfPoint(15, 15, 0), 1D, "SMALL"), Yellow);

      var parts = DxfPartSplitter.Split("nested.dxf", new List<DxfEntity>
      {
        InColor(Rectangle(0, 0, 40, 40), White),
        InColor(Rectangle(5, 5, 30, 30), White),
        InColor(Rectangle(10, 10, 10, 10), White),
        mark,
      });

      parts.Should().HaveCount(2);
      parts.Single(p => p.HoleCount == 0).Entities.Should().Contain(mark);
      parts.Single(p => p.HoleCount == 1).Entities.Should().NotContain(mark, "the mark is on the small part");
    }

    /// <summary>
    /// The whole point, end to end: the file a part is written to still holds its mark, because that
    /// file is what everything downstream reads to draw the mark and to write it out.
    /// </summary>
    [Fact]
    public void TheFileAPartIsWrittenToStillHoldsItsMark()
    {
      string path = WriteDrawing(
        InColor(Rectangle(0, 0, 20, 20), White),
        InColor(new DxfText(new DxfPoint(10, 10, 0), 1D, "A1-1"), Yellow),
        InColor(new DxfText(new DxfPoint(10, 4, 0), 1D, "A1-1,3"), Blue),
        InColor(Rectangle(40, 0, 20, 20), White),
        InColor(new DxfText(new DxfPoint(50, 10, 0), 1D, "A1-2"), Yellow),
        InColor(new DxfText(new DxfPoint(50, 4, 0), 1D, "A1-2,3"), Blue));

      try
      {
        var written = DxfPartSplitter.WriteParts(path);

        written.Should().HaveCount(2);
        foreach (var part in written)
        {
          DxfFile file;
          using (var stream = File.OpenRead(part.Path))
          {
            file = DxfFile.Load(stream);
          }

          string mark = Path.GetFileNameWithoutExtension(part.Path).Split(',')[0];
          file.Entities.OfType<DxfText>().Select(t => t.Value).Should().Contain(
            mark,
            "the part keeps the mark that was drawn on it");
        }
      }
      finally
      {
        File.Delete(path);
      }
    }

    /// <summary>
    /// A mark the operator centred on the part hangs from its SECOND point, and a CAD leaves the first
    /// at nothing. Deciding which part it belongs to by the first point asks about the drawing origin,
    /// which is inside no part, so the mark is given to none - and a mark given to none is deleted from
    /// every file written, without a word, because the writer keeps only what was given to a part.
    /// </summary>
    [Fact]
    public void ACentredMarkSurvivesIntoThePartsFile()
    {
      string path = WriteDrawing(
        InColor(Rectangle(10, 10, 20, 20), White),
        InColor(Centred(20, 20, "A1-1"), Yellow),
        InColor(new DxfText(new DxfPoint(20, 14, 0), 1D, "A1-1,3"), Blue),
        InColor(Rectangle(50, 10, 20, 20), White),
        InColor(Centred(60, 20, "A1-2"), Yellow),
        InColor(new DxfText(new DxfPoint(60, 14, 0), 1D, "A1-2,3"), Blue));

      try
      {
        var written = DxfPartSplitter.WriteParts(path);

        written.Should().HaveCount(2);
        foreach (var part in written)
        {
          DxfFile file;
          using (var stream = File.OpenRead(part.Path))
          {
            file = DxfFile.Load(stream);
          }

          string mark = Path.GetFileNameWithoutExtension(part.Path).Split(',')[0];
          file.Entities.OfType<DxfText>().Select(t => t.Value).Should().Contain(
            mark,
            "a centred mark belongs to the part it was centred on, not to nothing");
        }
      }
      finally
      {
        File.Delete(path);
      }
    }

    /// <summary>
    /// The same first point decides which part a name belongs to. A centred one asks about the origin
    /// too, so the part loses the name the operator gave it AND the count of how many to cut, and falls
    /// back to being numbered and cut once.
    /// </summary>
    [Fact]
    public void ACentredLabelStillNamesItsPartAndSaysHowMany()
    {
      string path = WriteDrawing(
        InColor(Rectangle(10, 10, 20, 20), White),
        InColor(Centred(20, 20, "A1-1,3"), Blue),
        InColor(Rectangle(50, 10, 20, 20), White),
        InColor(Centred(60, 20, "A1-2,7"), Blue));

      try
      {
        var written = DxfPartSplitter.WriteParts(path);

        written.Should().HaveCount(2);
        written.Select(p => Path.GetFileNameWithoutExtension(p.Path)).Should().BeEquivalentTo(
          new[] { "A1-1", "A1-2" },
          "a centred label names the part it was centred on");
        written.Select(p => p.Quantity).Should().BeEquivalentTo(
          new[] { 3, 7 },
          "and says how many of that part to cut");
      }
      finally
      {
        File.Delete(path);
      }
    }

    /// <summary>What is engraved on a part is read back out of the part's own file, which is the one
    /// thing everything that has to draw it or write it out holds in common.</summary>
    [Fact]
    public void WhatIsEngravedOnAPartIsReadBackFromItsFile()
    {
      string path = WriteDrawing(
        InColor(Rectangle(0, 0, 20, 20), White),
        InColor(new DxfText(new DxfPoint(10, 10, 0), 1D, "A1-1"), Yellow),
        InColor(new DxfText(new DxfPoint(10, 4, 0), 1D, "A1-1,3"), Blue));

      try
      {
        IReadOnlyList<DxfEntity> marks = DxfPartMarks.For(path);

        marks.OfType<DxfText>().Select(t => t.Value).Should().BeEquivalentTo(new[] { "A1-1" },
          "the blue text names the part and the white outline is cut; only the yellow is engraved");
      }
      finally
      {
        File.Delete(path);
      }
    }

    [Fact]
    public void APartWithNothingMarkedOnItHasNothingEngraved()
    {
      string path = WriteDrawing(InColor(Rectangle(0, 0, 20, 20), White));

      try
      {
        DxfPartMarks.For(path).Should().BeEmpty();
      }
      finally
      {
        File.Delete(path);
      }
    }

    [Fact]
    public void AFileThatIsNotThereIsNotAskedAboutTwice()
    {
      DxfPartMarks.For(Path.Combine(Path.GetTempPath(), "no-such-part.dxf")).Should().BeEmpty();
      DxfPartMarks.For(null).Should().BeEmpty();
    }

    /// <summary>The answer is kept so a redraw does not read the drawing off the disk every time, and a
    /// part that has been written again since says what it says NOW.</summary>
    [Fact]
    public void APartRewrittenSinceIsReadAgain()
    {
      string path = WriteDrawing(
        InColor(Rectangle(0, 0, 20, 20), White),
        InColor(new DxfText(new DxfPoint(10, 10, 0), 1D, "BEFORE"), Yellow));

      try
      {
        DxfPartMarks.For(path).OfType<DxfText>().Select(t => t.Value).Should().BeEquivalentTo(new[] { "BEFORE" });

        var again = new DxfFile();
        again.Entities.Add(InColor(Rectangle(0, 0, 20, 20), White));
        again.Entities.Add(InColor(new DxfText(new DxfPoint(10, 10, 0), 1D, "AFTER"), Yellow));
        again.Save(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

        DxfPartMarks.For(path).OfType<DxfText>().Select(t => t.Value).Should().BeEquivalentTo(new[] { "AFTER" });
      }
      finally
      {
        File.Delete(path);
      }
    }

    /// <summary>A path that is not just missing but malformed. The import is what refuses a drawing and
    /// says why; asking what is engraved on it must not throw in the middle of a repaint.</summary>
    [Fact]
    public void APathThatCouldNotBeAFileIsAnsweredWithNothing()
    {
      // A null in the middle of it: the one thing a path still cannot hold, and what a rebuilt path
      // out of a corrupt project file comes back as.
      string broken = "C:" + ((char)0) + "part.dxf";

      DxfPartMarks.For(broken).Should().BeEmpty();
      DxfPartMarks.Fresh(broken).Should().BeEmpty();
      DxfPartMarks.LayersFor(broken).Should().BeEmpty();
    }

    /// <summary>A layer name written with a space on the end is the same layer as the one its marks say
    /// they are on, or the mark loses the colour that makes it a mark.</summary>
    [Fact]
    public void ALayerWhoseNameHasASpaceOnItStillColoursItsMarks()
    {
      var drawing = new DxfFile();
      drawing.Layers.Add(new DxfLayer("ENGRAVE ", DxfColor.FromIndex(Yellow)));
      DxfEntity mark = OnLayer(new DxfText(new DxfPoint(5, 5, 0), 1D, "A1-1"), "ENGRAVE");

      DxfColorRoles.RoleOf(mark, DxfLayerColors.Of(drawing)).Should().Be(DxfColorRole.PartMark);
    }

    private static DxfPolyline Rectangle(double x, double y, double width, double height)
    {
      return new DxfPolyline(new[]
      {
        new DxfVertex(new DxfPoint(x, y, 0)),
        new DxfVertex(new DxfPoint(x + width, y, 0)),
        new DxfVertex(new DxfPoint(x + width, y + height, 0)),
        new DxfVertex(new DxfPoint(x, y + height, 0)),
        new DxfVertex(new DxfPoint(x, y, 0)),
      });
    }

    /// <summary>Text the way a CAD writes it once the operator has centred it: the anchor that counts
    /// in the second point, and the first left at nothing.</summary>
    private static DxfText Centred(double x, double y, string value)
    {
      return new DxfText(new DxfPoint(0, 0, 0), 1D, value)
      {
        SecondAlignmentPoint = new DxfPoint(x, y, 0),
        HorizontalTextJustification = DxfHorizontalTextJustification.Center,
        VerticalTextJustification = DxfVerticalTextJustification.Middle,
      };
    }

    private static T InColor<T>(T entity, int aci)
      where T : DxfEntity
    {
      entity.Color = DxfColor.FromIndex((byte)aci);
      return entity;
    }

    private static T OnLayer<T>(T entity, string layer)
      where T : DxfEntity
    {
      entity.Layer = layer;
      return entity;
    }

    private static string WriteDrawing(params DxfEntity[] entities)
    {
      var file = new DxfFile();
      foreach (DxfEntity entity in entities)
      {
        file.Entities.Add(entity);
      }

      string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dxf");
      file.Save(path);
      return path;
    }
  }
}
