namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Threading;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The rounds of a timed nest, on a clock the test moves by hand: which rounds start, which one is kept,
  /// and why it stopped. No engine runs here; each round is a scripted duration and score.
  /// </summary>
  public class NestRoundsFixture
  {
    [Fact]
    public void ItStopsAfterTwoRoundsInARowThatDoNotBeatTheBest()
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = Run(TimeSpan.FromSeconds(1000), clock, Script(clock, started, (1, 5), (1, 7), (1, 7), (1, 6), (1, 9)), out var report);

      started.Should().Equal(new[] { 1, 2, 3, 4 }, "rounds 3 and 4 both failed to beat round 2, so round 5 never starts");
      best!.Value.Should().Be(7);
      report.BestRound.Should().Be(2, "a tie is not an improvement, so the earlier of the two sevens is kept");
      report.StopReason.Should().Be(NestRoundsStop.NoImprovement);
      report.FinishedRounds.Should().Be(4);
    }

    /// <summary>A round is expected to take twice as long as Fast before Normal and five times as long as
    /// Normal before Best; with less time than that left, it is not started at all.</summary>
    [Theory]
    [InlineData(50, new[] { 30.0 }, new[] { 1 })] // 20 s left, Normal would need 60
    [InlineData(100, new[] { 30.0, 40.0 }, new[] { 1, 2 })] // 70 s left, then 30 left for a Best that needs 200
    public void ItDoesNotStartARoundThereIsNoLongerTimeFor(int maxSeconds, double[] durations, int[] expectedStarted)
    {
      var clock = new FakeClock();
      var started = new List<int>();
      var rounds = durations.Select((d, i) => (d, (int?)(i + 1))).Concat(new[] { (1.0, (int?)99) }).ToArray();

      var best = Run(TimeSpan.FromSeconds(maxSeconds), clock, Script(clock, started, rounds), out var report);

      started.Should().Equal(expectedStarted);
      best!.Value.Should().Be(expectedStarted.Length);
      report.StopReason.Should().Be(NestRoundsStop.NextRoundWontFit);
      report.OverTime.Should().BeFalse();
    }

    [Fact]
    public void AFirstRoundLongerThanTheTimeStillFinishesAndSaysItWentOver()
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = Run(TimeSpan.FromSeconds(15), clock, Script(clock, started, (73, 1), (1, 2)), out var report);

      best.Should().NotBeNull("a time too short for any nest still gets the first one");
      best!.Value.Should().Be(1);
      started.Should().Equal(new[] { 1 });
      report.OverTime.Should().BeTrue();
      report.StopReason.Should().Be(NestRoundsStop.FirstRoundOverTime);
    }

    /// <summary>
    /// The round still running at the deadline is thrown away whole, whether it notices the cut and
    /// throws, or does not notice and comes back looking finished (a stage that skips its work on a
    /// cancelled token and lets the round return anyway).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARoundCutByTheDeadlineIsDiscardedWhole(bool roundThrowsWhenCut)
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = Run(
        TimeSpan.FromSeconds(100),
        clock,
        Script(clock, started, roundThrowsWhenCut, (10, 5), (200, 99)),
        out var report);

      started.Should().Equal(new[] { 1, 2 });
      best!.Value.Should().Be(5, "round 2 was still running when the time ran out");
      report.BestRound.Should().Be(1);
      report.FinishedRounds.Should().Be(1);
      report.StopReason.Should().Be(NestRoundsStop.Deadline);
    }

    /// <summary>
    /// A sort whose comparison watches the token raises the cut inside an InvalidOperationException, on its
    /// own or inside a parallel loop's AggregateException, as the common-line planner's beam does. That is
    /// still the deadline, so the round is thrown away and the nest kept.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARoundCutWhileSortingIsDiscardedWhole(bool inAParallelLoop)
    {
      var clock = new FakeClock();

      var best = Run(
        TimeSpan.FromSeconds(100),
        clock,
        (round, token) =>
        {
          clock.Advance(TimeSpan.FromSeconds(round == 1 ? 10 : 200));
          void Sort() => new List<int> { 2, 1 }.Sort((a, b) =>
          {
            token.ThrowIfCancellationRequested();
            return a.CompareTo(b);
          });
          if (inAParallelLoop)
          {
            System.Threading.Tasks.Parallel.For(0, 1, _ => Sort());
          }
          else
          {
            Sort();
          }

          return new Score(round);
        },
        out var report);

      best!.Value.Should().Be(1, "round 2 was cut while it sorted");
      report.BestRound.Should().Be(1);
      report.StopReason.Should().Be(NestRoundsStop.Deadline);
    }

    [Fact]
    public void CancelDuringALaterRoundStillThrowsAwayEverything()
    {
      var clock = new FakeClock();
      using var user = new CancellationTokenSource();

      Action run = () => NestRounds.Run<Score>(
        TimeSpan.FromSeconds(1000),
        clock,
        (round, token) =>
        {
          clock.Advance(TimeSpan.FromSeconds(1));
          if (round == 2)
          {
            user.Cancel();
            token.ThrowIfCancellationRequested();
          }

          return new Score(round);
        },
        (a, b) => a.Value > b.Value,
        user.Token,
        null,
        out _);

      run.Should().Throw<OperationCanceledException>("Cancel is the operator's, not the deadline's, so round 1 is not kept");
    }

    [Fact]
    public void ACancelThatEndsARoundQuietlyStillThrowsAwayEverything()
    {
      var clock = new FakeClock();
      using var user = new CancellationTokenSource();

      var best = NestRounds.Run<Score>(
        TimeSpan.FromSeconds(1000),
        clock,
        (round, token) =>
        {
          clock.Advance(TimeSpan.FromSeconds(1));
          if (round == 2)
          {
            user.Cancel();
            return null;
          }

          return new Score(round);
        },
        (a, b) => a.Value > b.Value,
        user.Token,
        null,
        out var report);

      best.Should().BeNull();
      report.StopReason.Should().Be(NestRoundsStop.Cancelled);
    }

    [Fact]
    public void AFailedFirstRoundEndsTheNest()
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = Run(TimeSpan.FromSeconds(1000), clock, Script(clock, started, (1, null), (1, 7)), out var report);

      best.Should().BeNull();
      started.Should().Equal(new[] { 1 });
      report.StopReason.Should().Be(NestRoundsStop.FirstRoundFailed);
    }

    [Fact]
    public void TheRoundsAreTodaysEffortsThenBestOnNewSeeds()
    {
      Enumerable.Range(1, 5).Select(NestRounds.EffortOf).Should().Equal(
        NestEffort.Fast, NestEffort.Normal, NestEffort.Best, NestEffort.Best, NestEffort.Best);
      Enumerable.Range(1, 3).Select(NestRounds.SeedOffsetOf).Should().OnlyContain(offset => offset == 0,
        "the first three rounds must be exactly the Fast, Normal and Best nests");

      int[] later = Enumerable.Range(3, 3).Select(NestRounds.SeedOffsetOf).ToArray();
      later.Zip(later.Skip(1), (a, b) => b - a).Should().OnlyContain(step => step >= 16,
        "a sheet races up to 16 seeds, so a repeat Best must start past every seed the one before it raced");
    }

    [Theory]
    [InlineData(10, 200, 0, 90, 9, 100, 0, 10)] // more parts placed beats less sheet area
    [InlineData(10, 100, 0, 90, 10, 200, 50, 10)] // less sheet area beats more shared cut
    [InlineData(10, 100, 40, 10, 10, 100, 50, 90)] // a shorter pack beats more shared cut
    [InlineData(10, 100, 50, 10, 10, 100, 40, 10)] // and more shared cut decides the rest
    public void ANestIsRankedLikeASheetCandidate(int placedA, double areaA, double sharedA, double extentA, int placedB, double areaB, double sharedB, double extentB)
    {
      var a = new NestSummary(placedA, areaA, sharedA, extentA);
      var b = new NestSummary(placedB, areaB, sharedB, extentB);

      NestRounds.IsBetter(a, b).Should().BeTrue();
      NestRounds.IsBetter(b, a).Should().BeFalse();
    }

    [Fact]
    public void NestsThatDifferOnlyInFloatNoiseAreATie()
    {
      var a = new NestSummary(10, 100, 50, 100.00000001);
      var b = new NestSummary(10, 100, 50, 100);

      NestRounds.IsBetter(a, b).Should().BeFalse();
      NestRounds.IsBetter(b, a).Should().BeFalse("a tie keeps the earlier round, whichever came first");
    }

    /// <summary>Sheet area, not sheet count, so mixed stock compares; and every sheet's pack along its own
    /// long axis, added up.</summary>
    [Fact]
    public void ANestIsSummarizedOverEverySheet()
    {
      var wide = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(1, 100, 50),
        new List<IPartPlacement>
        {
          new PartPlacement(Square("a.dxf", 10)) { X = 0, Y = 0, Source = 0 },
          new PartPlacement(Square("a.dxf", 10)) { X = 20, Y = 0, Source = 0 },
        },
        0,
        false);
      var tall = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(2, 40, 80),
        new List<IPartPlacement>
        {
          new PartPlacement(Square("a.dxf", 10)) { X = 0, Y = 0, Source = 0 },
          new PartPlacement(Square("a.dxf", 10)) { X = 0, Y = 25, Source = 0 },
        },
        0,
        false);
      var sheets = new SheetPlacementCollection { wide, tall };
      var result = new NestResult(4, sheets, new List<INfp>(), PlacementTypeEnum.BoundingBox, 0, 0);

      var summary = NestRounds.Summarize(result);

      summary.Placed.Should().Be(4);
      summary.SheetArea.Should().BeApproximately((100 * 50) + (40 * 80), 1e-9);
      summary.SharedLength.Should().Be(0);
      summary.Extent.Should().BeApproximately(30 + 35, 1e-9, "30 across the wide sheet plus 35 up the tall one");
    }

    /// <summary>The opening (the row pattern) is ready in seconds; Fast on the same 800 parts takes about a
    /// minute. At 15 seconds round 1 is started, cut at the limit and thrown away, and the opening is the nest.</summary>
    [Fact]
    public void AnOpeningIsKeptWhenRoundOneDoesNotFinishInTime()
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = RunWithOpening(TimeSpan.FromSeconds(15), clock, started, (2, 5), Script(clock, started, (100, 9)), out var report);

      started.Should().Equal(new[] { 0, 1 });
      best!.Value.Should().Be(5, "round 1 was still running at the limit");
      report.OpeningFinished.Should().BeTrue();
      report.BestRound.Should().Be(0);
      report.FinishedRounds.Should().Be(0);
      report.OverTime.Should().BeFalse();
      report.StopReason.Should().Be(NestRoundsStop.Deadline);
    }

    [Fact]
    public void RoundOneIsNotStartedWithLessTimeLeftThanTheOpeningTook()
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = RunWithOpening(TimeSpan.FromSeconds(15), clock, started, (10, 5), Script(clock, started, (1, 9)), out var report);

      started.Should().Equal(new[] { 0 });
      best!.Value.Should().Be(5);
      report.StopReason.Should().Be(NestRoundsStop.NextRoundWontFit);
    }

    /// <summary>A short search that took 6 of the 15 seconds says Fast needs at least 12 more, so Fast is not
    /// started and the nest is handed over at once. The row pattern's time says nothing about a search, so
    /// after it round 1 still gets its try.</summary>
    [Theory]
    [InlineData(true, new[] { 0 })]
    [InlineData(false, new[] { 0, 1 })]
    public void AfterTheQuickSearchRoundOneNeedsTwiceItsTimeLeft(bool searches, int[] expected)
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = RunWithOpening(TimeSpan.FromSeconds(15), clock, started, (6, 5), Script(clock, started, (100, 9)), out var report, searches);

      started.Should().Equal(expected);
      best!.Value.Should().Be(5);
      report.StopReason.Should().Be(searches ? NestRoundsStop.NextRoundWontFit : NestRoundsStop.Deadline);
    }

    [Fact]
    public void AFailedOpeningLeavesTheRoundsAsTheyWere()
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = RunWithOpening(TimeSpan.FromSeconds(15), clock, started, (1, null), Script(clock, started, (73, 1), (1, 2)), out var report);

      started.Should().Equal(new[] { 0, 1 });
      best!.Value.Should().Be(1, "with no opening, round 1 is the one that always finishes");
      report.OpeningFinished.Should().BeFalse();
      report.OverTime.Should().BeTrue();
      report.StopReason.Should().Be(NestRoundsStop.FirstRoundOverTime);
    }

    [Fact]
    public void ARoundThatBeatsTheOpeningIsKept()
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = RunWithOpening(TimeSpan.FromSeconds(1000), clock, started, (1, 5), Script(clock, started, (2, 9), (2, 9), (2, 9)), out var report);

      started.Should().Equal(new[] { 0, 1, 2, 3 });
      best!.Value.Should().Be(9);
      report.BestRound.Should().Be(1);
      report.StopReason.Should().Be(NestRoundsStop.NoImprovement);
    }

    [Fact]
    public void AnOpeningLongerThanTheTimeIsKeptAndSaysItWentOver()
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = RunWithOpening(TimeSpan.FromSeconds(15), clock, started, (20, 5), Script(clock, started, (1, 9)), out var report);

      started.Should().Equal(new[] { 0 });
      best!.Value.Should().Be(5);
      report.OverTime.Should().BeTrue();
      report.StopReason.Should().Be(NestRoundsStop.FirstRoundOverTime);
    }

    [Fact]
    public void ACancelDuringTheOpeningThrowsAwayEverything()
    {
      var clock = new FakeClock();
      using var user = new CancellationTokenSource();

      var best = NestRounds.Run<Score>(
        TimeSpan.FromSeconds(1000),
        clock,
        (round, token) => new Score(round),
        (a, b) => a.Value > b.Value,
        user.Token,
        null,
        out var report,
        opening: token =>
        {
          user.Cancel();
          return new Score(5);
        });

      best.Should().BeNull();
      report.StopReason.Should().Be(NestRoundsStop.Cancelled);
    }

    /// <summary>A part left on its own is cut all the way round by itself, so it outranks shared cut won
    /// elsewhere on the sheet; material still outranks it.</summary>
    [Fact]
    public void APartLeftOnItsOwnCostsMoreThanSharedCutElsewhere()
    {
      var everyPartShares = new NestSummary(10, 100, 50, 90, unshared: 0);
      var moreCutOneAlone = new NestSummary(10, 100, 80, 90, unshared: 1);
      var lessSheetThreeAlone = new NestSummary(10, 90, 0, 90, unshared: 3);

      NestRounds.IsBetter(everyPartShares, moreCutOneAlone).Should().BeTrue();
      NestRounds.IsBetter(moreCutOneAlone, everyPartShares).Should().BeFalse();
      NestRounds.IsBetter(lessSheetThreeAlone, everyPartShares).Should().BeTrue("sheet area is material and still comes first");
    }

    [Fact]
    public void APartWithNoSharedCutIsCountedOnlyWhenItsDrawingMayShare()
    {
      var parts = new List<IPartPlacement>
      {
        new PartPlacement(Square("a.dxf", 10)) { X = 0, Y = 0, Source = 0, Id = 0 },
        new PartPlacement(Square("a.dxf", 10)) { X = 10.01, Y = 0, Source = 0, Id = 1 },
        new PartPlacement(Square("a.dxf", 10)) { X = 40, Y = 0, Source = 0, Id = 2 },
      };
      var seam = new CommonEdge(0, 1, 1, 3, new CommonLinePoint(10.005, 0), new CommonLinePoint(10.005, 10), 0.01);
      var sheet = new SheetPlacement(PlacementTypeEnum.BoundingBox, Sheet.NewSheet(1, 100, 50), parts, 0, false, new[] { seam }, 0.01, 0.0001);
      var result = new NestResult(3, new SheetPlacementCollection { sheet }, new List<INfp>(), PlacementTypeEnum.BoundingBox, 0, 0);

      NestRounds.Summarize(result, source => true).Unshared.Should().Be(1, "part 2 shares no cut");
      NestRounds.Summarize(result, source => false).Unshared.Should().Be(0, "a drawing not set to share is never on its own by choice");
      NestRounds.Summarize(result).Unshared.Should().Be(0);
    }

    /// <summary>An opening that already uses the sheet as well as the operator asked is handed over at once:
    /// round 1 is not started, though there is time for it.</summary>
    [Fact]
    public void AnOpeningThatReachesTheTargetIsHandedOverWithoutARound()
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = RunWithOpening(TimeSpan.FromSeconds(1000), clock, started, (3, 9), Script(clock, started, (5, 10)), out var report, reachedTarget: s => s.Value >= 9);

      started.Should().Equal(new[] { 0 });
      best!.Value.Should().Be(9);
      report.StopReason.Should().Be(NestRoundsStop.TargetReached);
    }

    [Fact]
    public void ARoundThatReachesTheTargetEndsTheRounds()
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = Run(TimeSpan.FromSeconds(1000), clock, Script(clock, started, (1, 5), (1, 9), (1, 12)), out var report, s => s.Value >= 9);

      started.Should().Equal(new[] { 1, 2 }, "round 2 reached the target, so round 3 never starts");
      best!.Value.Should().Be(9);
      report.BestRound.Should().Be(2);
      report.StopReason.Should().Be(NestRoundsStop.TargetReached);
    }

    [Fact]
    public void BelowTheTargetTheRoundsRunAsWithoutOne()
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = Run(TimeSpan.FromSeconds(1000), clock, Script(clock, started, (1, 5), (1, 7), (1, 7), (1, 6), (1, 9)), out var report, s => s.Value >= 100);

      started.Should().Equal(new[] { 1, 2, 3, 4 });
      best!.Value.Should().Be(7);
      report.StopReason.Should().Be(NestRoundsStop.NoImprovement);
    }

    /// <summary>The target is asked of the nest that would be handed over. A round that uses the sheet better
    /// but ranks lower (it leaves a part out, say) neither stops the rounds nor is handed over.</summary>
    [Fact]
    public void TheTargetIsAskedOfTheBestNestNotTheLast()
    {
      var clock = new FakeClock();
      var started = new List<int>();
      var rounds = new[] { new Score(7, 0.5), new Score(5, 0.95), new Score(6, 0.95), new Score(9, 0.95) };

      var best = Run(
        TimeSpan.FromSeconds(1000),
        clock,
        (round, token) =>
        {
          started.Add(round);
          clock.Advance(TimeSpan.FromSeconds(1));
          return rounds[round - 1];
        },
        out var report,
        s => s.Usage >= 0.9);

      started.Should().Equal(new[] { 1, 2, 3 }, "rounds 2 and 3 used the sheet better but ranked below round 1");
      best!.Value.Should().Be(7);
      report.StopReason.Should().Be(NestRoundsStop.NoImprovement);
    }

    /// <summary>The offcut is kept for the next job, so it is not counted: the one SheetNest cuts, a gap past the
    /// parts. Two 10 x 10 squares end 35 along a 100 x 50 sheet; with a gap of 1 the cut is at 36 and frees
    /// 64 x 50.</summary>
    [Fact]
    public void OnlyTheOffcutSheetNestCutsIsLeftOut()
    {
      var job = Job(SquaresOn(1, 100, 50, 5, 25));

      NestRounds.UsedUtilization(job, Offcut(OffcutDirection.End, 1)).Should().BeApproximately(200.0 / (5000 - (64 * 50)), 1e-9);
    }

    /// <summary>Only the last sheet keeps an offcut. What is left on the sheets before it is waste.</summary>
    [Fact]
    public void TheSheetsBeforeTheLastCountWhole()
    {
      var job = Job(SquaresOn(1, 100, 50, 5, 25), SquaresOn(2, 100, 50, 5, 25));

      NestRounds.UsedUtilization(job, Offcut(OffcutDirection.End, 1)).Should().BeApproximately(400.0 / (5000 + 5000 - (64 * 50)), 1e-9);
    }

    /// <summary>The parts end at 90, so the cut would free a strip 9 wide: under the 12 an offcut needs, so it
    /// stays on the sheet as waste.</summary>
    [Fact]
    public void AStripNarrowerThanTheMinimumIsNoOffcut()
    {
      var job = Job(SquaresOn(1, 100, 50, 70, 80));

      NestRounds.UsedUtilization(job, Offcut(OffcutDirection.End, 1)).Should().BeApproximately(200.0 / 5000, 1e-9);
    }

    /// <summary>On automatic, a 30 x 30 square in the corner of a 120 x 60 sheet is cut free as an L: 90 x 60 off
    /// the end and 30 x 30 off the side. Everything else is the square, so the parts use all of it.</summary>
    [Fact]
    public void AnLShapedOffcutIsLeftOutWhole()
    {
      var sheet = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(1, 120, 60),
        new List<IPartPlacement> { new PartPlacement(Square("a.dxf", 30)) { X = 0, Y = 0, Source = 0 } },
        0,
        false);

      NestRounds.UsedUtilization(Job(sheet), Offcut(OffcutDirection.Auto, 0)).Should().BeApproximately(1.0, 1e-9, "the end cut alone would leave 30 x 30 of waste: a half");
    }

    /// <summary>With the offcut option off SheetNest cuts no offcut, so every sheet counts whole.</summary>
    [Fact]
    public void WithTheOffcutOptionOffEverySheetCountsWhole()
    {
      var job = Job(SquaresOn(1, 100, 50, 5, 25));

      NestRounds.UsedUtilization(job, null).Should().BeApproximately(200.0 / 5000, 1e-9);
    }

    [Fact]
    public void ANestThatLeavesAPartOutNeverReachesTheTarget()
    {
      var sheet = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(1, 100, 50),
        new List<IPartPlacement> { new PartPlacement(Square("a.dxf", 10)) { X = 0, Y = 0, Source = 0 } },
        0,
        false);
      var everyPart = new NestResult(1, new SheetPlacementCollection { sheet }, new List<INfp>(), PlacementTypeEnum.BoundingBox, 0, 0);
      var onePartOut = new NestResult(2, new SheetPlacementCollection { sheet }, new List<INfp> { Square("a.dxf", 10) }, PlacementTypeEnum.BoundingBox, 0, 0);
      var offcut = Offcut(OffcutDirection.End, 0);

      NestRounds.UsedUtilization(everyPart, offcut).Should().BeApproximately(0.2, 1e-9, "the 10 x 10 square on the 10 x 50 left by a 90 x 50 offcut");
      SparrowNestService.ReachesTarget(everyPart, 0.2, offcut).Should().BeTrue();
      SparrowNestService.ReachesTarget(everyPart, 0.21, offcut).Should().BeFalse();
      SparrowNestService.ReachesTarget(onePartOut, 0.1, offcut).Should().BeFalse("a later round may place the part left out");
      SparrowNestService.ReachesTarget(everyPart, 0, offcut).Should().BeFalse("zero is no target");
    }

    /// <summary>The app nests with no time limit: a round of ten minutes is no reason to stop, and nothing says
    /// the nest went over. Two rounds in a row that do not improve still end it.</summary>
    [Fact]
    public void WithNoLimitLongRoundsRunUntilTheyStopImproving()
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = Run(NestRounds.NoLimit, clock, Script(clock, started, (600, 5), (600, 7), (600, 7), (600, 6), (600, 9)), out var report);

      started.Should().Equal(new[] { 1, 2, 3, 4 });
      best!.Value.Should().Be(7);
      report.BestRound.Should().Be(2);
      report.OverTime.Should().BeFalse();
      report.StopReason.Should().Be(NestRoundsStop.NoImprovement);
    }

    [Fact]
    public void WithNoLimitTheTargetStillEndsTheRounds()
    {
      var clock = new FakeClock();
      var started = new List<int>();

      var best = RunWithOpening(NestRounds.NoLimit, clock, started, (600, 5), Script(clock, started, (600, 9), (600, 12)), out var report, reachedTarget: s => s.Value >= 9);

      started.Should().Equal(new[] { 0, 1 }, "round 1 was not cut, and it reached the target");
      best!.Value.Should().Be(9);
      report.StopReason.Should().Be(NestRoundsStop.TargetReached);
    }

    /// <summary>Stop hands over the best nest so far. The round still running is thrown away whole, whether it
    /// notices and throws or comes back looking finished.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StopDuringARoundHandsOverTheBestSoFar(bool roundThrowsWhenStopped)
    {
      var clock = new FakeClock();
      using var stop = new CancellationTokenSource();

      var best = RunStoppable(
        NestRounds.NoLimit,
        clock,
        (round, token) =>
        {
          clock.Advance(TimeSpan.FromSeconds(60));
          if (round == 2)
          {
            stop.Cancel();
            if (roundThrowsWhenStopped)
            {
              token.ThrowIfCancellationRequested();
            }

            return new Score(99);
          }

          return new Score(round * 5);
        },
        stop.Token,
        out var report);

      best!.Value.Should().Be(5, "round 2 was still running when Stop was pressed");
      report.BestRound.Should().Be(1);
      report.FinishedRounds.Should().Be(1);
      report.StopReason.Should().Be(NestRoundsStop.Stopped);
    }

    /// <summary>A Stop that lands between two rounds hands the nest over without starting the next one.</summary>
    [Fact]
    public void StopBetweenRoundsStartsNoOther()
    {
      var clock = new FakeClock();
      var started = new List<int>();
      using var stop = new CancellationTokenSource();

      var best = RunStoppable(
        NestRounds.NoLimit,
        clock,
        Script(clock, started, false, (60, 5), (60, 9)),
        stop.Token,
        out var report,
        newBest: _ => stop.Cancel());

      started.Should().Equal(new[] { 1 });
      best!.Value.Should().Be(5);
      report.StopReason.Should().Be(NestRoundsStop.Stopped);
    }

    /// <summary>Before any nest has finished there is nothing to hand over, so a Stop ends the nest like a Cancel.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStopBeforeAnyNestHandsOverNothing(bool duringTheOpening)
    {
      var clock = new FakeClock();
      using var stop = new CancellationTokenSource();

      var best = NestRounds.Run<Score>(
        NestRounds.NoLimit,
        clock,
        (round, token) =>
        {
          stop.Cancel();
          token.ThrowIfCancellationRequested();
          return new Score(round);
        },
        (a, b) => a.Value > b.Value,
        CancellationToken.None,
        null,
        out var report,
        opening: duringTheOpening
          ? token =>
          {
            stop.Cancel();
            token.ThrowIfCancellationRequested();
            return new Score(5);
          }
          : null,
        stop: stop.Token);

      best.Should().BeNull();
      report.StopReason.Should().Be(NestRoundsStop.Cancelled);
    }

    /// <summary>Cancel is not Stop: it still throws away the nest a Stop would have handed over.</summary>
    [Fact]
    public void CancelStillThrowsAwayTheBestSoFar()
    {
      var clock = new FakeClock();
      using var user = new CancellationTokenSource();
      using var stop = new CancellationTokenSource();

      var best = NestRounds.Run<Score>(
        NestRounds.NoLimit,
        clock,
        (round, token) =>
        {
          if (round == 2)
          {
            user.Cancel();
            return null;
          }

          return new Score(round);
        },
        (a, b) => a.Value > b.Value,
        user.Token,
        null,
        out var report,
        stop: stop.Token);

      best.Should().BeNull();
      report.StopReason.Should().Be(NestRoundsStop.Cancelled);
    }

    /// <summary>The status line shows how good the nest Stop would hand over is, so it is told of every nest
    /// that becomes the best, and of no other.</summary>
    [Fact]
    public void NewBestIsToldOfEachImprovementOnly()
    {
      var clock = new FakeClock();
      var started = new List<int>();
      var told = new List<int>();

      NestRounds.Run(
        NestRounds.NoLimit,
        clock,
        Script(clock, started, (60, 9), (60, 7), (60, 12), (60, 12), (60, 3)),
        (a, b) => a.Value > b.Value,
        CancellationToken.None,
        null,
        out _,
        opening: token => new Score(5),
        newBest: s => told.Add(s.Value));

      told.Should().Equal(new[] { 5, 9, 12 });
    }

    /// <summary>The offcut settings a new install cuts with, in inches, on the given side.</summary>
    private static OffcutOptions Offcut(OffcutDirection direction, double spacing)
      => new OffcutOptions { Direction = direction, Spacing = spacing, MinX = 12, MinY = 12, MaxX = 240, MaxY = 240 };

    /// <summary>A sheet with a 10 x 10 square at each of <paramref name="xs"/>, along the bottom edge.</summary>
    private static SheetPlacement SquaresOn(int id, int width, int height, params double[] xs)
      => new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(id, width, height),
        xs.Select(x => (IPartPlacement)new PartPlacement(Square("a.dxf", 10)) { X = x, Y = 0, Source = 0 }).ToList(),
        0,
        false);

    private static NestResult Job(params SheetPlacement[] sheets)
    {
      var collection = new SheetPlacementCollection();
      foreach (var sheet in sheets)
      {
        collection.Add(sheet);
      }

      return new NestResult(sheets.Sum(s => s.PartPlacements.Count), collection, new List<INfp>(), PlacementTypeEnum.BoundingBox, 0, 0);
    }

    private static Score? Run(TimeSpan maxTime, FakeClock clock, Func<int, CancellationToken, Score?> runRound, out NestRoundsReport report, Func<Score, bool>? reachedTarget = null)
      => NestRounds.Run(maxTime, clock, runRound, (a, b) => a.Value > b.Value, CancellationToken.None, null, out report, reachedTarget: reachedTarget);

    private static Score? RunStoppable(TimeSpan maxTime, FakeClock clock, Func<int, CancellationToken, Score?> runRound, CancellationToken stop, out NestRoundsReport report, Action<Score>? newBest = null)
      => NestRounds.Run(maxTime, clock, runRound, (a, b) => a.Value > b.Value, CancellationToken.None, null, out report, stop: stop, newBest: newBest);

    /// <summary>The opening takes <paramref name="opening"/>.Seconds and scores its Score (null = gave nothing).</summary>
    private static Score? RunWithOpening(TimeSpan maxTime, FakeClock clock, List<int> started, (double Seconds, int? Score) opening, Func<int, CancellationToken, Score?> runRound, out NestRoundsReport report, bool searches = false, Func<Score, bool>? reachedTarget = null)
      => NestRounds.Run(
        maxTime,
        clock,
        runRound,
        (a, b) => a.Value > b.Value,
        CancellationToken.None,
        null,
        out report,
        opening: token =>
        {
          started.Add(0);
          clock.Advance(TimeSpan.FromSeconds(opening.Seconds));
          return opening.Score is int value ? new Score(value) : null;
        },
        openingSearches: searches,
        reachedTarget: reachedTarget);

    private static Func<int, CancellationToken, Score?> Script(FakeClock clock, List<int> started, params (double Seconds, int? Score)[] rounds)
      => Script(clock, started, true, rounds);

    /// <summary>Round n takes rounds[n-1].Seconds on the clock and scores rounds[n-1].Score (null = failed).</summary>
    private static Func<int, CancellationToken, Score?> Script(FakeClock clock, List<int> started, bool throwWhenCut, params (double Seconds, int? Score)[] rounds)
      => (round, token) =>
      {
        started.Add(round);
        var (seconds, score) = rounds[round - 1];
        clock.Advance(TimeSpan.FromSeconds(seconds));
        if (throwWhenCut)
        {
          token.ThrowIfCancellationRequested();
        }

        return score is int value ? new Score(value) : null;
      };

    private static INfp Square(string name, double side)
      => new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(0, 0),
        new SvgPoint(side, 0),
        new SvgPoint(side, side),
        new SvgPoint(0, side),
      })
      { Name = name };

    private sealed class Score
    {
      public Score(int value, double usage = 0)
      {
        this.Value = value;
        this.Usage = usage;
      }

      public int Value { get; }

      /// <summary>How well the round uses the sheet, apart from how it ranks.</summary>
      public double Usage { get; }
    }

    /// <summary>Time moves only when a round says it took some, and a deadline fires the moment it passes.</summary>
    private sealed class FakeClock : IRoundClock
    {
      private readonly List<(CancellationTokenSource Source, TimeSpan At)> armed = new();

      public TimeSpan Elapsed { get; private set; }

      public void CancelAt(CancellationTokenSource source, TimeSpan at)
      {
        this.armed.Add((source, at));
        this.Fire();
      }

      public void Advance(TimeSpan by)
      {
        this.Elapsed += by;
        this.Fire();
      }

      private void Fire()
      {
        foreach (var (source, at) in this.armed.Where(a => this.Elapsed >= a.At).ToList())
        {
          this.armed.Remove((source, at));
          try
          {
            source.Cancel();
          }
          catch (ObjectDisposedException)
          {
            // The deadline of a round that already finished; its source went with it.
          }
        }
      }
    }
  }
}
