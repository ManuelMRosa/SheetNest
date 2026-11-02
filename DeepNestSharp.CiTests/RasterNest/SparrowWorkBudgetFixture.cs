namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Text.Json;
  using System.Text.RegularExpressions;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The engine's work cap, <c>--max-evals</c>, which the opening of a timed nest uses so a hard sheet cannot
  /// take a minute. It has to be as reproducible as the iteration budget it sits on, and it must leave alone
  /// every search that finishes within it: that is what keeps a cheap job's nest exactly as it was.
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public class SparrowWorkBudgetFixture
  {
    private const int Iterations = 150;

    /// <summary>Long thin strips and small squares in a tall strip: the kind of instance on which one
    /// iteration of the search does a great deal of work.</summary>
    private static readonly string Instance = JsonSerializer.Serialize(new
    {
      name = "job",
      strip_height = 7.0,
      items = new object[]
      {
        Item(0, 8, Rectangle(30, 0.8)),
        Item(1, 12, Rectangle(1.2, 1.2)),
      },
    });

    [SparrowTheory]
    [InlineData(10)]
    public void ACapTheSearchStaysInsideChangesNothingAndATighterOneStopsItRepeatably(int fraction)
    {
      string exe = SparrowExe.Resolve();
      var free = Run(exe, null);
      free.Work.Should().BeGreaterThan(fraction * 1000L, "the instance has to be one the cap can bite on");

      // The cap is split between the phases the way the checks are, so it is the share of each phase that
      // has to be out of reach; ten times the whole work puts both well past it.
      var roomy = Run(exe, free.Work * 10);
      roomy.Solution.Should().Be(free.Solution, "a search that never reaches the cap must come out exactly as without it");
      roomy.Work.Should().Be(free.Work);

      long cap = free.Work / fraction;
      var capped = Run(exe, cap);
      capped.Work.Should().BeLessThan(free.Work, "the cap stops the search");
      capped.Work.Should().BeLessThan(cap * 2, "it stops within an iteration of the cap, not at the next outer check");
      Run(exe, cap).Solution.Should().Be(capped.Solution, "the cap counts work, not time, so it lands the same every run");
    }

    private static object Item(int id, int demand, double[][] shape) => new Dictionary<string, object>
    {
      ["id"] = id,
      ["demand"] = demand,
      ["orientation"] = new { rotation = new { mode = "discrete", angles = new[] { 0.0, 90.0 } } },
      ["shape"] = new { type = "simple_polygon", data = shape },
    };

    private static double[][] Rectangle(double w, double h) => new[]
    {
      new[] { 0.0, 0.0 }, new[] { w, 0.0 }, new[] { w, h }, new[] { 0.0, h },
    };

    private static (string Solution, long Work) Run(string exe, long? maxEvals)
    {
      string dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
        File.WriteAllText(Path.Combine(dir, "job.json"), Instance);
        var psi = new ProcessStartInfo(exe)
        {
          WorkingDirectory = dir,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-i", "job.json", "--max-iterations", Iterations.ToString(CultureInfo.InvariantCulture), "-x", "-s", "1", "--workers", "1" })
        {
          psi.ArgumentList.Add(arg);
        }

        if (maxEvals.HasValue)
        {
          psi.ArgumentList.Add("--max-evals");
          psi.ArgumentList.Add(maxEvals.Value.ToString(CultureInfo.InvariantCulture));
        }

        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0);

        // The layout and the length it reaches; the solution also carries the run's own seconds, which no
        // two runs need agree on.
        using var output = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "output", "final_job.json")));
        JsonElement found = output.RootElement.GetProperty("solution");
        string solution = found.GetProperty("strip_width").GetRawText() + found.GetProperty("layout").GetRawText();
        Match work = Regex.Match(stdout, @"work done: (\d+)");
        work.Success.Should().BeTrue("the engine reports the work it did");
        return (solution, long.Parse(work.Groups[1].Value, CultureInfo.InvariantCulture));
      }
      finally
      {
        try
        {
          Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
      }
    }
  }
}
