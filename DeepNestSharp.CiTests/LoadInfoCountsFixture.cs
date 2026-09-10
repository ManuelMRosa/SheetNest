namespace DeepNestSharp.CiTests
{
  using System.Collections.Generic;
  using DeepNestLib.NestProject;
  using DeepNestSharp.Domain.Models;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The counts a part card and a sheet card print. They are read at the machine to decide whether a job
  /// is finished, so each has to add up to what the nest really did, and each has to announce its change:
  /// a card that is not told keeps showing the count from before the nest.
  /// </summary>
  public class LoadInfoCountsFixture
  {
    [Fact]
    public void RequiredCountsTheQuantityTheSparesAndTheMirroredCopies()
    {
      var part = new ObservableDetailLoadInfo(new DetailLoadInfo { Path = "a.dxf", Quantity = 10, Extra = 2, MirrorQuantity = 3 });

      part.RequiredCount.Should().Be(15);
    }

    [Fact]
    public void RemainingIsWhatIsRequiredLessWhatTheNestPlaced()
    {
      var part = new ObservableDetailLoadInfo(new DetailLoadInfo { Path = "a.dxf", Quantity = 10, Extra = 2 }) { NestedCount = 9 };

      part.RemainingCount.Should().Be(3);
    }

    /// <summary>A card never tells the operator to cut minus two parts.</summary>
    [Fact]
    public void RemainingNeverDropsBelowZero()
    {
      var part = new ObservableDetailLoadInfo(new DetailLoadInfo { Path = "a.dxf", Quantity = 5 }) { NestedCount = 7 };

      part.RemainingCount.Should().Be(0);
    }

    [Fact]
    public void APartCardIsToldWhenTheNestOrTheQuantityChanges()
    {
      var part = new ObservableDetailLoadInfo(new DetailLoadInfo { Path = "a.dxf", Quantity = 5 });
      var raised = new List<string>();
      part.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

      part.NestedCount = 4;
      raised.Should().Contain(nameof(ObservableDetailLoadInfo.RemainingCount));

      raised.Clear();
      part.Quantity = 6;
      part.Extra = 1;
      part.MirrorQuantity = 1;
      raised.FindAll(n => n == nameof(ObservableDetailLoadInfo.RequiredCount)).Should().HaveCount(3);
      raised.FindAll(n => n == nameof(ObservableDetailLoadInfo.RemainingCount)).Should().HaveCount(3);
    }

    /// <summary>A sheet row's Quantity is already reduced by what the nest used, so the total adds it back.</summary>
    [Fact]
    public void ASheetTotalIsWhatIsLeftPlusWhatTheNestUsed()
    {
      var sheet = new ObservableSheetLoadInfo(new SheetLoadInfo(120, 60, 3)) { NestedCount = 28 };

      sheet.TotalCount.Should().Be(31);
    }

    [Fact]
    public void ASheetCardIsToldWhenTheNestOrTheQuantityChanges()
    {
      var sheet = new ObservableSheetLoadInfo(new SheetLoadInfo(120, 60, 3));
      var raised = new List<string>();
      sheet.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

      sheet.NestedCount = 28;
      sheet.Quantity = 2;

      raised.FindAll(n => n == nameof(ObservableSheetLoadInfo.TotalCount)).Should().HaveCount(2);
    }
  }
}
