namespace DeepNestSharp.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Linq;
  using System.Threading;
  using DeepNestLib.Placement;

  /// <summary>
  /// "Nest for up to this long": the job is nested in rounds of growing effort until the time says stop.
  /// The best round that finished is the nest.
  /// </summary>
  /// <remarks>
  /// <para>Each round is the ordinary fixed-iteration nest (Fast, then Normal, then Best, then Best again on
  /// seeds nobody has tried), so every round lands the same on any machine. The clock never cuts a round
  /// short and keeps what it had: a round still running when the time is up is thrown away whole, because
  /// a truncated search is not reproducible. What the clock decides is how many rounds are tried.</para>
  /// <para>The time is a ceiling, not a target. Nesting stops early when two rounds in a row fail to beat
  /// the best one, and does not start a round there is no longer time for. The first round is the
  /// exception: it always finishes, so a time too short for any nest still returns one, and says so. That
  /// first round is a quick one, the row pattern or a short search, so Fast is cut like the rest.</para>
  /// <para>It also stops as soon as the best nest so far reaches the operator's material usage target
  /// (<see cref="UsedUtilization"/>, the offcut left out): a nest already that good is handed over without
  /// spending the rest of the time.</para>
  /// <para>The app nests with <see cref="NoLimit"/>: no clock at all, so only the target, two rounds without
  /// improvement or the operator's Stop end the rounds. A fixed time is for the benches and the tests.</para>
  /// </remarks>
  internal static class NestRounds
  {
    /// <summary>The time the benches and the timed tests nest for. The app has no time limit (<see cref="NoLimit"/>).</summary>
    internal const int DefaultSeconds = 15;

    /// <summary>Later rounds race seeds this far apart. A sheet races at most 16, so they never overlap.</summary>
    private const int SeedStride = 1000;

    /// <summary>No time limit: the rounds stop on the material usage target, on two rounds in a row that do
    /// not improve, or when the operator stops them. This is how the app nests.</summary>
    internal static readonly TimeSpan NoLimit = Timeout.InfiniteTimeSpan;

    internal static NestEffort EffortOf(int round) => round switch
    {
      1 => NestEffort.Fast,
      2 => NestEffort.Normal,
      _ => NestEffort.Best,
    };

    /// <summary>Rounds 1 to 3 are exactly today's Fast, Normal and Best nests; from round 4 each one races
    /// a new range of seeds, or it would only repeat round 3.</summary>
    internal static int SeedOffsetOf(int round) => round <= 3 ? 0 : (round - 3) * SeedStride;

    /// <summary>How many times longer a round is expected to take than the one before it.</summary>
    /// <remarks>Measured, and taken at the low end so a round that would fit is not skipped: Fast to Normal
    /// took 2.3 times as long on 150 Hodgson parts with common line and 7.5 times on the fourteen part
    /// reference job; Normal to Best took 5 times on that job. From round 4 on every round is a Best.
    /// Round 1 after the row pattern is 1 as well: the pattern is arithmetic, so its time says nothing about
    /// how long a search takes, and the deadline throws round 1 away if it does not fit. After the quick
    /// search it is 2: on the invented mixed jobs Fast took 2.2 times as long with common line, 3.7 times on
    /// four sheets and 5.4 times on one.</remarks>
    internal static double GrowthBefore(int round, bool afterQuickSearch = false) => round switch
    {
      1 => afterQuickSearch ? 2 : 1,
      2 => 2,
      3 => 5,
      _ => 1,
    };

    /// <summary>
    /// Runs rounds 1, 2, 3... until the time, the lack of improvement or the operator stops them, and
    /// returns the best round that finished (null when the first round failed or the operator cancelled).
    /// </summary>
    /// <param name="runRound">Runs one whole nest for a round; null when it produced nothing.</param>
    /// <param name="isBetter">Whether the first result beats the second. A tie keeps the earlier round.</param>
    /// <param name="roundFinished">Told of every round that ran to the end with a result, kept or not.</param>
    /// <param name="opening">A quick nest run before round 1 as round 0: the row pattern, worked out without
    /// searching, or a short search. When it gives a nest, it is the one that always finishes, and round 1 has
    /// to fit in the time like every other round. When it gives nothing, the rounds run exactly as if it were
    /// not there.</param>
    /// <param name="openingSearches">The opening is the short search, so its time says how long round 1 will
    /// take (see <see cref="GrowthBefore"/>).</param>
    /// <param name="reachedTarget">Whether a nest is good enough to hand over at once. It is asked of the best
    /// nest so far, the one that would be handed over, after the opening and after every round.</param>
    /// <param name="stop">The operator's Stop: the round still running is thrown away whole, as at the deadline,
    /// and the best nest so far is handed over. With none yet it ends the nest like a Cancel.</param>
    /// <param name="newBest">Told of every nest that becomes the best so far, the opening's included.</param>
    internal static T? Run<T>(
      TimeSpan maxTime,
      IRoundClock clock,
      Func<int, CancellationToken, T?> runRound,
      Func<T, T, bool> isBetter,
      CancellationToken cancel,
      Action<int>? roundStarted,
      out NestRoundsReport report,
      Action<int, T>? roundFinished = null,
      Func<CancellationToken, T?>? opening = null,
      bool openingSearches = false,
      Func<T, bool>? reachedTarget = null,
      CancellationToken stop = default,
      Action<T>? newBest = null)
      where T : class
    {
      report = new NestRoundsReport { MaxTime = maxTime };
      bool limited = maxTime != NoLimit;
      T? best = null;
      int misses = 0;
      TimeSpan lastDuration = TimeSpan.Zero;
      if (opening != null)
      {
        roundStarted?.Invoke(0);
        TimeSpan started = clock.Elapsed;
        T? first;
        using (var openingCut = CancellationTokenSource.CreateLinkedTokenSource(cancel, stop))
        {
          try
          {
            first = opening(openingCut.Token);
          }
          catch (Exception ex) when (IsCancellation(ex) && stop.IsCancellationRequested && !cancel.IsCancellationRequested)
          {
            first = null;
          }
        }

        if (cancel.IsCancellationRequested || stop.IsCancellationRequested)
        {
          // Nothing finished yet, so a Stop has nothing to hand over.
          report.StopReason = NestRoundsStop.Cancelled;
          return null;
        }

        if (first != null)
        {
          best = first;
          newBest?.Invoke(first);
          report.OpeningFinished = true;
          lastDuration = clock.Elapsed - started;
          roundFinished?.Invoke(0, first);
          if (limited && clock.Elapsed > maxTime)
          {
            report.OverTime = true;
            report.StopReason = NestRoundsStop.FirstRoundOverTime;
            return best;
          }

          if (reachedTarget?.Invoke(best) == true)
          {
            report.StopReason = NestRoundsStop.TargetReached;
            return best;
          }
        }
      }

      for (int round = 1; ; round++)
      {
        if (best != null && stop.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
          report.StopReason = NestRoundsStop.Stopped;
          return best;
        }

        if (limited && (round > 1 || report.OpeningFinished) && maxTime - clock.Elapsed < lastDuration * GrowthBefore(round, openingSearches))
        {
          report.StopReason = NestRoundsStop.NextRoundWontFit;
          return best;
        }

        roundStarted?.Invoke(round);
        TimeSpan started = clock.Elapsed;
        T? result;
        using (var cut = CancellationTokenSource.CreateLinkedTokenSource(cancel, stop))
        {
          // The first round is never cut by the time: a time too short for any nest still gets one.
          if (limited && (round > 1 || report.OpeningFinished))
          {
            clock.CancelAt(cut, maxTime);
          }

          try
          {
            result = runRound(round, cut.Token);
          }
          catch (Exception ex) when (IsCancellation(ex) && cut.IsCancellationRequested && !cancel.IsCancellationRequested)
          {
            result = null;
          }

          // Judged by the token, not by whether the round threw: a stage that does not watch it can still
          // finish and hand over a nest the cut already changed, and that nest is not this round's.
          if (cut.IsCancellationRequested && !cancel.IsCancellationRequested)
          {
            if (stop.IsCancellationRequested)
            {
              report.StopReason = best == null ? NestRoundsStop.Cancelled : NestRoundsStop.Stopped;
            }
            else
            {
              report.StopReason = NestRoundsStop.Deadline;
            }

            return best;
          }
        }

        if (cancel.IsCancellationRequested)
        {
          report.StopReason = NestRoundsStop.Cancelled;
          return null;
        }

        lastDuration = clock.Elapsed - started;
        report.FinishedRounds = round;
        if (result != null)
        {
          roundFinished?.Invoke(round, result);
        }

        if (result != null && (best == null || isBetter(result, best)))
        {
          best = result;
          newBest?.Invoke(result);
          report.BestRound = round;
          misses = 0;
        }
        else
        {
          misses++;
        }

        if (round == 1 && !report.OpeningFinished)
        {
          if (best == null)
          {
            report.StopReason = NestRoundsStop.FirstRoundFailed;
            return null;
          }

          if (limited && clock.Elapsed > maxTime)
          {
            report.OverTime = true;
            report.StopReason = NestRoundsStop.FirstRoundOverTime;
            return best;
          }
        }

        if (best != null && reachedTarget?.Invoke(best) == true)
        {
          report.StopReason = NestRoundsStop.TargetReached;
          return best;
        }

        if (misses >= 2)
        {
          report.StopReason = NestRoundsStop.NoImprovement;
          return best;
        }
      }
    }

    /// <summary>What a whole nest is ranked on. <paramref name="mayShare"/> says which drawings are set to
    /// share cuts; a part of one of those with no shared cut on its sheet counts as on its own. Without it,
    /// nothing does.</summary>
    internal static NestSummary Summarize(INestResult result, Func<int, bool>? mayShare = null)
    {
      double shared = 0;
      double extent = 0;
      int unshared = 0;
      foreach (ISheetPlacement sheet in result.UsedSheets)
      {
        var edges = sheet.CommonEdges ?? Array.Empty<CommonEdge>();
        shared += edges.Sum(SparrowNestService.CommonEdgeLength);
        var (w, h) = SparrowNestService.Extent(sheet.PartPlacements);
        extent += sheet.Sheet.WidthCalculated >= sheet.Sheet.HeightCalculated ? w : h;
        if (mayShare != null)
        {
          var welded = new HashSet<int>(edges.SelectMany(e => new[] { e.PartAId, e.PartBId }));
          unshared += sheet.PartPlacements.Count(p => mayShare(p.Source) && !welded.Contains(p.Id));
        }
      }

      return new NestSummary(result.TotalPlacedCount, result.TotalSheetsArea, shared, extent, unshared);
    }

    /// <summary>How much of the sheets the parts use, the offcut not counted: the parts' area over the sheets
    /// they are on, less the offcut SheetNest cuts off the last one (<see cref="OffcutGeometry"/>, the same
    /// rectangle the viewer draws and the export cuts). The offcut is material kept for the next job, not
    /// waste. Whatever is left on the other sheets, or a strip too small to be an offcut, is waste and counts
    /// as used; with the offcut option off (null) every sheet counts whole.</summary>
    internal static double UsedUtilization(INestResult result, OffcutOptions? offcut)
    {
      double partsArea = 0;
      double usedArea = 0;
      ISheetPlacement? last = null;
      foreach (ISheetPlacement sheet in result.UsedSheets)
      {
        if (sheet.PartPlacements.Count == 0)
        {
          continue;
        }

        usedArea += sheet.Sheet.WidthCalculated * sheet.Sheet.HeightCalculated;
        partsArea += sheet.PartPlacements.Sum(p => Math.Abs(p.Part.NetArea));
        last = sheet;
      }

      if (last != null && offcut != null)
      {
        var (cutX, cutY) = OffcutGeometry.CutPositions(last, offcut);
        foreach (var remnant in OffcutGeometry.RemnantRects(cutX, cutY, last.Sheet.WidthCalculated, last.Sheet.HeightCalculated))
        {
          usedArea -= remnant.W * remnant.H;
        }
      }

      return usedArea > 0 ? partsArea / usedArea : 0;
    }

    /// <summary>
    /// Whether nest <paramref name="a"/> beats nest <paramref name="b"/>: parts placed first, because a part
    /// left uncut is the worst outcome; then less sheet area, which is material; then fewer parts set to
    /// share cuts that share none; then the shortest packs along each sheet's long axis, which is the offcut
    /// left in one piece; then more shared cut. Differences in the last bits of a float are a tie, and a
    /// tie is not better.
    /// <para>This is the order a sheet's own candidates are ranked in (SparrowNestService.PickBest) with one
    /// rule added on purpose: a part left on its own costs the operator a separate cut all the way round, so
    /// more shared cut elsewhere on the sheet does not make up for it.</para>
    /// </summary>
    internal static bool IsBetter(NestSummary a, NestSummary b)
    {
      if (a.Placed != b.Placed)
      {
        return a.Placed > b.Placed;
      }

      if (!Same(a.SheetArea, b.SheetArea))
      {
        return a.SheetArea < b.SheetArea;
      }

      if (a.Unshared != b.Unshared)
      {
        return a.Unshared < b.Unshared;
      }

      if (!Same(a.Extent, b.Extent))
      {
        return a.Extent < b.Extent;
      }

      return !Same(a.SharedLength, b.SharedLength) && a.SharedLength > b.SharedLength;
    }

    /// <summary>A Cancel, or the deadline, as the engine raises it: sometimes bare, sometimes wrapped by a sort
    /// whose comparison watches the token into an InvalidOperationException, and sometimes wrapped by a
    /// parallel loop into an AggregateException of nothing else.</summary>
    internal static bool IsCancellation(Exception ex)
    {
      return ex is OperationCanceledException
        || (ex is InvalidOperationException && ex.InnerException != null && IsCancellation(ex.InnerException))
        || (ex is AggregateException agg
            && agg.Flatten().InnerExceptions.Count > 0
            && agg.Flatten().InnerExceptions.All(IsCancellation));
    }

    private static bool Same(double x, double y)
      => Math.Abs(x - y) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
  }

  /// <summary>Why a timed nest stopped trying rounds.</summary>
  internal enum NestRoundsStop
  {
    /// <summary>Two rounds in a row did not beat the best one.</summary>
    NoImprovement,

    /// <summary>What was left of the time was less than the next round needs.</summary>
    NextRoundWontFit,

    /// <summary>A round was still running when the time ran out, and was thrown away.</summary>
    Deadline,

    /// <summary>The first round alone took longer than the time. It is kept anyway.</summary>
    FirstRoundOverTime,

    FirstRoundFailed,

    Cancelled,

    /// <summary>The best nest so far reached the material usage target, so it was handed over at once.</summary>
    TargetReached,

    /// <summary>The operator pressed Stop: the round still running was thrown away and the best nest handed over.</summary>
    Stopped,
  }

  /// <summary>The time a timed nest is measured with, so the rounds can be tested without waiting.</summary>
  internal interface IRoundClock
  {
    TimeSpan Elapsed { get; }

    /// <summary>Cancels <paramref name="source"/> once <see cref="Elapsed"/> reaches <paramref name="at"/>.</summary>
    void CancelAt(CancellationTokenSource source, TimeSpan at);
  }

  /// <summary>
  /// The time a nest by the clock has, and how it is spent sheet by sheet. The app nests this way when the
  /// operator sets a time per sheet: the search stops on the clock and hands over what it has, so the nest is
  /// on time but two runs, or two machines, can land a little differently.
  /// </summary>
  internal sealed class NestClock
  {
    /// <summary>The least a sheet is given however little time is left: one engine start and a short search.</summary>
    internal static readonly TimeSpan MinSheet = TimeSpan.FromMilliseconds(700);

    /// <summary>What starting the engine, reading its input and writing its answer cost before it searches at
    /// all: about 0.43 s measured on 170 parts, whatever the time asked for.</summary>
    internal static readonly TimeSpan EngineStart = TimeSpan.FromMilliseconds(450);

    /// <summary>The shortest search worth starting the engine for.</summary>
    internal const int MinSearchMs = 150;

    private readonly IRoundClock clock;
    private readonly double secondsPerSheet;
    private readonly Action<TimeSpan>? extended;

    /// <param name="end">The time the job has, from the sheets it was expected to take.</param>
    /// <param name="secondsPerSheet">The time per sheet the operator set; a job that turns out to use more sheets
    /// than expected gets it for each of them (<see cref="CoverSheets"/>). Zero keeps <paramref name="end"/> fixed.</param>
    /// <param name="extended">Told of the new end each time it moves.</param>
    public NestClock(IRoundClock clock, TimeSpan end, double secondsPerSheet = 0, Action<TimeSpan>? extended = null)
    {
      this.clock = clock;
      this.End = end;
      this.secondsPerSheet = secondsPerSheet;
      this.extended = extended;
    }

    public TimeSpan End { get; private set; }

    /// <summary>
    /// The job is using at least <paramref name="sheets"/> sheets, so it gets the time per sheet for each of them:
    /// the expected count is an estimate from the parts' area as if they packed solid, and a job that fills its
    /// sheets less than that would otherwise run out of time on sheets it was never given time for.
    /// </summary>
    public void CoverSheets(int sheets)
    {
      if (this.secondsPerSheet <= 0)
      {
        return;
      }

      TimeSpan covered = TimeSpan.FromSeconds(this.secondsPerSheet * sheets);
      if (covered > this.End)
      {
        this.End = covered;
        this.extended?.Invoke(covered);
      }
    }

    public TimeSpan Elapsed => this.clock.Elapsed;

    public TimeSpan Remaining => this.End - this.clock.Elapsed;

    /// <summary>What is left split evenly over the sheets still expected, never below <paramref name="floor"/>,
    /// so the last sheets are not starved by the first ones running over.</summary>
    internal static TimeSpan SheetShare(TimeSpan remaining, int sheetsLeft, TimeSpan floor)
    {
      TimeSpan even = remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining / Math.Max(1, sheetsLeft);
      return even > floor ? even : floor;
    }

    /// <summary>How long the engine searches on a sheet given <paramref name="share"/>: what is left after
    /// starting it, less what mapping and checking the answer needs (more with common line, where the planner
    /// runs after the search).</summary>
    internal static int SearchMs(TimeSpan share, bool commonLine)
    {
      double afterStart = (share - EngineStart).TotalMilliseconds;
      double fraction = commonLine ? 0.5 : 0.75;
      return (int)Math.Max(MinSearchMs, afterStart * fraction);
    }

    public TimeSpan SheetShare(int sheetsLeft) => SheetShare(this.Remaining, sheetsLeft, MinSheet);
  }

  /// <summary>One nest's score: see <see cref="NestRounds.IsBetter"/>.</summary>
  internal readonly struct NestSummary
  {
    public NestSummary(int placed, double sheetArea, double sharedLength, double extent, int unshared = 0)
    {
      this.Placed = placed;
      this.SheetArea = sheetArea;
      this.SharedLength = sharedLength;
      this.Extent = extent;
      this.Unshared = unshared;
    }

    public int Placed { get; }

    public double SheetArea { get; }

    public double SharedLength { get; }

    public double Extent { get; }

    /// <summary>Parts set to share cuts that share none.</summary>
    public int Unshared { get; }
  }

  /// <summary>What a timed nest did, for the status line.</summary>
  internal sealed class NestRoundsReport
  {
    public TimeSpan MaxTime { get; set; }

    /// <summary>The round whose nest was kept; 0 when none was, or when the opening's was.</summary>
    public int BestRound { get; set; }

    /// <summary>Rounds that ran to the end, not counting the opening. A round cut by the deadline is not one
    /// of them.</summary>
    public int FinishedRounds { get; set; }

    /// <summary>The opening gave a nest, so round 1 was held to the time like the others.</summary>
    public bool OpeningFinished { get; set; }

    /// <summary>The first round took longer than the time the operator picked.</summary>
    public bool OverTime { get; set; }

    public NestRoundsStop StopReason { get; set; }
  }

  internal sealed class StopwatchRoundClock : IRoundClock
  {
    private readonly Stopwatch watch = Stopwatch.StartNew();

    public TimeSpan Elapsed => this.watch.Elapsed;

    public void CancelAt(CancellationTokenSource source, TimeSpan at)
    {
      TimeSpan left = at - this.watch.Elapsed;
      if (left <= TimeSpan.Zero)
      {
        source.Cancel();
      }
      else
      {
        source.CancelAfter(left);
      }
    }
  }
}
