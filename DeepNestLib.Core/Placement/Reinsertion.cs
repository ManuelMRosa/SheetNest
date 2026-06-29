namespace DeepNestLib.Placement
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib.GeneticAlgorithm;

  /// <summary>
  /// Post-placement re-insertion local search. Greedy NFP placement positions each part
  /// optimally only against the parts placed BEFORE it, so an early part can end up forcing
  /// later ones into poor spots and leaving structural voids that translation cannot close.
  /// This pass removes each placed part and re-places it at its best position against ALL the
  /// others (reusing the exact NFP placement core), keeping the move only when it shrinks the
  /// overall bounding box. Repeated until stable, it descends to a tighter layout.
  /// </summary>
  public static class Reinsertion
  {
    /// <summary>Gets or sets a value indicating whether re-insertion runs. Default off => engine behaviour unchanged.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Gets or sets the maximum number of sweeps over all parts.</summary>
    public static int MaxPasses { get; set; } = 3;

    /// <summary>Diagnostics: number of accepted moves in the most recent run.</summary>
    public static int LastMovedCount { get; private set; }

    /// <summary>Diagnostics: fractional bounding-box area reduction in the most recent run.</summary>
    public static double LastImprovement { get; private set; }

    private const double Eps = 1e-6;

    public static void Optimize(List<IPartPlacement> placements, ISheet sheet, IPlacementConfig config, NfpHelper nfpHelper, INestState state, IPlacementWorker placementWorker, DeepNestGene gene)
    {
      if (placements == null || placements.Count < 2)
      {
        return;
      }

      LastMovedCount = 0;
      LastImprovement = 0;
      double startArea = BoundingArea(placements);

      for (int pass = 0; pass < MaxPasses; pass++)
      {
        bool improved = false;

        // Re-place larger parts first; they constrain the layout most.
        var order = placements.OrderByDescending(p => Math.Abs(p.Part.NetArea)).ToList();
        int rotations = Math.Max(1, config.Rotations);
        foreach (var p in order)
        {
          int idx = placements.IndexOf(p);
          if (idx < 0)
          {
            continue;
          }

          var others = placements.Where(x => !ReferenceEquals(x, p)).ToList();

          // Reuse the exact NFP placement core, but against every other part (not just the
          // ones that happened to precede p) and trying every rotation. Disable the clip
          // cache: it assumes the incremental placement order, which re-insertion breaks.
          var ppw = new PartPlacementWorker(placementWorker, config, gene, others, sheet, nfpHelper, state);
          ((ITestPartPlacementWorker)ppw).EnableCaches = false;

          double bestArea = BoundingArea(placements);
          PartPlacement best = null;
          for (int k = 0; k < rotations; k++)
          {
            INfp oriented = k == 0 ? p.Part : p.Part.Rotate(k * (360.0 / rotations));
            PartPlacement candidate;
            try
            {
              candidate = ppw.TryReplace(oriented, 0);
            }
            catch
            {
              candidate = null;
            }

            if (candidate == null)
            {
              continue;
            }

            candidate.Id = p.Id;
            candidate.Source = p.Source;

            // Guarded descent: only accept a strictly tighter overall bounding box.
            double area = AreaWith(others, candidate);
            if (area < bestArea - Eps)
            {
              bestArea = area;
              best = candidate;
            }
          }

          if (best != null)
          {
            placements[idx] = best;
            improved = true;
            LastMovedCount++;
          }
        }

        if (!improved)
        {
          break;
        }
      }

      double endArea = BoundingArea(placements);
      LastImprovement = startArea > Eps ? (startArea - endArea) / startArea : 0;
    }

    private static double BoundingArea(List<IPartPlacement> placements)
    {
      double minX = double.MaxValue;
      double minY = double.MaxValue;
      double maxX = double.MinValue;
      double maxY = double.MinValue;
      foreach (var p in placements)
      {
        if (p.MinX < minX)
        {
          minX = p.MinX;
        }

        if (p.MinY < minY)
        {
          minY = p.MinY;
        }

        if (p.MaxX > maxX)
        {
          maxX = p.MaxX;
        }

        if (p.MaxY > maxY)
        {
          maxY = p.MaxY;
        }
      }

      return (maxX - minX) * (maxY - minY);
    }

    /// <summary>Bounding-box area of <paramref name="others"/> together with one extra placement.</summary>
    private static double AreaWith(List<IPartPlacement> others, IPartPlacement extra)
    {
      double minX = extra.MinX;
      double minY = extra.MinY;
      double maxX = extra.MaxX;
      double maxY = extra.MaxY;
      foreach (var p in others)
      {
        if (p.MinX < minX)
        {
          minX = p.MinX;
        }

        if (p.MinY < minY)
        {
          minY = p.MinY;
        }

        if (p.MaxX > maxX)
        {
          maxX = p.MaxX;
        }

        if (p.MaxY > maxY)
        {
          maxY = p.MaxY;
        }
      }

      return (maxX - minX) * (maxY - minY);
    }
  }
}
