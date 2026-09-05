namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.IO;
  using System.Linq;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>Reproduces the operator's mixed direct-common-line job with the original DXF files.</summary>
  public sealed class MixedCommonLineRegressionFixture
  {
    private const string DxfDirectoryVariable = "SHEETNEST_MIXED_COMMON_LINE_DXF_DIR";
    private readonly ITestOutputHelper output;

    public MixedCommonLineRegressionFixture(ITestOutputHelper output) => this.output = output;

    [MixedCommonLineFact]
    public void FastMixedJobKeepsAtLeastOneValidatedDirectCommonEdge()
    {
      string dxfDirectory = Environment.GetEnvironmentVariable(DxfDirectoryVariable);
      string exe = SparrowExe.Resolve();
      var parts = new[]
      {
        DirectPart(dxfDirectory, "test2.dxf", 12, RotationCodes.RotZeroAnd90),
        DirectPart(dxfDirectory, "Hodgson.dxf", 17, 4),
        new RasterPartInfo
        {
          Path = Path.Combine(dxfDirectory, "inhole.dxf"),
          Quantity = 1,
          Rotations = 1,
          Spacing = 0.25D,
          Cc = CommonCuttingMode.None,
        },
      };

      INestResult result = SparrowNestService.Nest(
        parts,
        sheetWin: 120D,
        sheetHin: 60D,
        rotations: 4,
        spacing: 0.25D,
        margin: 0.25D,
        timeLimitSec: 5,
        sparrowExePath: exe,
        error: out string error,
        effort: NestEffort.Fast,
        commonLineGap: 0.010D,
        geometryTolerance: 0.0001D);

      result.Should().NotBeNull($"the mixed regression must nest (error={error})");
      int placed = result.UsedSheets.Sum(sheet => sheet.PartPlacements.Count);
      int edges = result.UsedSheets.Sum(sheet => sheet.CommonEdges?.Count ?? 0);
      this.output.WriteLine($"MIXED: sheets={result.UsedSheets.Count} placed={placed} edges={edges} given-up={SparrowNestService.DiagSeamsGivenUp}");
      foreach (string failure in SparrowNestService.DiagDirectFailures)
      {
        this.output.WriteLine($"MIXED: {failure}");
      }

      placed.Should().Be(30);
      edges.Should().BeGreaterThan(0,
        "the exact mixed direct-common-line job must preserve at least one validated relationship");
    }

    private static RasterPartInfo DirectPart(string directory, string fileName, int quantity, int rotations)
      => new RasterPartInfo
      {
        Path = Path.Combine(directory, fileName),
        Quantity = quantity,
        Rotations = rotations,
        Spacing = 0.25D,
        Cc = CommonCuttingMode.SamePart,
        CommonCuttingDomain = "direct",
        RequiresMergedLines = true,
      };

    private sealed class MixedCommonLineFactAttribute : FactAttribute
    {
      public MixedCommonLineFactAttribute()
      {
        string directory = Environment.GetEnvironmentVariable(DxfDirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)
          || !new[] { "test2.dxf", "Hodgson.dxf", "inhole.dxf" }
            .All(file => File.Exists(Path.Combine(directory, file))))
        {
          this.Skip = $"Set {DxfDirectoryVariable} to the directory containing test2.dxf, Hodgson.dxf and inhole.dxf.";
        }
      }
    }
  }
}
