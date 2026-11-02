namespace DeepNestLib.CiTests.Placement
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib.Placement;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// A placement keeps its shifted outline and hands the same one back until something it was built from
  /// changes: its position, or the points of the part or of any contour inside it. What it hands back must be,
  /// to the bit, what shifting the part builds, since everything downstream measures from it.
  /// </summary>
  public class PartPlacementPlacedPartFixture
  {
    [Fact]
    public void TwoReadsAtTheSamePlaceAreOneOutlineEqualToTheShift()
    {
      var placement = new PartPlacement(Frame()) { X = 3.1, Y = -7.25 };

      INfp first = placement.PlacedPart;
      INfp second = placement.PlacedPart;

      second.Should().BeSameAs(first, "nothing changed between the two reads");
      SameBits(first, placement.Part.Shift(3.1, -7.25));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMoveIsSeenOnTheNextRead(bool alongX)
    {
      var placement = new PartPlacement(Frame()) { X = 1, Y = 2 };
      INfp before = placement.PlacedPart;

      if (alongX)
      {
        placement.X += 0.000001;
      }
      else
      {
        placement.Y -= 0.5;
      }

      INfp after = placement.PlacedPart;
      after.Should().NotBeSameAs(before);
      SameBits(after, placement.Part.Shift(placement.X, placement.Y));
    }

    [Fact]
    public void NewPointsInThePartAreSeenOnTheNextRead()
    {
      var placement = new PartPlacement(Frame()) { X = 1, Y = 1 };
      _ = placement.PlacedPart;

      placement.Part.ReplacePoints(Square(6).Points);

      SameBits(placement.PlacedPart, placement.Part.Shift(1, 1));
    }

    [Fact]
    public void AnAddedHoleIsSeenOnTheNextRead()
    {
      var placement = new PartPlacement(Square(20)) { X = 1, Y = 1 };
      _ = placement.PlacedPart;

      placement.Part.Children.Add(Square(4).Shift(5, 5));

      placement.PlacedPart.Children.Should().HaveCount(1);
      SameBits(placement.PlacedPart, placement.Part.Shift(1, 1));
    }

    [Fact]
    public void NewPointsInAHoleAreSeenOnTheNextRead()
    {
      var placement = new PartPlacement(Frame()) { X = 1, Y = 1 };
      _ = placement.PlacedPart;

      placement.Part.Children[0].ReplacePoints(Square(2).Shift(9, 9).Points);

      SameBits(placement.PlacedPart, placement.Part.Shift(1, 1));
    }

    [Fact]
    public void AnIslandInsideAHoleIsShiftedAndWatchedToo()
    {
      var part = Frame();
      part.Children[0].Children.Add(Square(1).Shift(10, 10));
      var placement = new PartPlacement(part) { X = 2, Y = 3 };
      _ = placement.PlacedPart;

      part.Children[0].Children[0].ReplacePoints(Square(1.5).Shift(10, 10).Points);

      SameBits(placement.PlacedPart, part.Shift(2, 3));
    }

    /// <summary>Same type, same points to the bit with the same flags, the same contours inside, all the way down.</summary>
    private static void SameBits(INfp actual, INfp expected)
    {
      actual.GetType().Should().Be(expected.GetType());
      actual.Points.Length.Should().Be(expected.Points.Length);
      for (int i = 0; i < expected.Points.Length; i++)
      {
        BitConverter.DoubleToInt64Bits(actual.Points[i].X).Should().Be(BitConverter.DoubleToInt64Bits(expected.Points[i].X));
        BitConverter.DoubleToInt64Bits(actual.Points[i].Y).Should().Be(BitConverter.DoubleToInt64Bits(expected.Points[i].Y));
        actual.Points[i].Exact.Should().Be(expected.Points[i].Exact);
        actual.Points[i].Marked.Should().Be(expected.Points[i].Marked);
      }

      actual.Rotation.Should().Be(expected.Rotation);
      actual.Children.Should().HaveCount(expected.Children.Count);
      for (int c = 0; c < expected.Children.Count; c++)
      {
        SameBits(actual.Children[c], expected.Children[c]);
      }
    }

    private static NoFitPolygon Square(double side)
      => new NoFitPolygon(new[] { new SvgPoint(0, 0), new SvgPoint(side, 0), new SvgPoint(side, side), new SvgPoint(0, side) });

    /// <summary>A 20 x 20 plate with a 10 x 10 window, the shape a placement usually carries a hole in.</summary>
    private static NoFitPolygon Frame()
    {
      var frame = Square(20);
      frame.Children.Add(Square(10).Shift(5, 5));
      return frame;
    }
  }
}
