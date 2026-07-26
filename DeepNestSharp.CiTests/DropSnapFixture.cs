namespace DeepNestSharp.CiTests
{
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Where a dropped part settles: "moviéndolo con el mouse debería detectar el margen de .5 pulgadas y hacer
  /// el snap automático al part spacing o sheet spacing".
  /// <para>An arrow press gives the magnet a direction to travel along; a drag does not, so the drop searches
  /// outward over the four axes and the smallest correction wins. The predicates below stand in for the
  /// geometry: each says how far the part may move from where it was released before it is in something.</para>
  /// </summary>
  public class DropSnapFixture
  {
    /// <summary>Half an inch, the magnet's reach — see <see cref="DxfViewer.SnapZone"/>.</summary>
    private const double Reach = 0.5;

    private const double Precision = 1e-6;

    /// <summary>Dropped free with a neighbour 0.1 to its right and another 0.45 below: it settles against the
    /// NEARER one, and only that one — a drop settles against one thing, not into a corner.</summary>
    [Fact]
    public void AFreeDropSettlesAgainstWhateverIsNearest()
    {
      DxfViewer.TrySnapToNearest((ox, oy) => ox <= 0.1 && oy >= -0.45, Reach, out double sx, out double sy)
        .Should().BeTrue();

      sx.Should().BeApproximately(0.1, Precision);
      sy.Should().Be(0);
    }

    /// <summary>Open sheet: the magnet must leave the drop exactly where the operator released it. A drop that
    /// drifts on its own is worse than no magnet at all.</summary>
    [Fact]
    public void ADropWithNothingWithinReachDoesNotMove()
    {
      DxfViewer.TrySnapToNearest((_, _) => true, Reach, out double sx, out double sy).Should().BeFalse();

      sx.Should().Be(0);
      sy.Should().Be(0);
    }

    /// <summary>
    /// Already resting against its right-hand neighbour. Without this rule the part would go looking for the
    /// next nearest thing and slide off on its own — here, all the way left to the far wall.
    /// </summary>
    [Fact]
    public void ADropAlreadyRestingAgainstSomethingIsLeftAlone()
    {
      DxfViewer.TrySnapToNearest((ox, oy) => ox <= 0 && ox >= -0.4, Reach, out double sx, out double sy)
        .Should().BeFalse();

      sx.Should().Be(0);
      sy.Should().Be(0);
    }

    /// <summary>Dropped slightly into a neighbour: pushed straight back out, and it lands ON the clearance
    /// rather than somewhere merely legal.</summary>
    [Fact]
    public void AShallowOverlapIsPushedOutToTheClearance()
    {
      DxfViewer.TrySnapToNearest((ox, oy) => oy >= 0.2, Reach, out double sx, out double sy).Should().BeTrue();

      sx.Should().Be(0);
      sy.Should().BeApproximately(0.2, Precision);
    }

    /// <summary>With more than one way out, the part takes the shortest — 0.15 to the left beats 0.3 up.</summary>
    [Fact]
    public void TheSmallestWayOutWins()
    {
      DxfViewer.TrySnapToNearest((ox, oy) => ox <= -0.15 || oy >= 0.3, Reach, out double sx, out double sy)
        .Should().BeTrue();

      sx.Should().BeApproximately(-0.15, Precision);
      sy.Should().Be(0);
    }

    /// <summary>
    /// Dropped squarely on top of a neighbour, too deep to escape within reach: it stays exactly where it was
    /// dropped and goes red. This is the test that protects what makes rearranging a full sheet possible —
    /// parking a part on an occupied spot while its neighbour is moved out of the way.
    /// </summary>
    [Fact]
    public void ADeepOverlapStaysWhereItWasDropped()
    {
      DxfViewer.TrySnapToNearest((_, _) => false, Reach, out double sx, out double sy).Should().BeFalse();

      sx.Should().Be(0);
      sy.Should().Be(0);
    }
  }
}
