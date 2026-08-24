namespace DeepNestSharp.CiTests
{
  using System;
  using System.IO;
  using System.Linq;
  using DeepNestLib.IO;
  using DeepNestSharp.Ui.Views;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// Saying out loud when a job is not set up for common-line cutting.
  /// <para>
  /// A real job arrived stating a part spacing of 0.04in while its owner was trying to get common-line
  /// cutting working, and nothing anywhere said that a stated spacing is exactly what turns it OFF: a
  /// positive value imports as <see cref="DeepNestLib.NestProject.CommonCuttingMode.None"/> and not one
  /// cut is ever shared. The import summary repeated the number and the job read as ordinary, so the
  /// setting that decided everything was the one thing never mentioned.
  /// </para>
  /// </summary>
  public class CommonLineNoticeFixture
  {
    private readonly ITestOutputHelper output;

    public CommonLineNoticeFixture(ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData(0.0402, true)]  // what the shop's job actually stated
    [InlineData(0.25, true)]
    [InlineData(0.0, false)]    // SheetCam's flag for "these parts share their cuts"
    public void AStatedPartSpacingIsWhatTurnsCommonLineOff(double spacing, bool expectWarning)
    {
      string notice = MainWindow.CommonLineNotice(spacing, "in", totalPieces: 26);
      this.output.WriteLine($"spacing {spacing}: \"{notice.Trim()}\"");

      notice.Contains("NOT set up for common-line", StringComparison.Ordinal).Should().Be(
        expectWarning,
        "a spacing of 0 is how SheetCam says the parts share their cuts, and any other value says they do not");
    }

    /// <summary>
    /// A spacing that is not zero but might as well be. One of the shop's own jobs states 0.0003mm,
    /// which is thirty times finer than the machine can position and a thousandth of the cut, so nobody
    /// asked for it - it is a zero that did not survive a unit conversion. Common line is off all the
    /// same, because the flag is exact.
    /// </summary>
    /// <remarks>
    /// This is also what caught a defect in the warning itself: at three decimals 0.0003mm prints as
    /// "0.000", so the sentence read "NOT set up for common-line cutting: it states a part spacing of
    /// 0.000 in" and contradicted itself. The number has to survive being shown.
    /// </remarks>
    [Fact]
    public void ASpacingTooSmallToBeAGapSaysSo()
    {
      const double Kerf = 0.0134;      // the shop's measured cut, in inches
      const double Spacing = 0.0000118; // 0.0003mm, straight out of test2.nest

      string notice = MainWindow.CommonLineNotice(Spacing, "in", totalPieces: 26, kerf: Kerf);
      this.output.WriteLine(notice.Trim());

      notice.Contains("NOT set up for common-line", StringComparison.Ordinal).Should().BeTrue(
        "the flag is exact, so anything above zero turns common line off");
      notice.Contains("0.000 in", StringComparison.Ordinal).Should().BeFalse(
        "printing it as zero makes the warning contradict itself");
      notice.Contains("probably meant to be 0", StringComparison.Ordinal).Should().BeTrue(
        "a gap a thousandth of the cut is not one anybody asked for, and saying so is the difference "
        + "between a useful warning and a baffling one");

      // And the control: a real gap gets no such excuse made for it.
      MainWindow.CommonLineNotice(0.25, "in", 26, Kerf)
        .Contains("probably meant to be 0", StringComparison.Ordinal).Should().BeFalse(
          "a quarter inch is a gap somebody chose");
    }

    /// <summary>
    /// The other end of the same silence: the job was set up for common line and then asked for one
    /// piece. There is nobody to share with, and the layout looks perfectly normal.
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(26, false)]
    public void OnePieceHasNobodyToShareWith(int pieces, bool expectWarning)
    {
      string notice = MainWindow.CommonLineNotice(0, "in", pieces);
      this.output.WriteLine($"{pieces} piece(s): \"{notice.Trim()}\"");

      notice.Contains("nothing for a shared cut to join", StringComparison.Ordinal).Should().Be(expectWarning);
    }

    /// <summary>
    /// Against the real files, so the wording is judged on what SheetCam actually writes rather than on
    /// numbers picked to suit it. Both jobs exist and they disagree, which is the whole point.
    /// </summary>
    [Fact]
    public void TheShopsOwnJobsLandOnOppositeSidesOfIt()
    {
      string dir = Environment.GetEnvironmentVariable("SHEETNEST_NEST_CORPUS");
      if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
      {
        dir = @"C:\Users\rosam\Documents\SheetCam";
      }

      if (!Directory.Exists(dir))
      {
        this.output.WriteLine("NOTICE: no corpus - skipping.");
        return;
      }

      bool sawOn = false, sawOff = false;
      foreach (string path in Directory.GetFiles(dir, "*.nest").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
      {
        SheetCamNestFile nest;
        try
        {
          nest = SheetCamNestFile.Load(path);
        }
        catch (InvalidDataException)
        {
          continue;
        }

        double spacing = nest.PartSpacing * nest.SettingsToDrawingUnits(false);
        string notice = MainWindow.CommonLineNotice(spacing, "in", nest.Parts.Sum(p => p.Quantity));
        bool warned = notice.Contains("NOT set up for common-line", StringComparison.Ordinal);
        this.output.WriteLine(FormattableString.Invariant(
          $"NOTICE: {Path.GetFileName(path)} spacing {spacing:0.0000000} -> {(warned ? "warned" : "silent")}"));

        warned.Should().Be(spacing > 0, "the warning has to follow the file's own spacing and nothing else");
        sawOn |= !warned;
        sawOff |= warned;
      }

      sawOn.Should().BeTrue("the corpus has a job with spacing 0, or this proves nothing about the quiet case");
      sawOff.Should().BeTrue("and one with a stated spacing, or it proves nothing about the loud one");
    }
  }
}
