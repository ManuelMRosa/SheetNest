namespace DeepNestSharp.Ui.UserControls
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Windows;
  using System.Windows.Controls;
  using System.Windows.Input;
  using System.Windows.Media;
  using System.Windows.Threading;
  using DeepNestLib;
  using DeepNestLib.Placement;

  /// <summary>
  /// Faithful nest viewer. Renders each placement's actual nested geometry (PlacedPart — already rotated
  /// and shifted into absolute sheet coordinates) on a Y-flipped, auto-fit canvas, so the preview matches
  /// the nest/export exactly.
  /// </summary>
  public partial class DxfViewer : UserControl
  {
    public static readonly DependencyProperty ResultProperty = DependencyProperty.Register(
      nameof(Result),
      typeof(INestResult),
      typeof(DxfViewer),
      new PropertyMetadata(null, OnResultPropertyChanged));

    public static readonly DependencyProperty SheetIndexProperty = DependencyProperty.Register(
      nameof(SheetIndex),
      typeof(int),
      typeof(DxfViewer),
      new PropertyMetadata(0, OnSheetIndexPropertyChanged));

    // Target on-screen line width in device pixels (constant at any zoom level).
    private const double StrokeScreenPx = 1.0;

    private static readonly Brush SheetFill = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Brush SheetStroke = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush PartStroke = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
    private static readonly Brush PartFill = new SolidColorBrush(Color.FromArgb(0x33, 0x33, 0x99, 0xDD));
    private static readonly Brush HoleFill = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Brush SelectedFill = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x80)); // classic navy selection
    private static readonly Brush InvalidFill = new SolidColorBrush(Color.FromArgb(0x77, 0xD3, 0x2F, 0x2F));

    // Distinct sheet layouts and how many physical sheets use each (the production plan).
    private readonly List<SheetGroup> groups = new List<SheetGroup>();

    private double fittedW = -1;
    private double fittedH = -1;
    private bool isPanning;
    private Point panStart;
    private double panTranslateX;
    private double panTranslateY;

    // Manual nesting state: rendered path per placement, current selection, drag bookkeeping, and
    // the placements currently overlapping something (drawn red).
    private readonly List<(System.Windows.Shapes.Path Path, IPartPlacement Pp)> partPaths = new List<(System.Windows.Shapes.Path, IPartPlacement)>();
    private readonly HashSet<IPartPlacement> invalid = new HashSet<IPartPlacement>();
    private IPartPlacement selectedPp;
    private bool isDraggingPart;
    private bool dragInvalid;
    private Point dragStartCanvas;
    private double dragStartX;
    private double dragStartY;
    private double currentSheetW;
    private double currentSheetH;

    // Undo/redo history of manual edits (cleared when a new result arrives).
    private readonly Stack<EditRecord> undoStack = new Stack<EditRecord>();
    private readonly Stack<EditRecord> redoStack = new Stack<EditRecord>();

    /// <summary>Effective per-part spacing (inches) keyed by the part's source DXF path — set by the
    /// window right before a nest result is shown, so manual edits enforce the same clearances the
    /// nester used: two parts must stay (spacingA + spacingB)/2 apart; common-line parts (0) may touch.</summary>
    public IDictionary<string, double> PartSpacings { get; set; }

    /// <summary>Fallback spacing for parts not found in <see cref="PartSpacings"/>.</summary>
    public double DefaultPartSpacing { get; set; }

    private double SpacingOf(IPartPlacement pp)
    {
      string key = pp?.Part?.Name;
      if (key != null && this.PartSpacings != null && this.PartSpacings.TryGetValue(key, out double s))
      {
        return Math.Max(0, s);
      }

      return Math.Max(0, this.DefaultPartSpacing);
    }

    /// <summary>One manual edit: the placement at Index on the layout's representative sheet went
    /// from Before to After (full snapshots, so undo/redo just re-applies either side).</summary>
    private sealed class EditRecord
    {
      public int GroupIndex;
      public int Index;
      public PlacementSnapshot Before;
      public PlacementSnapshot After;
      public bool Nudge; // consecutive arrow-key nudges of the same part merge into ONE undo step
    }

    private struct PlacementSnapshot
    {
      public INfp Part;
      public double X;
      public double Y;
      public double Rotation;
      public int Source;
      public int Id;

      public static PlacementSnapshot Of(IPartPlacement pp) => new PlacementSnapshot
      {
        Part = pp.Part,
        X = pp.X,
        Y = pp.Y,
        Rotation = pp.Rotation,
        Source = pp.Source,
        Id = pp.Id,
      };

      public PartPlacement ToPlacement() => new PartPlacement(this.Part)
      {
        X = this.X,
        Y = this.Y,
        Rotation = this.Rotation,
        Source = this.Source,
        Id = this.Id,
      };
    }

    public DxfViewer()
    {
      InitializeComponent();
      if (SheetFill.CanFreeze)
      {
        SheetFill.Freeze();
        SheetStroke.Freeze();
        PartStroke.Freeze();
        PartFill.Freeze();
        HoleFill.Freeze();
      }

      this.Loaded += (s, e) => this.FitToView();
      this.host.SizeChanged += (s, e) =>
      {
        if (this.fittedW < 0)
        {
          this.FitToView();
        }
      };
    }

    public INestResult Result
    {
      get => (INestResult)GetValue(ResultProperty);
      set => SetValue(ResultProperty, value);
    }

    public int SheetIndex
    {
      get => (int)GetValue(SheetIndexProperty);
      set => SetValue(SheetIndexProperty, value);
    }

    private static void OnResultPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      var v = (DxfViewer)d;
      v.selectedPp = null;
      v.invalid.Clear();
      v.undoStack.Clear();
      v.redoStack.Clear();
      v.BuildGroups();
      if (v.SheetIndex != 0)
      {
        v.SheetIndex = 0; // back to the first layout on a new result (also renders)
      }

      v.Render();
    }

    /// <summary>
    /// Builds the production plan. For a single repeated geometry the full sheets are forced identical
    /// by taking the fullest nested sheet as the master layout, replicating it across all full sheets,
    /// and showing the leftover as one remainder sheet — so it reads "cut 24 of A + 1 of B" instead of
    /// many near-equal sheets. Mixed jobs fall back to grouping sheets by exact layout.
    /// </summary>
    private void BuildGroups()
    {
      this.groups.Clear();
      var result = this.Result;
      if (result?.UsedSheets == null || result.UsedSheets.Count == 0)
      {
        return;
      }

      // Group physical sheets by their exact layout. The raster engine already emits an industrial
      // plan (k identical pattern sheets + a freshly nested remainder), so this grouping IS the plan —
      // and every layout shown is a real dense nest.
      var byLayout = new List<SheetGroup>();
      var bySignature = new Dictionary<string, SheetGroup>();
      foreach (var sp in result.UsedSheets)
      {
        string sig = Signature(sp);
        if (!bySignature.TryGetValue(sig, out var g))
        {
          g = new SheetGroup { Representative = sp, Count = 0, Name = $"Layout {byLayout.Count + 1}", Members = new List<ISheetPlacement>() };
          bySignature[sig] = g;
          byLayout.Add(g);
        }

        g.Count++;
        g.Members.Add(sp);
      }

      if (byLayout.Count <= 3 || result.UsedSheets.Count <= 1)
      {
        this.groups.AddRange(byLayout);
        return;
      }

      // Many near-equal layouts (the NFP/GA engine packs every sheet slightly differently): condense
      // with the cutting-stock heuristic — but NEVER let the plan prescribe more physical sheets than
      // the nest actually used (it once split a 20-part leftover into phantom 18+2 sheets).
      if (this.TryBuildProductionPlan(result) && this.groups.Sum(g => g.Count) <= result.UsedSheets.Count)
      {
        return;
      }

      this.groups.Clear();
      this.groups.AddRange(byLayout);
    }

    /// <summary>The distinct sheet layouts for the current result — one per production-plan group
    /// ("cut N × this layout"). Used by Export so identical sheets export ONCE (with the plan telling
    /// how many to cut) instead of one DXF per physical sheet.</summary>
    public IReadOnlyList<ISheetPlacement> GetDistinctLayoutSheets()
    {
      return this.groups.Select(g => g.Representative).Where(s => s != null).ToList();
    }

    /// <summary>The full production plan — one entry per distinct layout with its cut count. Used by
    /// the PDF nest report so it mirrors exactly what the viewer shows ("cut N × this layout").</summary>
    public IReadOnlyList<(ISheetPlacement Sheet, int Count, string Name)> GetProductionPlan()
    {
      return this.groups
        .Where(g => g.Representative != null)
        .Select(g => (g.Representative, g.Count, g.Name))
        .ToList();
    }

    /// <summary>
    /// Cutting-stock plan (Gilmore–Gomory style, simplified). Uses the distinct sheet layouts the
    /// nester already found as a pool of candidate patterns, then greedily picks the combination that
    /// covers the whole order in the fewest sheets — repeatedly taking the pattern that places the most
    /// parts as full repeats, then a dense remainder. Result: few repeating patterns, minimal leftover.
    /// </summary>
    private bool TryBuildProductionPlan(INestResult result)
    {
      // Candidate pattern pool: one real (dense) sheet per distinct composition.
      var pool = new List<(ISheetPlacement Sheet, Dictionary<int, int> Comp, int Parts)>();
      var seen = new HashSet<string>();
      foreach (var sp in result.UsedSheets)
      {
        var comp = CompositionOf(sp);
        if (comp.Count > 0 && seen.Add(CompKey(comp)))
        {
          pool.Add((sp, comp, sp.PartPlacements.Count));
        }
      }

      if (pool.Count == 0)
      {
        return false;
      }

      // Demand per part geometry.
      var remaining = new Dictionary<int, int>();
      foreach (var sp in result.UsedSheets)
      {
        foreach (var pp in sp.PartPlacements)
        {
          remaining.TryGetValue(pp.Source, out int n);
          remaining[pp.Source] = n + 1;
        }
      }

      // Single part geometry: the clean industrial answer is ALWAYS "N identical full sheets + one
      // remainder" — never a fragmented mix. The general pool greedy below can splinter the leftover
      // across several tiny layouts because the GA's sheets vary in count (e.g. 802 → 24 + 8 + 2 = 3
      // layouts). Here we fix the full sheet at the densest count and put the rest on one remainder.
      var activeSources = remaining.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
      if (activeSources.Count == 1)
      {
        int src = activeSources[0];
        var densest = pool.OrderByDescending(p => p.Parts).First();
        int fullParts = densest.Parts;
        if (fullParts > 0)
        {
          int total = remaining[src];
          int fullSheets = total / fullParts;
          int rem = total - (fullSheets * fullParts);

          if (fullSheets > 0)
          {
            this.groups.Add(new SheetGroup { Representative = densest.Sheet, Count = fullSheets, Name = "Full sheet" });
          }

          if (rem > 0)
          {
            var take = new Dictionary<int, int> { { src, rem } };
            var remSheet = new SheetPlacement(densest.Sheet.PlacementType, densest.Sheet.Sheet, SelectPlacements(densest.Sheet, take), 0, SvgNest.Config.ClipperScale);
            this.groups.Add(new SheetGroup { Representative = remSheet, Count = 1, Name = "Remainder sheet" });
          }

          return this.groups.Count > 0;
        }
      }

      var remGroups = new Dictionary<string, SheetGroup>();
      int guard = 0;
      while (remaining.Values.Any(v => v > 0) && guard++ < 5000)
      {
        // Pick the DENSEST pattern (most parts per sheet) that still fits the remaining demand at least
        // once, then lay down as many whole copies as fit. Replicating the densest sheet minimises the
        // total sheet count — the standard cutting-stock heuristic (prefer low-trim, high-frequency
        // patterns). The old metric (copies*Parts) instead favoured the SPARSEST sheet: a near-empty
        // partial sheet "covers" all demand when replicated, so 800 parts could give "100 × 8 parts" —
        // but ONLY when the quantity left a partial sheet, which is why it broke for some quantities only.
        (ISheetPlacement Sheet, Dictionary<int, int> Comp, int Parts) best = default;
        int bestCopies = 0;
        int bestParts = 0;
        foreach (var p in pool)
        {
          int copies = MaxCopies(p.Comp, remaining);
          if (copies >= 1 && p.Parts > bestParts)
          {
            bestParts = p.Parts;
            best = p;
            bestCopies = copies;
          }
        }

        if (bestCopies >= 1)
        {
          this.groups.Add(new SheetGroup { Representative = best.Sheet, Count = bestCopies, Name = "Full sheet" });
          foreach (var kv in best.Comp)
          {
            remaining[kv.Key] -= kv.Value * bestCopies;
          }

          continue;
        }

        // No whole pattern fits the leftover — make one dense remainder sheet from the best-covering pattern.
        var fit = pool.OrderByDescending(p => UsableParts(p.Comp, remaining)).First();
        var take = new Dictionary<int, int>();
        foreach (var kv in fit.Comp)
        {
          if (remaining.TryGetValue(kv.Key, out int r) && r > 0)
          {
            take[kv.Key] = Math.Min(kv.Value, r);
          }
        }

        if (take.Count == 0)
        {
          break;
        }

        string key = CompKey(take);
        if (!remGroups.TryGetValue(key, out var g))
        {
          var sheet = new SheetPlacement(fit.Sheet.PlacementType, fit.Sheet.Sheet, SelectPlacements(fit.Sheet, take), 0, SvgNest.Config.ClipperScale);
          g = new SheetGroup { Representative = sheet, Count = 0, Name = "Remainder sheet" };
          remGroups[key] = g;
          this.groups.Add(g);
        }

        g.Count++;
        foreach (var kv in take)
        {
          remaining[kv.Key] -= kv.Value;
        }
      }

      return this.groups.Count > 0;
    }

    private static int MaxCopies(Dictionary<int, int> comp, Dictionary<int, int> remaining)
    {
      int k = int.MaxValue;
      foreach (var kv in comp)
      {
        remaining.TryGetValue(kv.Key, out int r);
        k = Math.Min(k, r / kv.Value);
      }

      return k == int.MaxValue ? 0 : k;
    }

    private static int UsableParts(Dictionary<int, int> comp, Dictionary<int, int> remaining)
    {
      int sum = 0;
      foreach (var kv in comp)
      {
        remaining.TryGetValue(kv.Key, out int r);
        sum += Math.Min(kv.Value, r);
      }

      return sum;
    }

    private static string CompKey(Dictionary<int, int> comp)
    {
      return string.Join(",", comp.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"));
    }

    private static Dictionary<int, int> CompositionOf(ISheetPlacement sheet)
    {
      var comp = new Dictionary<int, int>();
      if (sheet?.PartPlacements != null)
      {
        foreach (var pp in sheet.PartPlacements)
        {
          comp.TryGetValue(pp.Source, out int n);
          comp[pp.Source] = n + 1;
        }
      }

      return comp;
    }

    private static List<IPartPlacement> SelectPlacements(ISheetPlacement master, Dictionary<int, int> take)
    {
      var need = new Dictionary<int, int>(take);
      var list = new List<IPartPlacement>();
      foreach (var pp in master.PartPlacements)
      {
        if (need.TryGetValue(pp.Source, out int n) && n > 0)
        {
          list.Add(pp);
          need[pp.Source] = n - 1;
        }
      }

      return list;
    }

    private static string Signature(ISheetPlacement sp)
    {
      var items = sp.PartPlacements
        .Select(p => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.Source}:{Math.Round(p.X, 1)}:{Math.Round(p.Y, 1)}:{Math.Round(p.Rotation, 1)}"))
        .OrderBy(s => s, StringComparer.Ordinal);

      // Sheet size is part of the layout identity: with mixed stock, the same corner-packed parts on
      // a 120x60 and on a 60x60 are DIFFERENT cuts and must not group as one.
      return string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{sp.Sheet.WidthCalculated}x{sp.Sheet.HeightCalculated}#{string.Join("|", items)}");
    }

    /// <summary>Material utilization of ONE sheet layout: net part area on it / its sheet area (%).</summary>
    private static double UtilizationOf(ISheetPlacement sp)
    {
      double sheetArea = sp?.Sheet == null ? 0 : sp.Sheet.WidthCalculated * sp.Sheet.HeightCalculated;
      if (sheetArea <= 0)
      {
        return 0;
      }

      double partsArea = sp.PartPlacements.Sum(p => Math.Abs(p.Part.NetArea));
      return partsArea / sheetArea * 100.0;
    }

    private static void OnSheetIndexPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
      ((DxfViewer)d).Render();
    }

    private void OnPrevSheet(object sender, RoutedEventArgs e)
    {
      if (this.SheetIndex > 0)
      {
        this.SheetIndex--;
      }
    }

    private void OnNextSheet(object sender, RoutedEventArgs e)
    {
      if (this.SheetIndex < this.groups.Count - 1)
      {
        this.SheetIndex++;
      }
    }

    private sealed class SheetGroup
    {
      public ISheetPlacement Representative { get; set; }

      public int Count { get; set; }

      public string Name { get; set; }

      /// <summary>The physical sheets sharing this layout (null for synthetic plan groups) — manual
      /// edits to the representative are mirrored onto every member so all copies stay identical.</summary>
      public List<ISheetPlacement> Members { get; set; }
    }

    private void Render()
    {
      this.canvas.Children.Clear();
      this.partPaths.Clear();

      var result = this.Result;
      if (result == null || this.groups.Count == 0)
      {
        this.emptyHint.Visibility = Visibility.Visible;
        this.sheetNav.Visibility = Visibility.Collapsed;
        this.editBar.Visibility = Visibility.Collapsed;
        return;
      }

      this.editBar.Visibility = Visibility.Visible;

      int idx = Math.Max(0, Math.Min(this.SheetIndex, this.groups.Count - 1));
      var group = this.groups[idx];
      var sheetPlacement = group.Representative;
      var sheet = sheetPlacement.Sheet;
      if (sheet == null)
      {
        this.emptyHint.Visibility = Visibility.Visible;
        return;
      }

      this.emptyHint.Visibility = Visibility.Collapsed;

      // Navigate by distinct LAYOUT (not by physical sheet), labelled the way industrial nesting
      // packages do it (SigmaNEST: one repeated layout shown with its repeat count in parentheses
      // next to the name): "Nest 1/2 (30)" = layout 1 of 2, cut it 30 times. Job totals stay in the
      // status bar and results grid — nothing verbose over the drawing.
      this.sheetLabel.Text = group.Count > 1
        ? $"Nest {idx + 1}/{this.groups.Count}  ({group.Count})"
        : $"Nest {idx + 1}/{this.groups.Count}";
      this.sheetNav.Visibility = this.groups.Count > 1 || group.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

      double w = sheet.WidthCalculated;
      double h = sheet.HeightCalculated;
      this.currentSheetW = w;
      this.currentSheetH = h;
      this.canvas.Width = w;
      this.canvas.Height = h;

      // Selection belongs to a specific sheet's placements — drop it when another sheet is shown.
      if (this.selectedPp != null && !sheetPlacement.PartPlacements.Contains(this.selectedPp))
      {
        this.selectedPp = null;
      }

      this.UpdateEditButtons();

      // Hairline strokes: constant ~1px on screen at any zoom (stroke is in canvas units, which the
      // RenderTransform scales, so divide by the current scale).
      double stroke = StrokeScreenPx / Math.Max(0.0001, this.scale.ScaleX);

      // Re-fit only when the sheet size changes (e.g. a new job), so the user's zoom/pan is kept
      // while flicking through results of the same nest.
      if (w != this.fittedW || h != this.fittedH)
      {
        this.fittedW = w;
        this.fittedH = h;
        this.Dispatcher.BeginInvoke(new Action(this.FitToView), DispatcherPriority.Loaded);
      }

      // Sheet background + outline.
      var sheetGeo = new RectangleGeometry(new Rect(0, 0, w, h));
      this.canvas.Children.Add(new System.Windows.Shapes.Path
      {
        Data = sheetGeo,
        Fill = SheetFill,
        Stroke = SheetStroke,
        StrokeThickness = stroke,
      });

      foreach (var pp in sheetPlacement.PartPlacements)
      {
        try
        {
          var geometry = BuildPlacedGeometry(pp, h);
          if (geometry == null)
          {
            continue;
          }

          var path = new System.Windows.Shapes.Path
          {
            Data = geometry,
            Fill = this.FillFor(pp),
            Stroke = PartStroke,
            StrokeThickness = stroke,
            StrokeLineJoin = PenLineJoin.Round,
          };
          this.canvas.Children.Add(path);
          this.partPaths.Add((path, pp));
        }
        catch
        {
          // Skip a part that can't be rendered rather than blanking the whole view.
        }
      }
    }

    private Brush FillFor(IPartPlacement pp)
    {
      return this.invalid.Contains(pp) ? InvalidFill : pp == this.selectedPp ? SelectedFill : PartFill;
    }

    /// <summary>
    /// Builds the WPF geometry for one placed part (outline + holes) in sheet coordinates,
    /// Y flipped to screen space, from the placement's own nested geometry.
    /// </summary>
    private static Geometry BuildPlacedGeometry(IPartPlacement pp, double sheetHeight)
    {
      // Render pp.PlacedPart DIRECTLY — it is the actual nested geometry (already rotated + shifted into
      // absolute sheet coords), so the preview EXACTLY matches the nest/export (verified: the placed
      // polygons never overlap). The previous approach reloaded the original DXF and re-centred it, which
      // DRIFTED for non-90° angles / any frame mismatch between the two loaders, making parts LOOK
      // overlapped and gapped even though the real nest was correct. (Trade-off: curves show the nested
      // tessellation rather than a fresh fine re-tessellation; exact for straight-edged parts.)
      INfp source = pp.PlacedPart;
      if (source == null)
      {
        return Geometry.Empty;
      }

      Point Tx(double x, double y)
      {
        return new Point(x, sheetHeight - y); // already absolute sheet coords; just flip Y (DXF up -> screen down)
      }

      var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
      using (var ctx = geometry.Open())
      {
        AppendContour(ctx, source, Tx);
        if (source.Children != null)
        {
          foreach (var child in source.Children)
          {
            AppendContour(ctx, child, Tx);
          }
        }
      }

      geometry.Freeze();
      return geometry;
    }

    private static void AppendContour(StreamGeometryContext ctx, INfp contour, Func<double, double, Point> tx)
    {
      if (contour == null || contour.Length < 2)
      {
        return;
      }

      var first = tx(contour[0].X, contour[0].Y);
      ctx.BeginFigure(first, true, true); // filled, closed
      var points = new List<Point>(contour.Length - 1);
      for (int i = 1; i < contour.Length; i++)
      {
        points.Add(tx(contour[i].X, contour[i].Y));
      }

      ctx.PolyLineTo(points, true, true);
    }

    private void FitToView()
    {
      if (this.canvas.Width <= 0 || this.canvas.Height <= 0)
      {
        return;
      }

      double availW = this.host.ActualWidth;
      double availH = this.host.ActualHeight;
      if (availW <= 0 || availH <= 0)
      {
        return;
      }

      double s = Math.Min(availW / this.canvas.Width, availH / this.canvas.Height) * 0.92;
      if (s <= 0 || double.IsInfinity(s) || double.IsNaN(s))
      {
        s = 1;
      }

      this.scale.ScaleX = s;
      this.scale.ScaleY = s;
      this.translate.X = (availW - (this.canvas.Width * s)) / 2.0;
      this.translate.Y = (availH - (this.canvas.Height * s)) / 2.0;
      this.UpdateStrokeWidths();
    }

    /// <summary>Keeps every drawn line at ~StrokeScreenPx device pixels regardless of zoom.</summary>
    private void UpdateStrokeWidths()
    {
      double t = StrokeScreenPx / Math.Max(0.0001, this.scale.ScaleX);
      foreach (var child in this.canvas.Children)
      {
        if (child is System.Windows.Shapes.Path path)
        {
          path.StrokeThickness = t;
        }
      }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
      double oldScale = this.scale.ScaleX;
      double factor = e.Delta > 0 ? 1.2 : 1.0 / 1.2;
      double newScale = Math.Max(0.02, Math.Min(400.0, oldScale * factor));
      factor = newScale / oldScale;

      // Keep the point under the cursor fixed: screen = content * scale + translate.
      Point p = e.GetPosition(this.host);
      this.translate.X = p.X - ((p.X - this.translate.X) * factor);
      this.translate.Y = p.Y - ((p.Y - this.translate.Y) * factor);
      this.scale.ScaleX = newScale;
      this.scale.ScaleY = newScale;
      this.UpdateStrokeWidths();
      e.Handled = true;
    }

    private bool EditMode => this.editToggle.IsChecked == true;

    private void OnEditModeChanged(object sender, RoutedEventArgs e)
    {
      this.SelectPart(null);
      if (this.hintText != null)
      {
        this.hintText.Text = this.EditMode
          ? "click = select part  ·  drag = move  ·  buttons = rotate  ·  overlaps show red"
          : "scroll = zoom  ·  drag = pan  ·  right-click = fit";
      }
    }

    private void SelectPart(IPartPlacement pp)
    {
      this.selectedPp = pp;
      foreach (var (path, p) in this.partPaths)
      {
        path.Fill = this.FillFor(p);
      }

      this.UpdateEditButtons();
    }

    private void UpdateEditButtons()
    {
      if (this.rotateButtons != null)
      {
        this.rotateButtons.Visibility = this.EditMode && this.selectedPp != null ? Visibility.Visible : Visibility.Collapsed;
      }

      if (this.undoBtn != null)
      {
        this.undoBtn.IsEnabled = this.undoStack.Count > 0;
        this.redoBtn.IsEnabled = this.redoStack.Count > 0;
      }
    }

    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
      this.host.Focus(); // so Ctrl+Z / Ctrl+Y reach the viewer

      if (this.EditMode)
      {
        // Topmost part under the cursor gets selected and dragged; empty space falls through to pan.
        Point pt = e.GetPosition(this.canvas);
        for (int i = this.partPaths.Count - 1; i >= 0; i--)
        {
          if (this.partPaths[i].Path.Data != null && this.partPaths[i].Path.Data.FillContains(pt))
          {
            this.SelectPart(this.partPaths[i].Pp);
            this.isDraggingPart = true;
            this.dragInvalid = false;
            this.dragStartCanvas = pt;
            this.dragStartX = this.selectedPp.X;
            this.dragStartY = this.selectedPp.Y;
            this.host.CaptureMouse();
            this.host.Cursor = Cursors.Hand;
            return;
          }
        }

        this.SelectPart(null);
      }

      this.isPanning = true;
      this.panStart = e.GetPosition(this.host);
      this.panTranslateX = this.translate.X;
      this.panTranslateY = this.translate.Y;
      this.host.CaptureMouse();
      this.host.Cursor = Cursors.SizeAll;
    }

    private void OnPanMove(object sender, MouseEventArgs e)
    {
      if (this.isDraggingPart && this.selectedPp != null)
      {
        // Canvas units are sheet units; canvas Y runs downward, sheet Y upward.
        Point pt = e.GetPosition(this.canvas);
        this.selectedPp.X = this.dragStartX + (pt.X - this.dragStartCanvas.X);
        this.selectedPp.Y = this.dragStartY - (pt.Y - this.dragStartCanvas.Y);
        this.RefreshSelectedPath();

        // Live feedback: red while the current position overlaps a part or leaves the sheet
        // (dropping there is refused — the part snaps back).
        this.dragInvalid = !this.IsPositionValid(this.selectedPp, this.selectedPp);
        this.SetSelectedFill(this.dragInvalid ? InvalidFill : SelectedFill);
        return;
      }

      if (!this.isPanning)
      {
        return;
      }

      Point p = e.GetPosition(this.host);
      this.translate.X = this.panTranslateX + (p.X - this.panStart.X);
      this.translate.Y = this.panTranslateY + (p.Y - this.panStart.Y);
    }

    /// <summary>
    /// Finds the valid position nearest the drop point along the drag path (coarse scan from the drop
    /// backward, then binary refinement toward the drop) and leaves the part there. t=0 (the drag
    /// start) is always valid, so the part never ends up in an illegal spot.
    /// </summary>
    private void ResolveDropToContact()
    {
      double sx = this.dragStartX;
      double sy = this.dragStartY;
      double ex = this.selectedPp.X;
      double ey = this.selectedPp.Y;

      bool ValidAt(double t)
      {
        this.selectedPp.X = sx + (t * (ex - sx));
        this.selectedPp.Y = sy + (t * (ey - sy));
        return this.IsPositionValid(this.selectedPp, this.selectedPp);
      }

      const int CoarseSteps = 48;
      double lo = 0; // known valid
      double hi = 1; // known invalid (that's why we're here)
      for (int i = CoarseSteps - 1; i >= 1; i--)
      {
        double t = (double)i / CoarseSteps;
        if (ValidAt(t))
        {
          lo = t;
          hi = (double)(i + 1) / CoarseSteps;
          break;
        }
      }

      for (int i = 0; i < 20; i++)
      {
        double mid = (lo + hi) / 2.0;
        if (ValidAt(mid))
        {
          lo = mid;
        }
        else
        {
          hi = mid;
        }
      }

      ValidAt(lo); // land on the best known-valid position
      this.RefreshSelectedPath();
    }

    private void RefreshSelectedPath()
    {
      for (int i = 0; i < this.partPaths.Count; i++)
      {
        if (this.partPaths[i].Pp == this.selectedPp)
        {
          this.partPaths[i].Path.Data = BuildPlacedGeometry(this.selectedPp, this.currentSheetH);
          return;
        }
      }
    }

    private void OnPanEnd(object sender, MouseButtonEventArgs e)
    {
      if (this.isDraggingPart)
      {
        this.isDraggingPart = false;
        this.host.ReleaseMouseCapture();
        this.host.Cursor = null;

        if (this.dragInvalid)
        {
          // Dropped too close / on top of a neighbour: slide back along the drag path only as far as
          // needed, so the part settles at its required clearance — touching for common-line parts,
          // (spacingA + spacingB)/2 otherwise. Worst case it returns to where the drag began.
          this.dragInvalid = false;
          this.ResolveDropToContact();
        }

        bool moved = Math.Abs(this.selectedPp.X - this.dragStartX) > 1e-9 || Math.Abs(this.selectedPp.Y - this.dragStartY) > 1e-9;
        if (moved)
        {
          var after = PlacementSnapshot.Of(this.selectedPp);
          var before = after;
          before.X = this.dragStartX;
          before.Y = this.dragStartY;
          this.PushEdit(before, after, nudge: false);
        }

        this.SetSelectedFill(SelectedFill);
        this.CommitManualEdit();
        return;
      }

      this.isPanning = false;
      this.host.ReleaseMouseCapture();
      this.host.Cursor = Cursors.Arrow;
    }

    private void OnResetView(object sender, MouseButtonEventArgs e)
    {
      this.FitToView();
      e.Handled = true;
    }

    private void OnRotateCw90(object sender, RoutedEventArgs e) => this.RotateSelected(90);

    private void OnRotateCcw90(object sender, RoutedEventArgs e) => this.RotateSelected(-90);

    private void OnRotateCw5(object sender, RoutedEventArgs e) => this.RotateSelected(5);

    private void OnRotateCcw5(object sender, RoutedEventArgs e) => this.RotateSelected(-5);

    /// <summary>
    /// Rotates the selected part in place about its bounding-box centre. The placement is REPLACED
    /// (PartPlacement.Part is immutable) with one whose Part is the newly rotated polygon and whose
    /// Rotation is updated — keeping the invariant the DXF export relies on (rotate original by
    /// Rotation, then shift by X/Y).
    /// </summary>
    private void RotateSelected(double deltaDeg)
    {
      var pp = this.selectedPp;
      var group = this.CurrentGroup();
      if (pp == null || group == null)
      {
        return;
      }

      var sp = group.Representative;
      int index = -1;
      for (int i = 0; i < sp.PartPlacements.Count; i++)
      {
        if (sp.PartPlacements[i] == pp)
        {
          index = i;
          break;
        }
      }

      if (index < 0 || !(sp.PartPlacements is IList<IPartPlacement> list) || list.IsReadOnly)
      {
        return;
      }

      double cx = ((pp.Part.MinX + pp.Part.MaxX) / 2.0) + pp.X;
      double cy = ((pp.Part.MinY + pp.Part.MaxY) / 2.0) + pp.Y;
      var newPart = pp.Part.Rotate(deltaDeg);
      var replacement = new PartPlacement(newPart)
      {
        X = cx - ((newPart.MinX + newPart.MaxX) / 2.0),
        Y = cy - ((newPart.MinY + newPart.MaxY) / 2.0),
        Rotation = (((pp.Rotation + deltaDeg) % 360.0) + 360.0) % 360.0,
        Source = pp.Source,
        Id = pp.Id,
      };

      // Overlaps are not allowed: refuse the rotation (brief red flash) if the turned part would
      // collide with a neighbour or leave the sheet.
      if (!this.IsPositionValid(replacement, pp))
      {
        this.FlashSelectedInvalid();
        return;
      }

      this.PushEdit(PlacementSnapshot.Of(pp), PlacementSnapshot.Of(replacement), nudge: false);
      list[index] = replacement;
      this.invalid.Remove(pp);
      for (int i = 0; i < this.partPaths.Count; i++)
      {
        if (this.partPaths[i].Pp == pp)
        {
          this.partPaths[i] = (this.partPaths[i].Path, replacement);
          break;
        }
      }

      this.selectedPp = replacement;
      this.RefreshSelectedPath();
      this.CommitManualEdit();
    }

    /// <summary>True when the candidate placement neither overlaps any other part nor leaves the sheet.
    /// <paramref name="exclude"/> is the placement the candidate replaces (skipped in the pair tests).</summary>
    private bool IsPositionValid(IPartPlacement candidate, IPartPlacement exclude)
    {
      if (this.OutOfSheet(candidate))
      {
        return false;
      }

      var group = this.CurrentGroup();
      if (group == null)
      {
        return true;
      }

      foreach (var other in group.Representative.PartPlacements)
      {
        if (other == exclude || other == candidate)
        {
          continue;
        }

        if (this.TooClose(candidate, other))
        {
          return false;
        }
      }

      return true;
    }

    /// <summary>
    /// True when the two parts are closer than their required clearance: (spacingA + spacingB) / 2,
    /// the same rule the nester enforces. Common-line parts (spacing 0) may touch — only real overlap
    /// fails. Tested as: A's outline grown by the clearance intersects B.
    /// </summary>
    private bool TooClose(IPartPlacement a, IPartPlacement b)
    {
      double clearance = (this.SpacingOf(a) + this.SpacingOf(b)) / 2.0;
      if (clearance <= 0)
      {
        // Common-line pair: keep the CAM-safe mini-gap (coincident lines get merged/deleted by CAM).
        clearance = RasterNest.RasterCompact.CommonLineGap;
      }

      var pa = a.PlacedPart;
      var pb = b.PlacedPart;
      if (pa.MaxX + clearance <= pb.MinX || pb.MaxX <= pa.MinX - clearance
          || pa.MaxY + clearance <= pb.MinY || pb.MaxY <= pa.MinY - clearance)
      {
        return false;
      }

      var pathsA = ToClipperPaths(pa);
      if (clearance > 0)
      {
        pathsA = InflateOuter(pathsA, clearance);
      }

      return PathsOverlap(pathsA, ToClipperPaths(pb));
    }

    private void SetSelectedFill(Brush fill)
    {
      foreach (var (path, p) in this.partPaths)
      {
        if (p == this.selectedPp)
        {
          path.Fill = fill;
          return;
        }
      }
    }

    private void FlashSelectedInvalid()
    {
      this.SetSelectedFill(InvalidFill);
      var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
      timer.Tick += (s, e) =>
      {
        timer.Stop();
        this.SetSelectedFill(this.selectedPp != null ? SelectedFill : PartFill);
      };
      timer.Start();
    }

    private void PushEdit(PlacementSnapshot before, PlacementSnapshot after, bool nudge)
    {
      var group = this.CurrentGroup();
      if (group == null)
      {
        return;
      }

      int index = -1;
      for (int i = 0; i < group.Representative.PartPlacements.Count; i++)
      {
        if (group.Representative.PartPlacements[i] == this.selectedPp)
        {
          index = i;
          break;
        }
      }

      if (index < 0)
      {
        return;
      }

      int groupIndex = Math.Max(0, Math.Min(this.SheetIndex, this.groups.Count - 1));

      // A run of arrow-key nudges on the same part is ONE undo step (else Ctrl+Z crawls pixel by pixel).
      if (nudge && this.undoStack.Count > 0)
      {
        var top = this.undoStack.Peek();
        if (top.Nudge && top.GroupIndex == groupIndex && top.Index == index)
        {
          top.After = after;
          this.redoStack.Clear();
          this.UpdateEditButtons();
          return;
        }
      }

      this.undoStack.Push(new EditRecord
      {
        GroupIndex = groupIndex,
        Index = index,
        Before = before,
        After = after,
        Nudge = nudge,
      });
      this.redoStack.Clear();
      this.UpdateEditButtons();
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => this.Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => this.Redo();

    private void OnViewerKeyDown(object sender, KeyEventArgs e)
    {
      if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
      {
        this.Undo();
        e.Handled = true;
      }
      else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
      {
        this.Redo();
        e.Handled = true;
      }
      else if (this.EditMode && this.selectedPp != null
               && (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down))
      {
        // Fine positioning: arrows nudge the selected part (Shift = coarser). Screen-up is +Y in sheet
        // coordinates (the canvas is Y-flipped). A nudge into another part's clearance is simply refused.
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.25 : 0.05;
        double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
        double dy = e.Key == Key.Up ? step : e.Key == Key.Down ? -step : 0;
        this.NudgeSelected(dx, dy);
        e.Handled = true;
      }
    }

    private void NudgeSelected(double dx, double dy)
    {
      var pp = this.selectedPp;
      if (pp == null)
      {
        return;
      }

      var before = PlacementSnapshot.Of(pp);
      pp.X += dx;
      pp.Y += dy;

      if (!this.IsPositionValid(pp, pp))
      {
        pp.X = before.X;
        pp.Y = before.Y;
        this.FlashSelectedInvalid();
        return;
      }

      this.RefreshSelectedPath();
      this.PushEdit(before, PlacementSnapshot.Of(pp), nudge: true);
      this.CommitManualEdit();
    }

    private void Undo()
    {
      if (this.undoStack.Count == 0)
      {
        return;
      }

      var record = this.undoStack.Pop();
      this.redoStack.Push(record);
      this.ApplyRecord(record, useBefore: true);
    }

    private void Redo()
    {
      if (this.redoStack.Count == 0)
      {
        return;
      }

      var record = this.redoStack.Pop();
      this.undoStack.Push(record);
      this.ApplyRecord(record, useBefore: false);
    }

    private void ApplyRecord(EditRecord record, bool useBefore)
    {
      if (record.GroupIndex >= this.groups.Count)
      {
        return;
      }

      var group = this.groups[record.GroupIndex];
      var sp = group.Representative;
      if (record.Index >= sp.PartPlacements.Count || !(sp.PartPlacements is IList<IPartPlacement> list) || list.IsReadOnly)
      {
        return;
      }

      var snapshot = useBefore ? record.Before : record.After;
      var placement = snapshot.ToPlacement();
      list[record.Index] = placement;
      this.selectedPp = placement;
      this.invalid.Clear();

      // Mirror onto the layout's copies, then show the sheet the edit belongs to.
      if (group.Members != null)
      {
        foreach (var member in group.Members)
        {
          if (member != sp && member.PartPlacements.Count == sp.PartPlacements.Count
              && member.PartPlacements is IList<IPartPlacement> mlist && !mlist.IsReadOnly)
          {
            for (int i = 0; i < sp.PartPlacements.Count; i++)
            {
              mlist[i] = sp.PartPlacements[i];
            }
          }
        }
      }

      if (this.SheetIndex != record.GroupIndex)
      {
        this.SheetIndex = record.GroupIndex; // triggers a full re-render on that sheet
      }
      else
      {
        this.Render();
      }

      this.UpdateEditButtons();
    }

    private SheetGroup CurrentGroup()
    {
      int idx = Math.Max(0, Math.Min(this.SheetIndex, this.groups.Count - 1));
      return this.groups.Count == 0 ? null : this.groups[idx];
    }

    /// <summary>
    /// After a manual move/rotate: mirror the representative's placements onto every physical copy of
    /// this layout (so "cut ×24" copies stay identical and the export matches), then re-evaluate which
    /// parts overlap or hang off the sheet (drawn red — the operator decides what to do about it).
    /// </summary>
    private void CommitManualEdit()
    {
      var group = this.CurrentGroup();
      if (group == null)
      {
        return;
      }

      if (group.Members != null)
      {
        var rep = group.Representative;
        foreach (var member in group.Members)
        {
          if (member == rep || member.PartPlacements.Count != rep.PartPlacements.Count)
          {
            continue;
          }

          if (member.PartPlacements is IList<IPartPlacement> mlist && !mlist.IsReadOnly)
          {
            for (int i = 0; i < rep.PartPlacements.Count; i++)
            {
              mlist[i] = rep.PartPlacements[i]; // share the exact placements — copies stay in lockstep
            }
          }
        }
      }

      this.RefreshInvalid();
    }

    /// <summary>Re-check the moved part (and anything previously flagged) for overlaps/out-of-sheet.</summary>
    private void RefreshInvalid()
    {
      var group = this.CurrentGroup();
      if (group == null)
      {
        return;
      }

      var placements = group.Representative.PartPlacements;
      var toCheck = new HashSet<IPartPlacement>(this.invalid);
      if (this.selectedPp != null)
      {
        toCheck.Add(this.selectedPp);
      }

      foreach (var c in toCheck.ToList())
      {
        if (!placements.Contains(c))
        {
          this.invalid.Remove(c);
          continue;
        }

        bool bad = this.OutOfSheet(c);
        foreach (var other in placements)
        {
          if (other == c)
          {
            continue;
          }

          if (this.TooClose(c, other))
          {
            bad = true;
            this.invalid.Add(other); // both sides of an overlap show red
          }
        }

        if (bad)
        {
          this.invalid.Add(c);
        }
        else
        {
          this.invalid.Remove(c);
        }
      }

      foreach (var (path, p) in this.partPaths)
      {
        path.Fill = this.FillFor(p);
      }
    }

    private bool OutOfSheet(IPartPlacement pp)
    {
      const double Tol = 0.002;
      var placed = pp.PlacedPart;
      return placed.MinX < -Tol || placed.MinY < -Tol
        || placed.MaxX > this.currentSheetW + Tol || placed.MaxY > this.currentSheetH + Tol;
    }

    private const double ClipScale = 1e6;

    private static List<List<ClipperLib.IntPoint>> ToClipperPaths(INfp nfp)
    {
      var paths = new List<List<ClipperLib.IntPoint>>();
      void Add(INfp contour)
      {
        if (contour?.Points == null || contour.Points.Length < 3)
        {
          return;
        }

        var path = new List<ClipperLib.IntPoint>(contour.Points.Length);
        foreach (var p in contour.Points)
        {
          path.Add(new ClipperLib.IntPoint((long)Math.Round(p.X * ClipScale), (long)Math.Round(p.Y * ClipScale)));
        }

        paths.Add(path);
      }

      Add(nfp);
      if (nfp.Children != null)
      {
        foreach (var child in nfp.Children)
        {
          Add(child);
        }
      }

      return paths;
    }

    /// <summary>Grow the outer contour by <paramref name="inches"/> (round join = uniform Euclidean
    /// clearance, matching the nesting engine's halo). Holes kept as-is.</summary>
    private static List<List<ClipperLib.IntPoint>> InflateOuter(List<List<ClipperLib.IntPoint>> paths, double inches)
    {
      if (paths.Count == 0)
      {
        return paths;
      }

      var outer = new List<ClipperLib.IntPoint>(paths[0]);
      if (!ClipperLib.Clipper.Orientation(outer))
      {
        outer.Reverse();
      }

      var offset = new ClipperLib.ClipperOffset();
      offset.AddPath(outer, ClipperLib.JoinType.jtRound, ClipperLib.EndType.etClosedPolygon);
      var grown = new List<List<ClipperLib.IntPoint>>();
      offset.Execute(ref grown, inches * ClipScale);

      var result = new List<List<ClipperLib.IntPoint>>(grown);
      for (int i = 1; i < paths.Count; i++)
      {
        result.Add(paths[i]);
      }

      return result.Count > 0 ? result : paths;
    }

    private static bool PathsOverlap(List<List<ClipperLib.IntPoint>> a, List<List<ClipperLib.IntPoint>> b)
    {
      const double EpsArea = 1e-6 * ClipScale * ClipScale;
      var clipper = new ClipperLib.Clipper();
      clipper.AddPaths(a, ClipperLib.PolyType.ptSubject, true);
      clipper.AddPaths(b, ClipperLib.PolyType.ptClip, true);
      var solution = new List<List<ClipperLib.IntPoint>>();
      clipper.Execute(ClipperLib.ClipType.ctIntersection, solution, ClipperLib.PolyFillType.pftEvenOdd, ClipperLib.PolyFillType.pftEvenOdd);
      double area = 0;
      foreach (var path in solution)
      {
        area += Math.Abs(ClipperLib.Clipper.Area(path));
      }

      return area > EpsArea;
    }
  }
}
