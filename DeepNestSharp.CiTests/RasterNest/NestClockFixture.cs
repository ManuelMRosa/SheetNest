namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The nest by the clock (a time per sheet): how what is left is split between the sheets, how long the
  /// engine searches on each, and how it is told to stop on the clock instead of on a count.
  /// </summary>
  public class NestClockFixture
  {
    [Theory]
    [InlineData(10_000, 5, 2_000)] // the rest split evenly over the sheets still expected
    [InlineData(6_000, 1, 6_000)] // the last sheet gets all that is left
    [InlineData(1_000, 5, 700)] // never below the floor, so the last sheets are not starved
    [InlineData(-500, 3, 700)] // already late: still the floor, the job has to finish
    [InlineData(4_000, 0, 4_000)] // no sheet expected is one
    public void WhatIsLeftIsSplitOverTheSheetsStillExpected(int remainingMs, int sheetsLeft, int expectedMs)
    {
      NestClock.SheetShare(TimeSpan.FromMilliseconds(remainingMs), sheetsLeft, TimeSpan.FromMilliseconds(700))
        .Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }

    /// <summary>The engine's start is paid first; the search gets a part of the rest, a smaller part with common
    /// line, because the planner runs on its answer within the same share.</summary>
    [Fact]
    public void TheSearchGetsWhatTheShareLeavesAfterStartingTheEngine()
    {
      int plain = NestClock.SearchMs(TimeSpan.FromSeconds(2), commonLine: false);
      int shared = NestClock.SearchMs(TimeSpan.FromSeconds(2), commonLine: true);

      plain.Should().BeLessThan(2000 - (int)NestClock.EngineStart.TotalMilliseconds + 1);
      plain.Should().BeGreaterThan(1000);
      shared.Should().BeLessThan(plain, "the planner needs its time after the search");
      NestClock.SearchMs(TimeSpan.FromMilliseconds(300), commonLine: false).Should().Be(NestClock.MinSearchMs, "a search shorter than this is not worth starting the engine for");
    }

    /// <summary>A job expected to take one sheet that turns out to need three gets the time per sheet for each,
    /// and never less than it was given; the new end is announced so the status line can show it.</summary>
    [Fact]
    public void ASheetBeyondTheExpectedOnesBringsItsOwnTime()
    {
      var announced = new List<TimeSpan>();
      var clock = new NestClock(new StoppedClock(), TimeSpan.FromSeconds(2), secondsPerSheet: 2, extended: announced.Add);

      clock.CoverSheets(1);
      clock.End.Should().Be(TimeSpan.FromSeconds(2), "one sheet was expected and one is in use");
      clock.CoverSheets(3);
      clock.End.Should().Be(TimeSpan.FromSeconds(6));
      clock.CoverSheets(2);
      clock.End.Should().Be(TimeSpan.FromSeconds(6), "the time given is never taken back");
      announced.Should().Equal(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void WithoutATimePerSheetTheEndStaysPut()
    {
      var clock = new NestClock(new StoppedClock(), TimeSpan.FromSeconds(10));

      clock.CoverSheets(50);

      clock.End.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ByTheClockTheEngineIsToldATimeAndNoCount()
    {
      var arguments = SparrowNestService.SparrowArguments("job.json", 300, 1_000_000, 1, 7, timeMs: 1200);

      arguments.Should().Equal("-i", "job.json", "--time-ms", "1200", "-x", "-s", "7", "--workers", "1");
      arguments.Should().NotContain("--max-iterations");
      arguments.Should().NotContain("--max-evals");
    }

    [Fact]
    public void WithoutAClockTheEngineIsToldTheCountAsBefore()
    {
      var arguments = SparrowNestService.SparrowArguments("job.json", 300, 1_000_000, 2, 7);

      arguments.Should().Equal("-i", "job.json", "--max-iterations", "300", "--max-evals", "1000000", "-x", "-s", "7", "--workers", "2");
    }

    /// <summary>A clock that never moves: only the end matters here.</summary>
    private sealed class StoppedClock : IRoundClock
    {
      public TimeSpan Elapsed => TimeSpan.Zero;

      public void CancelAt(System.Threading.CancellationTokenSource source, TimeSpan at)
      {
      }
    }
  }
}
