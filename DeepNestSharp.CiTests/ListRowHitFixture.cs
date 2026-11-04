namespace DeepNestSharp.CiTests
{
  using System;
  using System.Runtime.ExceptionServices;
  using System.Threading;
  using System.Windows;
  using System.Windows.Controls;
  using System.Windows.Documents;
  using DeepNestSharp.Ui.Views;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Issue #16: a double-click on the width of a stock row closed the program. The width is a Run inside a
  /// TextBlock, the click lands on the Run, and the walk up to the row asked the visual tree for the Run's parent,
  /// which it refuses for anything that is not drawn by itself. The parts list's size column is built the same way.
  /// </summary>
  public class ListRowHitFixture
  {
    [Fact]
    public void ADoubleClickOnTheTextOfACellFindsItsRow()
    {
      OnSta(() =>
      {
        var run = new Run("120");
        var item = Row(new TextBlock(run));

        MainWindow.RowHit(run).Should().BeSameAs(item);
      });
    }

    [Fact]
    public void ADoubleClickOnTheTextOfAButtonInTheRowIsNotARowHit()
    {
      OnSta(() =>
      {
        var run = new Run("x");
        var text = new TextBlock(run);
        var button = new Button { Content = text };
        var item = Row(button);
        item.IsAncestorOf(button).Should().BeTrue("the walk must reach the button to stop at it");
        button.IsAncestorOf(text).Should().BeTrue();

        MainWindow.RowHit(run).Should().BeNull();
      });
    }

    /// <summary>A list row holding <paramref name="content"/>, laid out so its template builds the visual tree.</summary>
    private static ListViewItem Row(UIElement content)
    {
      var item = new ListViewItem { Content = content };
      item.Measure(new Size(200, 40));
      item.Arrange(new Rect(0, 0, 200, 40));
      return item;
    }

    private static void OnSta(Action test)
    {
      ExceptionDispatchInfo? failure = null;
      var thread = new Thread(() =>
      {
        try
        {
          test();
        }
        catch (Exception ex)
        {
          failure = ExceptionDispatchInfo.Capture(ex);
        }
      });
      thread.SetApartmentState(ApartmentState.STA);
      thread.Start();
      thread.Join();
      failure?.Throw();
    }
  }
}
