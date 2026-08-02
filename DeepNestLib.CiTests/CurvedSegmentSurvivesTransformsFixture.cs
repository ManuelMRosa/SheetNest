namespace DeepNestLib.CiTests
{
  using System.Linq;
  using DeepNestLib;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The mark has to reach the nester intact. The path a part actually takes is
  /// ToNfp -> (MirrorX) -> Rotate -> Shift, so it is those that must not lose it, and the mutations
  /// that rebuild a ring that must drop it rather than leave it describing the wrong segments.
  /// </summary>
  public class CurvedSegmentSurvivesTransformsFixture
  {
    private static readonly bool[] Marks = { false, false, true, true, false, false, true, true };

    [Fact]
    public void RotateMirrorAndShiftAllKeepIt()
    {
      var poly = Octagon();

      var moved = poly.Rotate(37).MirrorX().Shift(3, -4);

      Flags(moved).Should().Equal(Marks, "those three move every point and reorder none");
    }

    /// <summary>Each on its own, so a failure says which one.</summary>
    [Fact]
    public void EachTransformKeepsItOnItsOwn()
    {
      Flags(Octagon().Rotate(37)).Should().Equal(Marks);
      Flags(Octagon().MirrorX()).Should().Equal(Marks);
      Flags(Octagon().Shift(3, -4)).Should().Equal(Marks);
    }

    /// <summary>A clone is the same ring, so it keeps the same segments.</summary>
    [Fact]
    public void CloningKeepsIt()
    {
      Flags(Octagon().Clone()).Should().Equal(Marks);
      Flags(Octagon().CloneExact()).Should().Equal(Marks);
    }

    private static bool[] Flags(INfp poly) => ((NoFitPolygon)poly).CurvedSegments;

    /// <summary>
    /// A ring rebuilt to a different length can no longer be described by the old flags, and guessing
    /// would offer a chord as a real edge. It gives up instead: everything is called a curve, so the
    /// polygon simply stops contributing edges.
    /// </summary>
    [Fact]
    public void RebuildingTheRingGivesUpTheProvenanceRatherThanMisplaceIt()
    {
      var poly = Octagon();
      poly.ReplacePoints(poly.Points.Take(5).ToArray());

      Flags(poly).Should().OnlyContain(c => c);
      Flags(poly).Should().HaveCount(5);
    }

    [Fact]
    public void AddingAPointAlsoGivesItUp()
    {
      var poly = Octagon();
      poly.AddPoint(new SvgPoint(99, 99));

      Flags(poly).Should().OnlyContain(c => c);
    }

    /// <summary>
    /// A polygon nobody recorded provenance for stays null, which reads as "no segment is a chord".
    /// That is every polygon that did not come from the DXF parser, and their behaviour is unchanged.
    /// </summary>
    [Fact]
    public void APolygonWithNoProvenanceKeepsNone()
    {
      var plain = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0), new SvgPoint(10, 0), new SvgPoint(10, 10), new SvgPoint(0, 10),
      });

      Flags(plain).Should().BeNull();
      Flags(plain.Rotate(15)).Should().BeNull();
      Flags(plain.Shift(1, 1)).Should().BeNull();
    }

    private static INfp Octagon()
    {
      var poly = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0), new SvgPoint(4, 0), new SvgPoint(6, 2), new SvgPoint(6, 4),
        new SvgPoint(4, 6), new SvgPoint(0, 6), new SvgPoint(-2, 4), new SvgPoint(-2, 2),
      });
      poly.CurvedSegments = (bool[])Marks.Clone();
      return poly;
    }
  }
}
