namespace DeepNestSharp.CiTests
{
  using System.Collections.Generic;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestSharp.Ui.Views;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The arithmetic that charges a finished nest to the stock in the Sheets tab, and hands it back when
  /// the result is discarded.
  /// <para>It has a fixture at all because it broke twice in one afternoon while it lived inside the NEST
  /// button's click handler, where the suite could not reach it: first the Available badge read "0 used"
  /// after a nest, then the give-back turned out to have a second route in. Both were found by Manuel
  /// looking at the screen. <see cref="MainWindow"/> is a WPF Window and cannot be instantiated here, but
  /// these are static, which is the same trick <c>CommonCuttingOf</c> and <c>DxfViewer.CountUnfit</c>
  /// already use.</para>
  /// </summary>
  public class SheetStockAccountingFixture
  {
    [Fact]
    public void ACountedRowLosesExactlyWhatTheNestUsed()
    {
      var rows = new List<ISheetLoadInfo> { new SheetLoadInfo(120, 60, 5) };

      var consumed = MainWindow.ConsumeStock(rows, Used((120, 60, 3)));

      rows[0].Quantity.Should().Be(2, "five in stock, three cut");
      consumed[(120, 60)].Should().Be(3);
    }

    /// <summary>The nest can use more sheets of a size than the row claims to have, because a second row
    /// of the same size supplies the rest. Stock still cannot go negative.</summary>
    [Fact]
    public void ACountedRowNeverGoesBelowZero()
    {
      var rows = new List<ISheetLoadInfo> { new SheetLoadInfo(120, 60, 2) };

      var consumed = MainWindow.ConsumeStock(rows, Used((120, 60, 5)));

      rows[0].Quantity.Should().Be(0);
      consumed[(120, 60)].Should().Be(2, "it can only give up what it had");
    }

    /// <summary>
    /// The one that was wrong on screen. An unlimited size is not charged, but it still has to report what
    /// the nest took, because that report IS the badge. Leaving it out of the record to protect the
    /// give-back is exactly the mistake that made it read "0 used".
    /// </summary>
    [Fact]
    public void AnUnlimitedRowReportsWhatItUsedAndKeepsItsQuantity()
    {
      var rows = new List<ISheetLoadInfo> { new SheetLoadInfo(120, 60, 4) { Unlimited = true } };

      var consumed = MainWindow.ConsumeStock(rows, Used((120, 60, 3)));

      rows[0].Quantity.Should().Be(4, "an unlimited size is not a stock level and must not move");
      consumed.Should().ContainKey((120, 60));
      consumed[(120, 60)].Should().Be(3, "the badge has nothing else to count");
    }

    [Fact]
    public void TwoRowsOfTheSameSizeShareWhatWasUsed()
    {
      var rows = new List<ISheetLoadInfo>
      {
        new SheetLoadInfo(120, 60, 2),
        new SheetLoadInfo(120, 60, 5),
      };

      var consumed = MainWindow.ConsumeStock(rows, Used((120, 60, 4)));

      rows[0].Quantity.Should().Be(0, "the first row is emptied first");
      rows[1].Quantity.Should().Be(3, "the second covers the remaining two, not all four");
      consumed[(120, 60)].Should().Be(4);
    }

    /// <summary>The caller's tally is theirs: charging the stock must not empty the dictionary it was
    /// handed, or the next thing to read it sees a nest that used nothing.</summary>
    [Fact]
    public void ChargingTheStockLeavesTheCallersTallyAlone()
    {
      var rows = new List<ISheetLoadInfo> { new SheetLoadInfo(120, 60, 5) };
      var used = Used((120, 60, 3));

      MainWindow.ConsumeStock(rows, used);

      used[(120, 60)].Should().Be(3);
    }

    [Fact]
    public void ClearResultGivesBackWhatACountedRowLost()
    {
      var rows = new List<ISheetLoadInfo> { new SheetLoadInfo(120, 60, 5) };
      var consumed = MainWindow.ConsumeStock(rows, Used((120, 60, 3)));

      MainWindow.ReturnStock(rows, consumed);

      rows[0].Quantity.Should().Be(5, "discarding the result puts the sheets back on the rack");
    }

    /// <summary>The guard against inventing stock. Nothing was taken from an unlimited size, so nothing
    /// may be credited to it however the consumed record was arrived at.</summary>
    [Fact]
    public void ClearResultCreditsAnUnlimitedRowNothing()
    {
      var rows = new List<ISheetLoadInfo> { new SheetLoadInfo(120, 60, 4) { Unlimited = true } };

      // Straight from the record, the way reopening a saved project rebuilds it from the result itself.
      MainWindow.ReturnStock(rows, Used((120, 60, 7)));

      rows[0].Quantity.Should().Be(4);
    }

    /// <summary>A row deleted after nesting comes back, so the operator does not silently lose stock.</summary>
    [Fact]
    public void ClearResultBringsBackASizeWhoseRowWasDeleted()
    {
      var rows = new List<ISheetLoadInfo>();

      MainWindow.ReturnStock(rows, Used((120, 60, 3)));

      rows.Should().HaveCount(1);
      rows[0].Width.Should().Be(120);
      rows[0].Quantity.Should().Be(3);
    }

    private static Dictionary<(int W, int H), int> Used(params (int W, int H, int Count)[] entries)
    {
      var used = new Dictionary<(int W, int H), int>();
      foreach (var e in entries)
      {
        used[(e.W, e.H)] = e.Count;
      }

      return used;
    }
  }
}
