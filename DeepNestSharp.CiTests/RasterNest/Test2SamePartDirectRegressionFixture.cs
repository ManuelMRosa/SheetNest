namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// The operator's own job, set up in the app exactly as he ran it: test2.dxf brought in as a plain
  /// DXF, 26 copies, Same part, 0 and 90 degrees, a 120x60 sheet at a quarter inch. It produced shared
  /// cuts on 4 September and stopped producing any. Every earlier direct-route test nests two parts on
  /// a roomy sheet, which is why none of them noticed.
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public sealed class Test2SamePartDirectRegressionFixture
  {
    private const string DxfDirectoryVariable = "SHEETNEST_MIXED_COMMON_LINE_DXF_DIR";
    private readonly ITestOutputHelper output;

    public Test2SamePartDirectRegressionFixture(ITestOutputHelper output) => this.output = output;

    [Test2SamePartTheory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    public void TwentySixSamePartCopiesStillShareCuts(int quantity)
    {
      string dxfDirectory = Environment.GetEnvironmentVariable(DxfDirectoryVariable);
      string exe = SparrowExe.Resolve();
      var parts = new[]
      {
        new RasterPartInfo
        {
          Path = Path.Combine(dxfDirectory, "test2.dxf"),
          Quantity = quantity,
          Rotations = RotationCodes.RotZeroAnd90,
          Spacing = 0.25D,
          Cc = CommonCuttingMode.SamePart,
          CommonCuttingDomain = "direct",
          RequiresMergedLines = true,
        },
      };

      // The app gives the engine FIVE seconds a sheet (MainWindow.xaml.cs:1590). A bench that hands it
      // ten is measuring a nest the operator never gets.
      NestEffort effort =
        string.Equals(Environment.GetEnvironmentVariable("SHEETNEST_BENCH_EFFORT"), "Normal", StringComparison.OrdinalIgnoreCase)
          ? NestEffort.Normal
          : NestEffort.Fast;

      // DiagCulled is not reset per nest, so it only means anything as a difference. Same for the two
      // millisecond counters, which are worse: they are SUMMED across seeds that run in parallel, so
      // they are four times the wall clock on this job and cannot attribute anything on their own.
      // DiagCommonLineTimings is the one that can: it carries one line per seed.
      long culledBefore = SparrowNestService.DiagCulled;
      long wavesBefore = SparrowNestService.DiagWaves;
      long triesBefore = SparrowNestService.DiagTries;
      long sparrowBefore = SparrowNestService.DiagSparrowMs;
      long mapBefore = SparrowNestService.DiagMapMs;
      SparrowNestService.DiagCommonLineTimings.Clear();
      var clock = System.Diagnostics.Stopwatch.StartNew();
      INestResult result = SparrowNestService.Nest(
        parts,
        sheetWin: 120D,
        sheetHin: 60D,
        rotations: RotationCodes.RotZeroAnd90,
        spacing: 0.25D,
        margin: 0.25D,
        timeLimitSec: 5,
        sparrowExePath: exe,
        error: out string error,
        effort: effort,
        commonLineGap: 0.010D,
        geometryTolerance: 0.0001D);
      clock.Stop();

      result.Should().NotBeNull($"the operator's job must nest (error={error})");
      int placed = result.UsedSheets.Sum(sheet => sheet.PartPlacements.Count);
      int edges = result.UsedSheets.Sum(sheet => sheet.CommonEdges?.Count ?? 0);
      this.output.WriteLine(
        $"TEST2x{quantity}: sheets={result.UsedSheets.Count} placed={placed} edges={edges} " +
        $"snapped={SparrowNestService.DiagSeamsSnapped} given-up={SparrowNestService.DiagSeamsGivenUp} " +
        $"culled={SparrowNestService.DiagCulled - culledBefore} deferred={SparrowNestService.DiagDeferred}");
      this.output.WriteLine(FormattableString.Invariant(
        $"TEST2x{quantity}: wallMs={clock.ElapsedMilliseconds} sparrowSumMs={SparrowNestService.DiagSparrowMs - sparrowBefore} mapSumMs={SparrowNestService.DiagMapMs - mapBefore} waves={SparrowNestService.DiagWaves - wavesBefore} tries={SparrowNestService.DiagTries - triesBefore}"));
      foreach (string timing in SparrowNestService.DiagCommonLineTimings)
      {
        this.output.WriteLine($"TEST2x{quantity}: {timing}");
      }

      // Only the greedy fallback emits these with edges>0, so their presence is the tell that the beam's
      // finalists all failed and the planner took the slow three-pass road.
      foreach (string failure in SparrowNestService.DiagDirectFailures)
      {
        this.output.WriteLine($"TEST2x{quantity}: {failure}");
      }

      // THE SAFETY READING, and it outranks the part count. Two cuts a tenth of a millimetre apart with
      // no shared cut between them cannot be cut at all, so any pair left inside the normal gap without a
      // recorded CommonEdge condemns the layout however many parts it fits.
      int unsafePairs = 0;
      double worst = double.PositiveInfinity;
      foreach (ISheetPlacement sheet in result.UsedSheets)
      {
        var welded = new HashSet<(int, int)>();
        foreach (CommonEdge edge in sheet.CommonEdges ?? Array.Empty<CommonEdge>())
        {
          welded.Add((Math.Min(edge.PartAId, edge.PartBId), Math.Max(edge.PartAId, edge.PartBId)));
        }

        var onSheet = sheet.PartPlacements.ToList();
        for (int i = 0; i < onSheet.Count; i++)
        {
          for (int j = i + 1; j < onSheet.Count; j++)
          {
            if (welded.Contains((Math.Min(onSheet[i].Id, onSheet[j].Id), Math.Max(onSheet[i].Id, onSheet[j].Id))))
            {
              continue;
            }

            // Bisect the real polygon distance so the log carries a number, not just a verdict.
            // TooClose(d) is false while d is below the true distance and true above it, so the answer is
            // the LARGEST d that still reads false. Capped at the nominal gap: anything past that is
            // "comfortably clear" and the exact figure stops mattering.
            double lo = 0D;
            double hi = 0.25D;
            for (int step = 0; step < 14; step++)
            {
              double mid = (lo + hi) / 2D;
              if (PlacementCollision.TooClose(onSheet[i].PlacedPart, onSheet[j].PlacedPart, mid, 0D))
              {
                hi = mid;
              }
              else
              {
                lo = mid;
              }
            }

            worst = Math.Min(worst, lo);
            if (PlacementCollision.TooClose(
              onSheet[i].PlacedPart,
              onSheet[j].PlacedPart,
              0.25D - PlacementCollision.PlacementNoise,
              0D))
            {
              unsafePairs++;
            }
          }
        }
      }

      // A part with no recorded edge at all is one the operator sees sitting on its own in a row of
      // welded neighbours. Counting them is what would have caught this without anyone reading the
      // screen: 27 shared cuts still leaves one part out if they are not the right 27.
      int isolated = 0;
      int biggestChain = 0;
      foreach (ISheetPlacement sheet in result.UsedSheets)
      {
        var peers = sheet.PartPlacements.ToDictionary(p => p.Id, _ => new List<int>());
        foreach (CommonEdge edge in sheet.CommonEdges ?? Array.Empty<CommonEdge>())
        {
          if (peers.ContainsKey(edge.PartAId) && peers.ContainsKey(edge.PartBId))
          {
            peers[edge.PartAId].Add(edge.PartBId);
            peers[edge.PartBId].Add(edge.PartAId);
          }
        }

        isolated += peers.Count(entry => entry.Value.Count == 0);
        var seen = new HashSet<int>();
        foreach (int start in peers.Keys)
        {
          if (!seen.Add(start))
          {
            continue;
          }

          var stack = new Stack<int>();
          stack.Push(start);
          int size = 1;
          while (stack.Count > 0)
          {
            foreach (int next in peers[stack.Pop()])
            {
              if (seen.Add(next))
              {
                size++;
                stack.Push(next);
              }
            }
          }

          biggestChain = Math.Max(biggestChain, size);
        }
      }

      this.output.WriteLine(FormattableString.Invariant(
        $"TEST2x{quantity}: effort={effort} minUnrecordedGap={worst:F5} unsafePairs={unsafePairs} isolated={isolated} biggestChain={biggestChain} ms={clock.ElapsedMilliseconds}"));

      unsafePairs.Should().Be(
        0,
        "a pair left inside the normal gap with no recorded shared cut is two cuts a tenth of a "
        + "millimetre apart, which is not cuttable; fitting more parts never buys that");

      edges.Should().BeGreaterThan(
        0,
        "a sheet of one drawing set to Same part is the job common-line cutting exists for; zero shared "
        + "cuts here is the failure the operator reported, and it is silent in the app");
    }

    private sealed class Test2SamePartTheoryAttribute : TheoryAttribute
    {
      public Test2SamePartTheoryAttribute()
      {
        string directory = Environment.GetEnvironmentVariable(DxfDirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "test2.dxf")))
        {
          this.Skip = $"Set {DxfDirectoryVariable} to the directory containing test2.dxf.";
        }
      }
    }
  }
}
