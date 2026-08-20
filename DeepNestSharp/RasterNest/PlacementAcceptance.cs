namespace DeepNestSharp.RasterNest
{
  using System;
  using System.Collections.Generic;
  using DeepNestLib.Placement;

  /// <summary>
  /// THE rule for "this sheet can be cut": one part per neighbour clearance, one part sheet edge margin.
  /// <para>It lives here, next to <see cref="PlacementCollision"/>, rather than in the viewer that paints
  /// the parts red, because the ENGINE has to be able to ask it too. Until it moved, the engine decided
  /// where parts go with one notion of "far enough apart" and the viewer judged the answer with another:
  /// area threshold against depth threshold, both sides inflated by half against one side inflated by the
  /// whole, tooling footprint against bare outline. Two implementations of the same sentence drift, and
  /// they did. A plain spaced job came out of the engine with three to five pairs the viewer would not
  /// accept, which is what a user finally reported as parts turning red on a nest nobody had touched.</para>
  /// <para>Nothing here is new. It is <c>DxfViewer.FindUnfit</c> moved, verbatim, so that a single
  /// sentence has a single implementation and the producer can be held to it.</para>
  /// </summary>
  internal static class PlacementAcceptance
  {
    /// <summary>
    /// Outside the usable area, which is the sheet less its edge margin on all four sides, the same margin
    /// the nester packed to. Without it a part could sit right on the sheet edge and the nest would quietly
    /// stop honouring what the job asked for.
    /// </summary>
    internal static bool IsOutsideUsableArea(IPartPlacement pp, double sheetWidth, double sheetHeight, double margin)
    {
      var placed = pp.PlacedPart;
      return IsOutsideUsableArea(placed.MinX, placed.MinY, placed.MaxX, placed.MaxY, sheetWidth, sheetHeight, margin);
    }

    /// <summary>The same rule over plain numbers, so the drag's cached bounds are judged by it too. It was
    /// written out twice and the copies drifted: the cached path kept testing the raw sheet edge after the
    /// margin arrived, so a drag went red at a different place than the drop did.</summary>
    internal static bool IsOutsideUsableArea(
      double minX, double minY, double maxX, double maxY, double sheetWidth, double sheetHeight, double margin)
    {
      const double Tol = 0.002;
      double m = Math.Max(0, margin);
      return minX < m - Tol || minY < m - Tol
        || maxX > sheetWidth - m + Tol || maxY > sheetHeight - m + Tol;
    }

    /// <summary>
    /// How many placements on one sheet are not fit to cut. See <see cref="FindUnfit"/>; this is its count.
    /// <para>Takes the sheet's OWN size rather than reading the viewer's: the margin has to be measured
    /// against the sheet a part actually sits on, or every other layout gets judged by the visible one's
    /// dimensions.</para>
    /// </summary>
    internal static int CountUnfit(
      IReadOnlyList<IPartPlacement> placements,
      double sheetWidth,
      double sheetHeight,
      double sheetEdgeMargin,
      Func<IPartPlacement, IPartPlacement, double> clearanceBetween,
      Func<IPartPlacement, IPartPlacement, double> sliverBetween = null)
      => FindUnfit(placements, sheetWidth, sheetHeight, sheetEdgeMargin, clearanceBetween, sliverBetween).All.Count;

    /// <summary>
    /// The placements on one sheet that are not fit to cut, handed back by FAULT rather than as a number:
    /// the two need different things done about them (move a part, or re-nest with a smaller margin), and
    /// whoever is looking has to be able to tell them apart. <see cref="Unfit.All"/> is the union, since a
    /// part can be both on top of a neighbour and in the edge margin.
    /// </summary>
    /// <param name="sliverBetween">How deep a pair may bite into each other before it means anything, the
    /// width of the cut about to run between them. Left out, nothing beyond the noise in the numbers is
    /// forgiven; it used to assume an inch-drawing kerf, which a metric caller had no way to correct.</param>
    internal static Unfit FindUnfit(
      IReadOnlyList<IPartPlacement> placements,
      double sheetWidth,
      double sheetHeight,
      double sheetEdgeMargin,
      Func<IPartPlacement, IPartPlacement, double> clearanceBetween,
      Func<IPartPlacement, IPartPlacement, double> sliverBetween = null)
    {
      var unfit = new Unfit();
      sliverBetween = sliverBetween ?? ((a, b) => PlacementCollision.PlacementNoise);
      for (int i = 0; i < placements.Count; i++)
      {
        var a = placements[i];
        var placedA = a?.PlacedPart;
        if (placedA == null)
        {
          continue;
        }

        if (IsOutsideUsableArea(a, sheetWidth, sheetHeight, sheetEdgeMargin))
        {
          unfit.OutsideMargin.Add(a);
          unfit.All.Add(a);
        }

        for (int j = i + 1; j < placements.Count; j++)
        {
          var b = placements[j];
          if (b?.PlacedPart != null && PlacementCollision.TooClose(placedA, b.PlacedPart, clearanceBetween(a, b), sliverBetween(a, b)))
          {
            unfit.Overlapping.Add(a);
            unfit.Overlapping.Add(b);
            unfit.All.Add(a);
            unfit.All.Add(b);
          }
        }
      }

      return unfit;
    }

    /// <summary>What is wrong on one sheet, split by fault.</summary>
    internal sealed class Unfit
    {
      /// <summary>Sitting closer to a neighbour than the pair's clearance allows. Both sides count, the
      /// same way both go red on screen.</summary>
      public HashSet<IPartPlacement> Overlapping { get; } = new HashSet<IPartPlacement>();

      /// <summary>Off the sheet, or inside the edge margin the job asked to keep clear.</summary>
      public HashSet<IPartPlacement> OutsideMargin { get; } = new HashSet<IPartPlacement>();

      /// <summary>Every part with something wrong with it, counted once.</summary>
      public HashSet<IPartPlacement> All { get; } = new HashSet<IPartPlacement>();
    }
  }
}
