namespace DeepNestSharp.CiTests
{
  using DeepNestLib.NestProject;
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Hand editing has to judge a pair the same way the nester did, or the operator opens a nest the engine
  /// built correctly and finds it painted red with the exports refused.
  /// <para>
  /// This is the half that used to be missing. A common-line part reached the viewer with its spacing
  /// already flattened to 0, so "may these touch" never had to be asked. Now the part keeps its real
  /// spacing and the mode answers instead, which means the viewer needs the mode too.
  /// </para>
  /// </summary>
  public class CommonCuttingClearanceFixture
  {
    private const string A = @"C:\parts\bracket.dxf";
    private const string B = @"C:\parts\plate.dxf";

    [Fact]
    public void UnrestrictedSharesWithAnyOtherSharingPart()
    {
      DxfViewer.MayShare(CommonCuttingMode.Unrestricted, A, false, CommonCuttingMode.Unrestricted, B, false)
        .Should().BeTrue("unrestricted means any part that also allows it");
    }

    [Fact]
    public void NothingSharesWithAPartSetToNone()
    {
      DxfViewer.MayShare(CommonCuttingMode.Unrestricted, A, false, CommonCuttingMode.None, A, false)
        .Should().BeFalse("a part set to None keeps its clearance even from its own copies");
    }

    [Fact]
    public void SamePartSharesOnlyWithItsOwnDrawing()
    {
      DxfViewer.MayShare(CommonCuttingMode.SamePart, A, false, CommonCuttingMode.SamePart, A, false)
        .Should().BeTrue();
      DxfViewer.MayShare(CommonCuttingMode.SamePart, A, false, CommonCuttingMode.SamePart, B, false)
        .Should().BeFalse("a different drawing is a different part");
    }

    /// <summary>The stricter mode wins, whichever side it is on. Unrestricted does not get to drag a
    /// Same-part neighbour into a shared cut just by being permissive.</summary>
    [Fact]
    public void TheStricterModeDecidesWhicheverSideItIsOn()
    {
      DxfViewer.MayShare(CommonCuttingMode.Unrestricted, A, false, CommonCuttingMode.SamePart, B, false)
        .Should().BeFalse();
      DxfViewer.MayShare(CommonCuttingMode.SamePart, B, false, CommonCuttingMode.Unrestricted, A, false)
        .Should().BeFalse("and it must not depend on the argument order");
    }

    /// <summary>A mirrored copy is the other hand: same file, different part. Matches how the nester keys
    /// its share groups, on (path, mirrored).</summary>
    [Fact]
    public void AMirroredCopyIsADifferentPart()
    {
      DxfViewer.MayShare(CommonCuttingMode.SamePart, A, false, CommonCuttingMode.SamePart, A, true)
        .Should().BeFalse();
      DxfViewer.MayShare(CommonCuttingMode.SamePart, A, true, CommonCuttingMode.SamePart, A, true)
        .Should().BeTrue("two mirrored copies are each other's own kind");
    }

    [Fact]
    public void PathsAreComparedCaseInsensitively()
    {
      DxfViewer.MayShare(CommonCuttingMode.SamePart, @"C:\Parts\Bracket.DXF", false, CommonCuttingMode.SamePart, A, false)
        .Should().BeTrue("Windows paths are the same file in either case");
    }

    /// <summary>
    /// The wiring, and the reason this commit cannot ship apart from the one before it. Two copies the
    /// nester deliberately placed touching must be asked for NO clearance; the same two parts without the
    /// mode must be asked for the full 0.25, which is what would have marked the whole layout red.
    /// </summary>
    [Fact]
    public void SharingPairsAreAskedForNoClearanceAndEveryoneElseForTheFullGap()
    {
      DxfViewer.ClearanceFor(0.25, 0.25, mayShare: true).Should().Be(0);
      DxfViewer.ClearanceFor(0.25, 0.25, mayShare: false).Should().BeApproximately(0.25, 1e-9);
      DxfViewer.ClearanceFor(0.5, 0.1, mayShare: false).Should().BeApproximately(0.3, 1e-9, "still the average of the two");
    }
  }
}
