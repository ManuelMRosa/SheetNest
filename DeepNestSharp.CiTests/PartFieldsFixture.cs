namespace DeepNestSharp.CiTests
{
  using System.Globalization;
  using System.Threading;
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The X, Y and angle boxes in the edit panel: when a typed number counts as an edit, and what counts
  /// as a number at all.
  /// </summary>
  public class PartFieldsFixture
  {
    /// <summary>
    /// The one that moved parts nobody meant to move. The box shows a rounded number and the placement
    /// holds the exact one, so comparing the text to the placement made an untouched field look edited:
    /// clicking into a box to read it and clicking away nudged the part by whatever the display had
    /// rounded off, pushed an undo step, and marked the project dirty.
    /// </summary>
    [Fact]
    public void AFieldNobodyTypedInIsNotAnEdit()
    {
      const double exact = 4.7503218;

      DxfViewer.WasEdited(DxfViewer.Shown(exact, DxfViewer.PositionFormat), exact, DxfViewer.PositionFormat)
        .Should().BeFalse("that is the very text the box was given");
    }

    /// <summary>The same for the angle, which is shown to fewer places and REPLACES the polygon when it
    /// is applied, so a phantom edit there rebuilds the part's geometry for nothing.</summary>
    [Fact]
    public void AnUntouchedAngleIsNotAnEdit()
    {
      const double exact = 45.678;

      DxfViewer.WasEdited(DxfViewer.Shown(exact, DxfViewer.AngleFormat), exact, DxfViewer.AngleFormat)
        .Should().BeFalse();
    }

    /// <summary>And it still notices a real one, including one finer than the box was showing.</summary>
    [Fact]
    public void TypingSomethingElseIsAnEdit()
    {
      DxfViewer.WasEdited("50", 10, DxfViewer.PositionFormat).Should().BeTrue();
      DxfViewer.WasEdited("4.7503", 4.75, DxfViewer.PositionFormat)
        .Should().BeTrue("asking for more precision than the box showed is still asking");
    }

    /// <summary>Surrounding space is not an edit.</summary>
    [Fact]
    public void SpaceAroundTheNumberIsNotAnEdit()
    {
      DxfViewer.WasEdited("  10  ", 10, DxfViewer.PositionFormat).Should().BeFalse();
    }

    /// <summary>
    /// A point is how every CAD tool writes a decimal, and it used to be thrown away without a word on a
    /// machine set to a comma: the field reverted, nothing was said, and the operator walked away
    /// believing the part had been placed.
    /// </summary>
    [Fact]
    public void APointIsReadOnACommaDecimalMachine()
    {
      var was = Thread.CurrentThread.CurrentCulture;
      try
      {
        Thread.CurrentThread.CurrentCulture = new CultureInfo("es-ES");

        DxfViewer.TryParseTyped("12.5", out double point).Should().BeTrue();
        point.Should().Be(12.5);

        DxfViewer.TryParseTyped("12,5", out double comma).Should().BeTrue("the local separator still works");
        comma.Should().Be(12.5);
      }
      finally
      {
        Thread.CurrentThread.CurrentCulture = was;
      }
    }

    /// <summary>And on a point-decimal machine, unchanged.</summary>
    [Fact]
    public void APointIsReadOnAPointDecimalMachine()
    {
      var was = Thread.CurrentThread.CurrentCulture;
      try
      {
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");

        DxfViewer.TryParseTyped("12.5", out double value).Should().BeTrue();
        value.Should().Be(12.5);
      }
      finally
      {
        Thread.CurrentThread.CurrentCulture = was;
      }
    }

    /// <summary>Something that is not a number is reported as one it cannot read, not silently taken as
    /// zero, which would send the part to the corner of the sheet.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1..5")]
    public void WhatIsNotANumberIsRefused(string text)
    {
      DxfViewer.TryParseTyped(text, out _).Should().BeFalse();
    }
  }
}
