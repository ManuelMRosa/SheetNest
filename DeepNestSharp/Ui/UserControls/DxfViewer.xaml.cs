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
  using DeepNestLib.IO;
  using DeepNestLib.Placement;

  /// <summary>
  /// Faithful nest viewer. Instead of rendering the coarse/inflated polygons the nesting math uses,
  /// it reloads each part's ORIGINAL DXF geometry (finely tessellated by DxfParser), transforms it to
  /// the part's placement exactly like the verified DXF export (rotate then offset), and draws it on a
  /// Y-flipped, auto-fit canvas. Result: the preview matches what AutoCAD shows.
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

    // Original (un-rotated) geometry per source file, so a result with many copies reads each once.
    private static readonly Dictionary<string, INfp> GeometryCache = new Dictionary<string, INfp>();

    // Distinct sheet layouts and how many physical sheets use each (the production plan).
    private readonly List<SheetGroup> groups = new List<SheetGroup>();

    private double fittedW = -1;
    private double fittedH = -1;
    private bool isPanning;
    private Point panStart;
    private double panTranslateX;
    private double panTranslateY;

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

      // Industrial plan: replicate the densest sheet pattern across the whole demand, then mop up the
      // leftovers as a few remainder sheets — so one or two part types still read as "N× pattern + remainder".
      if (result.UsedSheets.Count > 1 && this.TryBuildProductionPlan(result))
      {
        return;
      }

      // Fallback: group physical sheets by their exact layout.
      var bySignature = new Dictionary<string, SheetGroup>();
      foreach (var sp in result.UsedSheets)
      {
        string sig = Signature(sp);
        if (!bySignature.TryGetValue(sig, out var g))
        {
          g = new SheetGroup { Representative = sp, Count = 0, Name = $"Layout {this.groups.Count + 1}" };
          bySignature[sig] = g;
          this.groups.Add(g);
        }

        g.Count++;
      }
    }

    /// <summary>The distinct sheet layouts for the current result — one per production-plan group
    /// ("cut N × this layout"). Used by Export so identical sheets export ONCE (with the plan telling
    /// how many to cut) instead of one DXF per physical sheet.</summary>
    public IReadOnlyList<ISheetPlacement> GetDistinctLayoutSheets()
    {
      return this.groups.Select(g => g.Representative).Where(s => s != null).ToList();
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
      return string.Join("|", items);
    }

    private string BuildSummary()
    {
      int totalSheets = this.groups.Sum(g => g.Count);
      int placed = this.Result?.TotalPlacedCount ?? 0;
      var plan = this.groups.Select(g => $"{g.Count} × {g.Name.ToLowerInvariant()} ({g.Representative.PartPlacements.Count} parts each)");
      return "CUT:   " + string.Join("    +    ", plan) + $"\n{placed} parts total  ·  {totalSheets} sheets";
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
    }

    private void Render()
    {
      this.canvas.Children.Clear();

      var result = this.Result;
      if (result == null || this.groups.Count == 0)
      {
        this.emptyHint.Visibility = Visibility.Visible;
        this.summaryBox.Visibility = Visibility.Collapsed;
        this.sheetNav.Visibility = Visibility.Collapsed;
        return;
      }

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

      // Show the production plan, and navigate by distinct LAYOUT (not by physical sheet).
      this.summaryText.Text = this.BuildSummary();
      this.summaryBox.Visibility = (result.UsedSheets?.Count ?? 0) > 1 ? Visibility.Visible : Visibility.Collapsed;
      this.sheetLabel.Text = $"{group.Name}  ·  {sheetPlacement.PartPlacements.Count} parts  ·  cut ×{group.Count}";
      this.sheetNav.Visibility = this.groups.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

      double w = sheet.WidthCalculated;
      double h = sheet.HeightCalculated;
      this.canvas.Width = w;
      this.canvas.Height = h;

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

          this.canvas.Children.Add(new System.Windows.Shapes.Path
          {
            Data = geometry,
            Fill = PartFill,
            Stroke = PartStroke,
            StrokeThickness = stroke,
            StrokeLineJoin = PenLineJoin.Round,
          });
        }
        catch
        {
          // Skip a part that can't be rendered rather than blanking the whole view.
        }
      }
    }

    /// <summary>
    /// Builds the WPF geometry for one placed part in sheet coordinates (Y flipped to screen space),
    /// using the original DXF outline + holes when available, else the part's own points.
    /// </summary>
    private static Geometry BuildPlacedGeometry(IPartPlacement pp, double sheetHeight)
    {
      INfp original = TryGetOriginal(pp.Part?.Name, pp.Source);

      double rot;
      double dx;
      double dy;
      INfp source;

      if (original != null && pp.PlacedPart != null)
      {
        // Render the true (un-inflated) outline CENTERED on the actual placed (spacing-inflated) part.
        // This keeps the spacing margin even on every side and identical for every part, regardless of
        // any coordinate-frame difference between the reloaded original and the nested geometry — which
        // was making some parts touch and others gap.
        rot = pp.Rotation * Math.PI / 180.0;
        double rcos = Math.Cos(rot);
        double rsin = Math.Sin(rot);
        var (icx, icy) = CenterOf(pp.PlacedPart);          // inflated, absolute sheet coords
        var (ocx, ocy) = CenterOf(original);               // original, un-rotated local coords
        dx = icx - ((ocx * rcos) - (ocy * rsin));
        dy = icy - ((ocx * rsin) + (ocy * rcos));
        source = original;
      }
      else
      {
        // Fallback: PlacedPart is already rotated + in absolute sheet coords.
        rot = 0;
        dx = 0;
        dy = 0;
        source = pp.PlacedPart;
      }

      double cos = Math.Cos(rot);
      double sin = Math.Sin(rot);

      Point Tx(double x, double y)
      {
        double ax = (x * cos) - (y * sin) + dx;
        double ay = (x * sin) + (y * cos) + dy;
        return new Point(ax, sheetHeight - ay); // flip Y: DXF up -> screen down
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

    private static (double X, double Y) CenterOf(INfp nfp)
    {
      double minX = double.MaxValue;
      double minY = double.MaxValue;
      double maxX = double.MinValue;
      double maxY = double.MinValue;
      for (int i = 0; i < nfp.Length; i++)
      {
        var p = nfp[i];
        if (p.X < minX)
        {
          minX = p.X;
        }

        if (p.X > maxX)
        {
          maxX = p.X;
        }

        if (p.Y < minY)
        {
          minY = p.Y;
        }

        if (p.Y > maxY)
        {
          maxY = p.Y;
        }
      }

      return ((minX + maxX) / 2.0, (minY + maxY) / 2.0);
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

    private static INfp TryGetOriginal(string name, int source)
    {
      if (string.IsNullOrEmpty(name) || !name.ToLowerInvariant().Contains(".dxf"))
      {
        return null;
      }

      if (GeometryCache.TryGetValue(name, out INfp cached))
      {
        return cached;
      }

      try
      {
        var raw = DxfParser.LoadDxfFile(name).GetAwaiter().GetResult();
        if (raw != null && raw.TryConvertToNfp(source, out INfp nfp))
        {
          GeometryCache[name] = nfp;
          return nfp;
        }
      }
      catch
      {
        // fall through to null -> caller uses the in-memory part instead
      }

      GeometryCache[name] = null;
      return null;
    }

    private void OnPanStart(object sender, MouseButtonEventArgs e)
    {
      this.isPanning = true;
      this.panStart = e.GetPosition(this.host);
      this.panTranslateX = this.translate.X;
      this.panTranslateY = this.translate.Y;
      this.host.CaptureMouse();
      this.host.Cursor = Cursors.SizeAll;
    }

    private void OnPanMove(object sender, MouseEventArgs e)
    {
      if (!this.isPanning)
      {
        return;
      }

      Point p = e.GetPosition(this.host);
      this.translate.X = this.panTranslateX + (p.X - this.panStart.X);
      this.translate.Y = this.panTranslateY + (p.Y - this.panStart.Y);
    }

    private void OnPanEnd(object sender, MouseButtonEventArgs e)
    {
      this.isPanning = false;
      this.host.ReleaseMouseCapture();
      this.host.Cursor = Cursors.Arrow;
    }

    private void OnResetView(object sender, MouseButtonEventArgs e)
    {
      this.FitToView();
      e.Handled = true;
    }

  }
}
