namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using FluentAssertions.Execution;
  using Xunit;

  /// <summary>
  /// A shared cut that would close a loop is not part of the planner's tree: it is picked up afterwards,
  /// once the tree has pulled every part into place. Where the two faces meet has to be read from where
  /// the parts ended up, not from where they were before. A row of parts pulled together along one bar
  /// drifts along it by a different amount at each part, and a seam placed where the part used to be is
  /// refused, so the bar and the part were each cut on their own a gap apart.
  /// </summary>
  public sealed class DirectCommonLineCycleSpanFixture
  {
    private const double Spacing = 0.01D;
    private const double CommonLineGap = 0.01D;
    private const double GeometryTolerance = 0.0001D;
    private const double PostWidth = 1D;
    private const double PostHeight = 4D;

    /// <summary>The posts' top corners are cut off, as a shop's rounded corners are, so the seams between
    /// the posts end below the bar instead of running into its seam.</summary>
    private const double Chamfer = 0.2D;

    /// <summary>
    /// Uneven gaps between the posts, the way a nest leaves them. Pulling each gap to CommonLineGap moves
    /// every post along the bar by a different amount.
    /// </summary>
    private static readonly double[] PostGaps = { 0.0104D, 0.0103D, 0.0105D, 0.0104D, 0.0106D, 0.0104D, 0.0105D, 0.0103D };

    [Fact]
    public void EveryPostUnderTheBarSharesItsCut()
    {
      INfp post = Post();
      var placements = new List<IPartPlacement>();
      double x = 1D;
      placements.Add(PlaceAt(post, 0, x, 1D));
      foreach (double gap in PostGaps)
      {
        x += PostWidth + gap;
        placements.Add(PlaceAt(post, 0, x, 1D));
      }

      double barWidth = x + PostWidth - 1D;
      INfp bar = Rectangle(barWidth, 1D);
      IPartPlacement barPlacement = PlaceAt(bar, 1, 1D, 1D + PostHeight + CommonLineGap);
      placements.Add(barPlacement);

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        Rules(Spacing),
        Profiles(post, bar),
        sheetW: barWidth + 4D,
        sheetH: PostHeight + 4D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);

      var posts = placements.Where(p => !ReferenceEquals(p, barPlacement)).ToList();
      using (new AssertionScope())
      {
        foreach (IPartPlacement placedPost in posts)
        {
          SparrowNestService.CommonEdgeIntent seam = Seam(result, placedPost, barPlacement);
          seam.Should().NotBeNull($"the post at x={placedPost.MinX:F4} sits a gap under the bar, so it shares its cut");
          if (seam != null)
          {
            Length(Create(seam)).Should().BeApproximately(PostWidth - (2D * Chamfer), GeometryTolerance,
              "the whole flat top of the post is under the bar");
          }
        }

        for (int i = 1; i < posts.Count; i++)
        {
          SparrowNestService.CommonEdgeIntent seam = Seam(result, posts[i - 1], posts[i]);
          seam.Should().NotBeNull($"posts {i - 1} and {i} stand a gap apart, so they share their cut");
          if (seam != null)
          {
            Length(Create(seam)).Should().BeApproximately(PostHeight - Chamfer, GeometryTolerance,
              "the posts stand side by side along their whole straight side");
          }
        }
      }
    }

    private static SparrowNestService.CommonEdgeIntent Seam(
      SparrowNestService.DirectCommonLineSnapResult result,
      IPartPlacement first,
      IPartPlacement second)
      => result.Edges.SingleOrDefault(edge =>
        (ReferenceEquals(edge.PartA, first) && ReferenceEquals(edge.PartB, second))
        || (ReferenceEquals(edge.PartB, first) && ReferenceEquals(edge.PartA, second)));

    private static CommonEdge Create(SparrowNestService.CommonEdgeIntent intent)
      => CommonLineGeometryResolver.CreateCommonEdge(
        intent.PartA,
        intent.EdgeAIndex,
        intent.EdgeAStart,
        intent.EdgeAEnd,
        intent.PartB,
        intent.EdgeBIndex,
        intent.EdgeBStart,
        intent.EdgeBEnd,
        CommonLineGap,
        GeometryTolerance);

    private static double Length(CommonEdge edge)
    {
      double dx = edge.End.X - edge.Start.X;
      double dy = edge.End.Y - edge.Start.Y;
      return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> Rules(double spacing)
      => new Dictionary<int, (CommonCuttingMode, int, string, bool, double)>
      {
        [0] = (CommonCuttingMode.Unrestricted, 0, "direct", true, spacing),
        [1] = (CommonCuttingMode.Unrestricted, 0, "direct", true, spacing),
      };

    private static Dictionary<int, SparrowNestService.DirectOrientationProfile> Profiles(INfp post, INfp bar)
      => new Dictionary<int, SparrowNestService.DirectOrientationProfile>
      {
        [0] = new SparrowNestService.DirectOrientationProfile(post, new[] { 0 }),
        [1] = new SparrowNestService.DirectOrientationProfile(bar, new[] { 0 }),
      };

    /// <summary>A post whose top corners are cut off, as a shop's rounded corners are, so the seams between
    /// posts end below whatever sits on top instead of running into its seam.</summary>
    private static INfp Post()
      => new NoFitPolygon(new[]
      {
        new SvgPoint(0D, 0D),
        new SvgPoint(PostWidth, 0D),
        new SvgPoint(PostWidth, PostHeight - Chamfer),
        new SvgPoint(PostWidth - Chamfer, PostHeight),
        new SvgPoint(Chamfer, PostHeight),
        new SvgPoint(0D, PostHeight - Chamfer),
      });

    private static IPartPlacement PlaceAt(INfp part, int source, double minX, double minY)
      => new PartPlacement(part)
      {
        Source = source,
        Rotation = 0D,
        X = minX - part.MinX,
        Y = minY - part.MinY,
      };

    private static INfp Rectangle(double width, double height)
      => new NoFitPolygon(new[]
      {
        new SvgPoint(0D, 0D),
        new SvgPoint(width, 0D),
        new SvgPoint(width, height),
        new SvgPoint(0D, height),
      });
  }
}
