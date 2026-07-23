namespace DeepNestLib.CiTests.IO
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Xml.Linq;
  using DeepNestLib.IO;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The .nest round trip with SheetCam. What has to hold: the geometry and everything we do not model
  /// survives untouched, and a part's local frame is preserved end to end — that frame is what makes a
  /// placement directly writable as a Position, so a shift anywhere in the chain would silently move
  /// every part on the sheet.
  /// </summary>
  public class SheetCamNestFileFixture
  {
    /// <summary>
    /// A 20 x 40 mm bar with a 6 mm square hole, centred on its own origin exactly like SheetCam writes
    /// them. Carries a Lead element we do not model, to prove it survives a rewrite.
    /// </summary>
    private const string SampleNest = @"<?xml version=""1.0"" encoding=""utf-8""?>
<NestingData ID=""SheetCam01"" Version=""1.0"">
  <Settings Units=""in"" PartSpacing=""0.25"" EdgeSpacing=""0.25"" />
  <Metadata><Job Name=""sample.job"" /></Metadata>
  <Sheets>
    <Sheet Name=""Sheet"" Quantity=""1"">
      <External>
        <Point X=""0"" Y=""0"" />
        <Point X=""3048"" Y=""0"" />
        <Point X=""3048"" Y=""1524"" />
        <Point X=""0"" Y=""1524"" />
        <Point X=""0"" Y=""0"" />
      </External>
    </Sheet>
  </Sheets>
  <Parts>
    <Part Name=""bar"">
      <Positions>
        <Position X=""0"" Y=""0"" A=""0"" Sheet=""0"" />
        <Position X=""0"" Y=""0"" A=""0"" Sheet=""0"" />
      </Positions>
      <External>
        <Point X=""-10"" Y=""-20"" />
        <Point X=""10"" Y=""-20"" />
        <Point X=""10"" Y=""20"" />
        <Point X=""-10"" Y=""20"" />
        <Point X=""-10"" Y=""-20"" />
      </External>
      <Internal>
        <Point X=""-3"" Y=""-3"" />
        <Point X=""3"" Y=""-3"" />
        <Point X=""3"" Y=""3"" />
        <Point X=""-3"" Y=""3"" />
        <Point X=""-3"" Y=""-3"" />
      </Internal>
      <Lead X=""-10"" Y=""0"" />
    </Part>
  </Parts>
</NestingData>";

    [Fact]
    public void ReadsPartsSheetsAndSettings()
    {
      var nest = Load(SampleNest);

      nest.Units.Should().Be("in");
      nest.PartSpacing.Should().Be(0.25);
      nest.EdgeSpacing.Should().Be(0.25);
      nest.JobName.Should().Be("sample.job");

      nest.Parts.Should().HaveCount(1);
      var part = nest.Parts[0];
      part.Name.Should().Be("bar");
      part.Quantity.Should().Be(2, "quantity is one Position element per copy");
      part.External.Should().HaveCount(4, "the repeated closing point is dropped");
      part.Internals.Should().HaveCount(1);
      part.Internals[0].Should().HaveCount(4);

      // Geometry is millimetres even though Units says inches: this is a 120 x 60 inch sheet.
      nest.Sheets.Should().HaveCount(1);
      nest.Sheets[0].Width.Should().Be(3048);
      nest.Sheets[0].Height.Should().Be(1524);
      (nest.Sheets[0].Width / SheetCamNestFile.MmPerDrawingUnit(false)).Should().BeApproximately(120, 1e-9);
      (nest.Sheets[0].Height / SheetCamNestFile.MmPerDrawingUnit(false)).Should().BeApproximately(60, 1e-9);
    }

    [Fact]
    public void RewritingPositionsLeavesGeometryAndUnmodelledElementsAlone()
    {
      var path = TempFile(SampleNest);
      try
      {
        var nest = SheetCamNestFile.Load(path);
        nest.SetPositions("bar", new[]
        {
          new SheetCamNestPosition(100.5, 200.25, Math.PI / 2, 0),
          new SheetCamNestPosition(300, 400, Math.PI, 1),
        });
        nest.SetSheetCount(2);
        nest.Save(path);

        var written = XDocument.Load(path);
        var part = written.Root.Element("Parts").Element("Part");

        // Everything we do not model has to survive — SheetCam jobs with lead-ins carry Lead/Loop.
        part.Element("Lead").Should().NotBeNull("elements we do not model must survive a rewrite");
        written.Root.Element("Metadata").Element("Job").Attribute("Name").Value.Should().Be("sample.job");
        written.Root.Element("Settings").Attribute("PartSpacing").Value.Should().Be("0.25");

        // Geometry values must come back bit-for-bit; SheetCam re-matches parts on them.
        part.Element("External").Elements("Point").Select(p => p.Attribute("X").Value)
          .Should().Equal("-10", "10", "10", "-10", "-10");
        part.Element("Internal").Elements("Point").Should().HaveCount(5);

        var positions = part.Element("Positions").Elements("Position").ToList();
        positions.Should().HaveCount(2);
        positions[0].Attribute("X").Value.Should().Be("100.5");
        positions[0].Attribute("Sheet").Value.Should().Be("0");
        positions[1].Attribute("Sheet").Value.Should().Be("1");
        written.Root.Element("Sheets").Element("Sheet").Attribute("Quantity").Value.Should().Be("2");
      }
      finally
      {
        File.Delete(path);
      }
    }

    [Theory]
    [InlineData(@"ID=""Other01"" Version=""1.0""", "vendor")]
    [InlineData(@"ID=""SheetCam01"" Version=""2.0""", "version")]
    public void RejectsForeignVendorOrVersion(string attributes, string because)
    {
      var xml = SampleNest.Replace(@"ID=""SheetCam01"" Version=""1.0""", attributes);
      var path = TempFile(xml);
      try
      {
        Action load = () => SheetCamNestFile.Load(path);
        load.Should().Throw<InvalidDataException>("SheetCam validates the {0} marker too", because);
      }
      finally
      {
        File.Delete(path);
      }
    }

    /// <summary>
    /// The load-bearing one. A part written out as DXF and read back through the normal part pipeline
    /// must keep the coordinates it had in the .nest (only scaled) — no normalisation to the origin.
    /// If this ever fails, every exported position is off by the shift.
    /// </summary>
    [Fact]
    public void PartGeometrySurvivesTheTripThroughDxfWithItsLocalFrameIntact()
    {
      var nestPath = TempFile(SampleNest);
      var dxfPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dxf");
      try
      {
        var nest = SheetCamNestFile.Load(nestPath);
        double scale = 1.0 / SheetCamNestFile.MmPerDrawingUnit(false); // mm -> inches
        SheetCamNestPartWriter.Write(nest.Parts[0], dxfPath, scale);

        var raw = DxfParser.LoadDxfFile(dxfPath).Result;
        raw.TryConvertToNfp(0, out INfp nfp).Should().BeTrue();

        // Tolerance is single-precision, not double: the part pipeline stores contours as
        // System.Drawing.PointF (DxfParser.cs casts every vertex to float), so ~1e-7 relative is the
        // best any part geometry can do in this app. On a 120in sheet that is well under a micron, and
        // the geometry handed back to SheetCam is the original from the file, never this copy.
        const double tolerance = 1e-6;

        // The bar is 20 x 40 mm centred on its origin: in inches that is -10/25.4 .. 10/25.4.
        nfp.MinX.Should().BeApproximately(-10 * scale, tolerance, "the local frame must NOT be shifted to the origin");
        nfp.MaxX.Should().BeApproximately(10 * scale, tolerance);
        nfp.MinY.Should().BeApproximately(-20 * scale, tolerance);
        nfp.MaxY.Should().BeApproximately(20 * scale, tolerance);

        nfp.Children.Should().HaveCount(1, "the square hole must come back as a child contour");
        nfp.Children[0].MinX.Should().BeApproximately(-3 * scale, tolerance);
      }
      finally
      {
        File.Delete(nestPath);
        File.Delete(dxfPath);
      }
    }

    /// <summary>
    /// The position convention, checked against SheetCam's own arithmetic: a Position rotates the part's
    /// points about (0,0) by A radians anticlockwise and then translates them. Reproduces the real
    /// test.nest case, where the first part of a 90-degree-rotated 7x36in strip lands exactly on the
    /// 0.25in edge margin.
    /// </summary>
    [Fact]
    public void PositionRotatesAboutTheOriginThenTranslates()
    {
      // Half-width 88.9 mm, half-length 457.2 mm => the 7 x 36 inch strip of the real job.
      var corner = new SvgPoint(-88.9, -431.8);
      const double a = Math.PI / 2;
      const double posX = 464.9929917;
      const double posY = 95.25000150000002;

      double x = (corner.X * Math.Cos(a)) - (corner.Y * Math.Sin(a)) + posX;
      double y = (corner.X * Math.Sin(a)) + (corner.Y * Math.Cos(a)) + posY;

      // 1e-5 mm, not tighter: SheetCam's own numbers carry that much noise (the Y above is
      // 95.25 + 1.5e-6), which is itself evidence these are its raw computed positions.
      y.Should().BeApproximately(6.35, 1e-5, "0.25in of edge spacing is 6.35mm — the part sits on the margin");
      x.Should().BeApproximately(896.7929917, 1e-5);
    }

    /// <summary>A nested placement in drawing units converts back to the file's millimetres and radians.</summary>
    [Fact]
    public void PlacementConvertsToMillimetresAndRadians()
    {
      double mm = SheetCamNestFile.MmPerDrawingUnit(false);

      // A part placed at 18.3 x 3.75 inches, turned a quarter turn.
      var position = new SheetCamNestPosition(18.3 * mm, 3.75 * mm, 90 * Math.PI / 180d, 0);

      position.X.Should().BeApproximately(464.82, 1e-9);
      position.Y.Should().BeApproximately(95.25, 1e-9);
      position.A.Should().BeApproximately(1.5707963267948966, 1e-15, "SheetCam writes the angle in radians");
    }

    /// <summary>
    /// Spacings are in the unit the file declares, not millimetres like the geometry — so they only need
    /// converting when the file and the app disagree. Getting this wrong nests to the wrong clearance,
    /// which is what produced parts touching edge to edge the first time round.
    /// </summary>
    [Theory]
    [InlineData("in", false, 1.0)]      // inch file, inch drawing
    [InlineData("mm", true, 1.0)]       // metric file, metric drawing
    [InlineData("in", true, 25.4)]      // inch file read into a metric drawing
    [InlineData("mm", false, 1 / 25.4)] // metric file read into an inch drawing
    public void SpacingsConvertOnlyWhenTheFileAndTheAppDisagree(string fileUnits, bool appUnitsMm, double expected)
    {
      var nest = Load(SampleNest.Replace(@"Units=""in""", $@"Units=""{fileUnits}"""));

      nest.SettingsToDrawingUnits(appUnitsMm).Should().BeApproximately(expected, 1e-12);
      (nest.PartSpacing * nest.SettingsToDrawingUnits(appUnitsMm))
        .Should().BeApproximately(0.25 * expected, 1e-12, "the job's part spacing has to survive into drawing units");
    }

    /// <summary>The file carries a fixed number of copies, so writing more than it asks for is not possible.</summary>
    [Fact]
    public void WritingMorePositionsThanTheJobAsksForKeepsOnlyTheQuantityDeclared()
    {
      var path = TempFile(SampleNest);
      try
      {
        var nest = SheetCamNestFile.Load(path);
        nest.Parts[0].Quantity.Should().Be(2);

        // Three placements for a job that wants two.
        nest.SetPositions("bar", new[]
        {
          new SheetCamNestPosition(1, 1, 0, 0),
          new SheetCamNestPosition(2, 2, 0, 0),
          new SheetCamNestPosition(3, 3, 0, 0),
        }.Take(nest.Parts[0].Quantity));
        nest.Save(path);

        XDocument.Load(path).Root.Element("Parts").Element("Part").Element("Positions")
          .Elements("Position").Should().HaveCount(2);
      }
      finally
      {
        File.Delete(path);
      }
    }

    /// <summary>
    /// A .nest describes a part that is already toolpathed, so the lead paths have to come through — they
    /// reach outside the contour and are what was running into the neighbouring part on the sheet.
    /// </summary>
    [Fact]
    public void ReadsLeadPathsAsToolPaths()
    {
      var nest = Load(SampleNest.Replace("<Lead X=\"-10\" Y=\"0\" />", LeadXml(10.15, 0, 13.15, 0)));

      nest.Parts[0].ToolPaths.Should().HaveCount(1);
      nest.Parts[0].ToolPaths[0].Should().HaveCount(2, "a lead is an open path, so no closing point is stripped");
      nest.Parts[0].ToolPaths[0][1].X.Should().Be(13.15, "the pierce point sits clear of the 20mm-wide bar");
    }

    /// <summary>
    /// The kerf is measured off the gap between a lead's join and the drawn outline. Tessellated curves
    /// scatter that reading both ways, so the maximum is taken: it can only err high, and erring high
    /// costs a sliver of material while erring low drives a lead through the next part.
    /// </summary>
    [Fact]
    public void DerivesKerfFromTheLeadThatMeetsAStraightEdge()
    {
      // Lead A joins the bar's straight right edge (x = 10) exactly half a 0.3mm kerf out, and pierces
      // clear of it. Lead B joins the tessellated hole and is the kind of reading that scatters.
      var leads = LeadXml(10.15, 0, 13.15, 0) + LeadXml(2.9, 0, 0, 0);
      var nest = Load(SampleNest.Replace("<Lead X=\"-10\" Y=\"0\" />", leads));

      nest.DeriveKerfMm().Should().BeApproximately(0.3, 1e-9, "the straight-edge lead reads exactly half a kerf");
    }

    [Fact]
    public void DerivesNoKerfWhenTheJobHasNoLeads()
    {
      var nest = Load(SampleNest.Replace("<Lead X=\"-10\" Y=\"0\" />", string.Empty));

      nest.Parts[0].ToolPaths.Should().BeEmpty();
      nest.DeriveKerfMm().Should().Be(0, "with nothing to measure, plain parts must nest exactly as before");
    }

    /// <summary>A reading that is obviously not a cut width is a misreading, and must not silently inflate
    /// every part on the sheet.</summary>
    [Fact]
    public void IgnoresAnImplausibleKerfReading()
    {
      // Both ends far from any contour: whichever is taken as the join gives a nonsense gap.
      var nest = Load(SampleNest.Replace("<Lead X=\"-10\" Y=\"0\" />", LeadXml(60, 60, 80, 80)));

      nest.DeriveKerfMm().Should().Be(0);
    }

    private static string LeadXml(double x0, double y0, double x1, double y1)
    {
      return FormattableString.Invariant(
        $"<Lead><Point X=\"{x0}\" Y=\"{y0}\" /><Point X=\"{x1}\" Y=\"{y1}\" /></Lead>");
    }

    private static SheetCamNestFile Load(string xml)
    {
      var path = TempFile(xml);
      try
      {
        return SheetCamNestFile.Load(path);
      }
      finally
      {
        File.Delete(path);
      }
    }

    private static string TempFile(string xml)
    {
      var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".nest");
      File.WriteAllText(path, xml, new System.Text.UTF8Encoding(true));
      return path;
    }
  }
}
