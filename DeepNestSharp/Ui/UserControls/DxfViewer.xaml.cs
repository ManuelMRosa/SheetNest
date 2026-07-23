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
    private const double KerfBandPx = 7.0; // cut-band width in device pixels; fixed on screen so it reads at any zoom

    // Measure snap: how close (device px) the cursor must be to a vertex to snap onto it.
    private const double SnapScreenPx = 12.0;

    private static readonly Brush SheetFill = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Brush SheetStroke = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush PartStroke = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
    private static readonly Brush PartFill = new SolidColorBrush(Color.FromArgb(0xC0, 0xB4, 0xB8, 0xBC)); // aluminum gray
    private static readonly Brush HoleFill = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Brush SelectedFill = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x80)); // classic navy selection
    private static readonly Brush InvalidFill = new SolidColorBrush(Color.FromArgb(0x77, 0xD3, 0x2F, 0x2F));
    private static readonly Brush OffcutStroke = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)); // reusable-offcut outline + label
    private static readonly Brush LeadStroke = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)); // lead-in/out cut path
    private static readonly Brush KerfBand = new SolidColorBrush(Color.FromArgb(0xE0, 0xC6, 0x28, 0x28)); // cut band (contour + leads), bold so it reads at any zoom

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

    // Measure tool: first clicked point (canvas/inch coords), whether the segment is fixed, and the
    // overlay shapes (children of the canvas so they zoom/pan with the geometry).
    private Point? measureA;
    private bool measureDone;
    private System.Windows.Shapes.Line measureLine;
    private System.Windows.Shapes.Ellipse measureDotA;
    private System.Windows.Shapes.Ellipse measureDotB;
    private System.Windows.Shapes.Ellipse measureSnapMarker; // hollow ring shown over the snapped vertex
    private Point measureSnapAt;

    // Vertices / edge midpoints / hole centres (canvas coords) the measure tool snaps to, and the
    // edge segments for "nearest point on an edge" projection — all rebuilt each Render.
    private readonly List<Point> snapPoints = new List<Point>();
    private readonly List<(Point A, Point B)> snapSegments = new List<(Point, Point)>();

    // Undo/redo history of manual edits (cleared when a new result arrives).
    private readonly Stack<EditRecord> undoStack = new Stack<EditRecord>();
    private readonly Stack<EditRecord> redoStack = new Stack<EditRecord>();

    /// <summary>Effective per-part spacing (inches) keyed by the part's source DXF path — set by the
    /// window right before a nest result is shown, so manual edits enforce the same clearances the
    /// nester used: two parts must stay (spacingA + spacingB)/2 apart; common-line parts (0) may touch.</summary>
    public IDictionary<string, double> PartSpacings { get; set; }

    /// <summary>Fallback spacing for parts not found in <see cref="PartSpacings"/>.</summary>
    public double DefaultPartSpacing { get; set; }

    /// <summary>Drawing units are millimeters (true) vs inches (false); labels the measure readout.</summary>
    public bool UnitsMm { get; set; }

    /// <summary>Outline the clean rectangular offcut(s) on the last layout (set by the window when
    /// the nest ran with "Prefer rectangular offcut"; null = off); computed live from the placements
    /// each render, with the same rules the DXF export uses for the separation cut lines.</summary>
    internal RasterNest.OffcutOptions OffcutOptions { get; set; }

    /// <summary>Lead-in/out paths per part (keyed by <c>Part.Name</c>, i.e. its file path), in the part's
    /// own local frame — set by the window for jobs imported from a SheetCam .nest; null = nothing to draw.</summary>
    internal System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyList<DeepNestLib.SvgPoint>>> LeadPaths { get; set; }

    /// <summary>Cut width (kerf) per part in drawing units (keyed by <c>Part.Name</c>) — set for jobs from a
    /// SheetCam .nest so the cut is drawn as a band of its real width; absent/0 = draw the outline hairline.</summary>
    internal System.Collections.Generic.IReadOnlyDictionary<string, double> KerfByPart { get; set; }

    /// <summary>Re-render on demand — used by the Offcut dialog so overlay changes show without
    /// re-nesting (setting <see cref="OffcutOptions"/> alone does not redraw).</summary>
    internal void RefreshRender() => this.Render();

    /// <summary>The one sheet the offcut belongs to: the genuine remainder the engine shaped, which
    /// is the LAST physical sheet — but only when the current view shows it faithfully (it is a
    /// group's own representative). In the condensed production-plan view the remainder is a
    /// synthetic representative, and a replicated pattern has no remainder at all; in both cases this
    /// returns null and the offcut is suppressed rather than drawn on geometry the engine never
    /// shaped.</summary>
    internal ISheetPlacement OffcutSheet
    {
      get
      {
        var used = this.Result?.UsedSheets;
        if (used == null || used.Count == 0)
        {
          return null;
        }

        var last = used[used.Count - 1];
        return this.groups.Any(g => ReferenceEquals(g.Representative, last)) ? last : null;
      }
    }

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
      public bool IsMirrored;
      public int Source;
      public int Id;

      public static PlacementSnapshot Of(IPartPlacement pp) => new PlacementSnapshot
      {
        Part = pp.Part,
        X = pp.X,
        Y = pp.Y,
        Rotation = pp.Rotation,
        IsMirrored = pp.IsMirrored,
        Source = pp.Source,
        Id = pp.Id,
      };

      public PartPlacement ToPlacement() => new PartPlacement(this.Part)
      {
        X = this.X,
        Y = this.Y,
        Rotation = this.Rotation,
        IsMirrored = this.IsMirrored,
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
        OffcutStroke.Freeze();
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
      this.ClearMeasure(); // canvas was cleared; drop dangling measure-shape references

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
      // packages do it (one repeated layout shown with its repeat count in parentheses
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

      this.DrawOffcutOverlay(sheetPlacement, idx, w, h, stroke);
      this.DrawKerfOverlay(sheetPlacement, h, stroke);
      this.DrawLeadsOverlay(sheetPlacement, h, stroke);
      this.BuildSnapPoints();
    }

    /// <summary>
    /// For parts that came from a SheetCam .nest, highlights the cut — the outline and every hole edge —
    /// as a bold band. The band is a FIXED width on screen (not the real kerf, which is sub-pixel on a
    /// full-sheet view and so invisible); the point is to see where the cut and its lead-ins run. Nothing
    /// for plain DXF parts, so their thin outline is unchanged.
    /// </summary>
    private void DrawKerfOverlay(ISheetPlacement sheetPlacement, double h, double stroke)
    {
      if (this.KerfByPart == null || this.KerfByPart.Count == 0)
      {
        return;
      }

      // stroke is StrokeScreenPx (1) device pixel in drawing units; scale it up to the band width. Constant
      // on screen at any zoom.
      double band = stroke * KerfBandPx;

      foreach (var pp in sheetPlacement.PartPlacements)
      {
        // kerf just flags "this part came in toolpathed"; the band width is the fixed on-screen one.
        if (pp.Part?.Name == null || !this.KerfByPart.TryGetValue(pp.Part.Name, out double kerf) || kerf <= 0)
        {
          continue;
        }

        var geometry = BuildPlacedGeometry(pp, h);
        if (geometry == null)
        {
          continue;
        }

        this.canvas.Children.Add(new System.Windows.Shapes.Path
        {
          Data = geometry,
          Fill = null,
          Stroke = KerfBand,
          StrokeThickness = band,
          StrokeLineJoin = PenLineJoin.Round,
        });
      }
    }

    /// <summary>
    /// The lead-in/out paths of parts that came from a SheetCam .nest, drawn where they will actually be
    /// cut. They reach outside the part outline, and the nester reserves that room — showing them is what
    /// explains the gaps it left, and makes a lead running into a neighbour visible here instead of after
    /// the job is back in SheetCam.
    /// </summary>
    private void DrawLeadsOverlay(ISheetPlacement sheetPlacement, double h, double stroke)
    {
      if (this.LeadPaths == null || this.LeadPaths.Count == 0)
      {
        return;
      }

      foreach (var pp in sheetPlacement.PartPlacements)
      {
        if (pp.Part?.Name == null || !this.LeadPaths.TryGetValue(pp.Part.Name, out var paths))
        {
          continue;
        }

        // Draw the lead as the same bold cut band as the outline when this part came in toolpathed;
        // otherwise a thin line.
        double kerf = 0;
        this.KerfByPart?.TryGetValue(pp.Part.Name, out kerf);
        bool asBand = kerf > 0;
        var leadBrush = asBand ? KerfBand : LeadStroke;
        double leadThickness = asBand ? stroke * KerfBandPx : stroke * 1.5;

        double radians = pp.Rotation * Math.PI / 180d;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        foreach (var path in paths)
        {
          if (path == null || path.Count < 2)
          {
            continue;
          }

          var figure = new PathFigure { IsClosed = false, IsFilled = false };
          for (int i = 0; i < path.Count; i++)
          {
            // Same transform the placement applies: mirror, rotate about the part origin, then translate.
            // The canvas is Y-down, so the sheet's Y is flipped last.
            double lx = pp.IsMirrored ? -path[i].X : path[i].X;
            double ly = path[i].Y;
            double x = pp.X + ((lx * cos) - (ly * sin));
            double y = pp.Y + ((lx * sin) + (ly * cos));
            var point = new Point(x, h - y);

            if (i == 0)
            {
              figure.StartPoint = point;
            }
            else
            {
              figure.Segments.Add(new LineSegment(point, true));
            }
          }

          var geometry = new PathGeometry();
          geometry.Figures.Add(figure);
          this.canvas.Children.Add(new System.Windows.Shapes.Path
          {
            Data = geometry,
            Stroke = leadBrush,
            StrokeThickness = leadThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
          });
        }
      }
    }

    /// <summary>
    /// Dashed outline + size label over the clean rectangular offcut(s) on the genuine remainder
    /// sheet (see <see cref="OffcutSheet"/>), when the nest ran with "Prefer rectangular offcut".
    /// Computed live from the placements with the same cut positions the DXF export writes, so manual
    /// edits move the outline — or hide it once a part invades the strip — and what the user sees is
    /// exactly what gets cut.
    /// </summary>
    private void DrawOffcutOverlay(ISheetPlacement sheetPlacement, int idx, double w, double h, double stroke)
    {
      if (this.OffcutOptions == null || !ReferenceEquals(sheetPlacement, this.OffcutSheet) || sheetPlacement.PartPlacements.Count == 0)
      {
        return;
      }

      var (cutX, cutY) = RasterNest.OffcutGeometry.CutPositions(sheetPlacement, this.OffcutOptions);

      // Draw the SAME remnant rectangles the export cuts (single source: OffcutGeometry.RemnantRects).
      // Sheet coords are Y-up; the canvas flips Y, so a rect at sheet [r.Y..r.Y+r.H] lands at canvas
      // top [h-(r.Y+r.H)]. Empty list = no offcut worth advertising.
      foreach (var r in RasterNest.OffcutGeometry.RemnantRects(cutX, cutY, w, h))
      {
        this.DrawOffcutRect(new Rect(r.X, h - (r.Y + r.H), r.W, r.H), w, h, stroke);
      }
    }

    /// <summary>One dashed offcut rectangle (canvas coords) with its centered size label.</summary>
    private void DrawOffcutRect(Rect rect, double w, double h, double stroke)
    {
      if (rect.Width <= 0 || rect.Height <= 0)
      {
        return;
      }

      this.canvas.Children.Add(new System.Windows.Shapes.Path
      {
        Data = new RectangleGeometry(rect),
        Stroke = OffcutStroke,
        StrokeThickness = stroke,
        StrokeDashArray = new DoubleCollection { 4, 3 },
        IsHitTestVisible = false,
      });

      string unit = this.UnitsMm ? "mm" : "in";
      var label = new TextBlock
      {
        Text = $"{rect.Width:0.##} × {rect.Height:0.##} {unit}",
        Foreground = OffcutStroke,
        FontFamily = new FontFamily("Tahoma"),
        FontSize = Math.Max(0.001, Math.Min(w, h) * 0.045),
        IsHitTestVisible = false,
      };

      // Center the label in the strip; a strip too narrow for the text gets it turned 90°.
      label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
      if (label.DesiredSize.Width > rect.Width && rect.Height > rect.Width)
      {
        label.LayoutTransform = new RotateTransform(90);
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
      }

      Canvas.SetLeft(label, rect.X + ((rect.Width - label.DesiredSize.Width) / 2));
      Canvas.SetTop(label, rect.Y + ((rect.Height - label.DesiredSize.Height) / 2));
      this.canvas.Children.Add(label);
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
      this.UpdateMeasureScale();
      e.Handled = true;
    }

    private bool EditMode => this.editToggle.IsChecked == true;

    private void OnEditModeChanged(object sender, RoutedEventArgs e)
    {
      if (this.EditMode && this.measureToggle != null && this.measureToggle.IsChecked == true)
      {
        this.measureToggle.IsChecked = false; // mutually exclusive with Measure
      }

      this.SelectPart(null);
      if (this.hintText != null)
      {
        this.hintText.Text = this.EditMode
          ? "click = select part  ·  drag = move  ·  buttons = rotate  ·  overlaps show red"
          : "scroll = zoom  ·  wheel-drag = pan  ·  right-click = fit";
      }
    }

    private bool MeasureMode => this.measureToggle != null && this.measureToggle.IsChecked == true;

    private void OnMeasureModeChanged(object sender, RoutedEventArgs e)
    {
      if (this.MeasureMode && this.editToggle != null && this.editToggle.IsChecked == true)
      {
        this.editToggle.IsChecked = false; // mutually exclusive with Edit
      }

      this.ClearMeasure();
      if (this.MeasureMode)
      {
        this.EnsureMeasureShapes(); // so the snap ring can show while aiming the first click
      }

      if (this.hintText != null)
      {
        this.hintText.Text = this.MeasureMode
          ? "measure: click two points (snaps to corners)"
          : "scroll = zoom  ·  wheel-drag = pan  ·  right-click = fit";
      }
    }

    private void ClearMeasure()
    {
      this.measureA = null;
      this.measureDone = false;
      if (this.measureLine != null) { this.canvas.Children.Remove(this.measureLine); this.measureLine = null; }
      if (this.measureDotA != null) { this.canvas.Children.Remove(this.measureDotA); this.measureDotA = null; }
      if (this.measureDotB != null) { this.canvas.Children.Remove(this.measureDotB); this.measureDotB = null; }
      if (this.measureSnapMarker != null) { this.canvas.Children.Remove(this.measureSnapMarker); this.measureSnapMarker = null; }
    }

    private void EnsureMeasureShapes()
    {
      if (this.measureLine != null)
      {
        return;
      }

      var brush = new SolidColorBrush(Color.FromRgb(0xC0, 0x00, 0x00)); // measure red
      brush.Freeze();
      this.measureLine = new System.Windows.Shapes.Line { Stroke = brush, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
      this.measureDotA = new System.Windows.Shapes.Ellipse { Fill = brush, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
      this.measureDotB = new System.Windows.Shapes.Ellipse { Fill = brush, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
      this.measureSnapMarker = new System.Windows.Shapes.Ellipse { Stroke = brush, Fill = null, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
      this.canvas.Children.Add(this.measureLine);
      this.canvas.Children.Add(this.measureDotA);
      this.canvas.Children.Add(this.measureDotB);
      this.canvas.Children.Add(this.measureSnapMarker);
    }

    /// <summary>Keeps the measure overlay a constant on-screen size (counter-scaling the zoom) and
    /// positions the dots + snap ring at their points.</summary>
    private void UpdateMeasureScale()
    {
      if (this.measureLine == null)
      {
        return;
      }

      double f = 1.0 / Math.Max(0.0001, this.scale.ScaleX);
      this.measureLine.StrokeThickness = 1.5 * f;
      double r = 3.0 * f;

      if (this.measureDotA != null && this.measureA != null)
      {
        this.measureDotA.Width = this.measureDotA.Height = r * 2;
        System.Windows.Controls.Canvas.SetLeft(this.measureDotA, this.measureA.Value.X - r);
        System.Windows.Controls.Canvas.SetTop(this.measureDotA, this.measureA.Value.Y - r);
      }

      if (this.measureDotB != null)
      {
        this.measureDotB.Width = this.measureDotB.Height = r * 2;
        System.Windows.Controls.Canvas.SetLeft(this.measureDotB, this.measureLine.X2 - r);
        System.Windows.Controls.Canvas.SetTop(this.measureDotB, this.measureLine.Y2 - r);
      }

      if (this.measureSnapMarker != null)
      {
        double sr = 5.0 * f;
        this.measureSnapMarker.StrokeThickness = 1.5 * f;
        this.measureSnapMarker.Width = this.measureSnapMarker.Height = sr * 2;
        System.Windows.Controls.Canvas.SetLeft(this.measureSnapMarker, this.measureSnapAt.X - sr);
        System.Windows.Controls.Canvas.SetTop(this.measureSnapMarker, this.measureSnapAt.Y - sr);
      }
    }

    private void BuildSnapPoints()
    {
      this.snapPoints.Clear();
      this.snapSegments.Clear();
      double h = this.currentSheetH;

      // Sheet corners (canvas coords: Y flipped).
      this.snapPoints.Add(new Point(0, h));
      this.snapPoints.Add(new Point(this.currentSheetW, h));
      this.snapPoints.Add(new Point(0, 0));
      this.snapPoints.Add(new Point(this.currentSheetW, 0));

      foreach (var (_, pp) in this.partPaths)
      {
        var src = pp?.PlacedPart;
        if (src == null)
        {
          continue;
        }

        this.AddContourSnap(src, h);
        if (src.Children != null)
        {
          foreach (var child in src.Children)
          {
            this.AddContourSnap(child, h);
            this.AddHoleCentre(child, h); // circle/hole centre = its bbox centre
          }
        }
      }
    }

    /// <summary>Adds each vertex + each edge midpoint to snapPoints, and each edge to snapSegments,
    /// for one contour (in canvas coords). Wraps the last edge to close the loop and skips the
    /// zero-length edge some contours carry from a duplicated closing vertex.</summary>
    private void AddContourSnap(INfp contour, double h)
    {
      if (contour == null)
      {
        return;
      }

      int n = contour.Length;
      for (int i = 0; i < n; i++)
      {
        var a = new Point(contour[i].X, h - contour[i].Y); // absolute sheet coords -> canvas (flip Y)
        this.snapPoints.Add(a);

        var next = contour[(i + 1) % n];
        var b = new Point(next.X, h - next.Y);
        double ex = b.X - a.X;
        double ey = b.Y - a.Y;
        if ((ex * ex) + (ey * ey) > 1e-9)
        {
          this.snapPoints.Add(new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2)); // edge midpoint
          this.snapSegments.Add((a, b));
        }
      }
    }

    private void AddHoleCentre(INfp hole, double h)
    {
      if (hole == null || hole.Length < 3)
      {
        return;
      }

      double cx = (hole.MinX + hole.MaxX) / 2;
      double cy = (hole.MinY + hole.MaxY) / 2;
      this.snapPoints.Add(new Point(cx, h - cy)); // exact centre for a circular hole; bbox centre otherwise
    }

    /// <summary>Snaps <paramref name="cursor"/> (canvas coords) to the nearest vertex within the screen
    /// threshold; returns the cursor unchanged (snapped=false) when none is close enough.</summary>
    private Point SnapToNearest(Point cursor, out bool snapped)
    {
      snapped = false;
      Point result = cursor;
      double thresh = SnapScreenPx / Math.Max(0.0001, this.scale.ScaleX);
      double bestD2 = thresh * thresh;

      // Tier 1 — discrete points (vertices, edge midpoints, hole centres, sheet corners): these win.
      foreach (var sp in this.snapPoints)
      {
        double dx = sp.X - cursor.X;
        double dy = sp.Y - cursor.Y;
        double d2 = (dx * dx) + (dy * dy);
        if (d2 <= bestD2)
        {
          bestD2 = d2;
          result = sp;
          snapped = true;
        }
      }

      if (snapped)
      {
        return result;
      }

      // Tier 2 — nearest point on an edge (perpendicular projection): fallback when no vertex is close.
      foreach (var (a, b) in this.snapSegments)
      {
        double abx = b.X - a.X;
        double aby = b.Y - a.Y;
        double len2 = (abx * abx) + (aby * aby);
        if (len2 < 1e-9)
        {
          continue;
        }

        double t = (((cursor.X - a.X) * abx) + ((cursor.Y - a.Y) * aby)) / len2;
        t = Math.Max(0, Math.Min(1, t));
        var proj = new Point(a.X + (t * abx), a.Y + (t * aby));
        double dx = proj.X - cursor.X;
        double dy = proj.Y - cursor.Y;
        double d2 = (dx * dx) + (dy * dy);
        if (d2 <= bestD2)
        {
          bestD2 = d2;
          result = proj;
          snapped = true;
        }
      }

      return result;
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

      if (this.MeasureMode)
      {
        this.EnsureMeasureShapes();
        Point p = this.SnapToNearest(e.GetPosition(this.canvas), out _); // canvas units == drawing inches
        if (this.measureA == null || this.measureDone)
        {
          this.measureA = p;
          this.measureDone = false;
          this.measureLine.X1 = this.measureLine.X2 = p.X;
          this.measureLine.Y1 = this.measureLine.Y2 = p.Y;
          this.measureLine.Visibility = Visibility.Visible;
          this.measureDotA.Visibility = Visibility.Visible;
          this.measureDotB.Visibility = Visibility.Collapsed;
          this.UpdateMeasureScale();
          if (this.hintText != null)
          {
            this.hintText.Text = "measure: click the second point";
          }
        }
        else
        {
          this.measureLine.X2 = p.X;
          this.measureLine.Y2 = p.Y;
          this.measureDotB.Visibility = Visibility.Visible;
          this.measureDone = true;
          this.UpdateMeasureScale();
          double d = (p - this.measureA.Value).Length; // Y-flip invariant → drawing units
          if (this.hintText != null)
          {
            this.hintText.Text = $"distance: {d:0.000} {(this.UnitsMm ? "mm" : "in")}  ·  click to measure again";
          }
        }

        e.Handled = true;
        return;
      }

      if (this.EditMode)
      {
        // Topmost part under the cursor gets selected and dragged; empty space just deselects.
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
    }

    private void OnPanMove(object sender, MouseEventArgs e)
    {
      // An active pan (middle-button, in any mode) wins over measure/edit, so wheel-pan works in
      // every mode.
      if (this.isPanning)
      {
        Point panPos = e.GetPosition(this.host);
        this.translate.X = this.panTranslateX + (panPos.X - this.panStart.X);
        this.translate.Y = this.panTranslateY + (panPos.Y - this.panStart.Y);
        return;
      }

      if (this.MeasureMode)
      {
        this.EnsureMeasureShapes();
        Point mp = this.SnapToNearest(e.GetPosition(this.canvas), out bool snapped);
        this.measureSnapAt = mp;
        this.measureSnapMarker.Visibility = snapped ? Visibility.Visible : Visibility.Collapsed;

        if (this.measureA != null && !this.measureDone)
        {
          this.measureLine.X2 = mp.X;
          this.measureLine.Y2 = mp.Y;
          double d = (mp - this.measureA.Value).Length;
          if (this.hintText != null)
          {
            this.hintText.Text = $"distance: {d:0.000} {(this.UnitsMm ? "mm" : "in")}";
          }
        }

        this.UpdateMeasureScale();
        return;
      }

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

    private void OnViewerMouseDown(object sender, MouseButtonEventArgs e)
    {
      // Middle button (mouse-wheel press) = pan, in ANY mode (view / edit / measure), CAD-style —
      // leaving the left button free for selecting/dragging parts and measuring.
      if (e.ChangedButton != System.Windows.Input.MouseButton.Middle)
      {
        return;
      }

      this.host.Focus();
      this.isPanning = true;
      this.panStart = e.GetPosition(this.host);
      this.panTranslateX = this.translate.X;
      this.panTranslateY = this.translate.Y;
      this.host.CaptureMouse();
      this.host.Cursor = Cursors.SizeAll;
      e.Handled = true;
    }

    private void OnViewerMouseUp(object sender, MouseButtonEventArgs e)
    {
      if (e.ChangedButton == System.Windows.Input.MouseButton.Middle && this.isPanning)
      {
        this.isPanning = false;
        this.host.ReleaseMouseCapture();
        this.host.Cursor = Cursors.Arrow;
        e.Handled = true;
      }
    }

    private void OnRotateCw90(object sender, RoutedEventArgs e) => this.RotateSelected(90);

    private void OnRotateCcw90(object sender, RoutedEventArgs e) => this.RotateSelected(-90);

    private void OnRotateCw5(object sender, RoutedEventArgs e) => this.RotateSelected(5);

    private void OnRotateCcw5(object sender, RoutedEventArgs e) => this.RotateSelected(-5);

    private void OnMirror(object sender, RoutedEventArgs e) => this.MirrorSelected();

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
        IsMirrored = pp.IsMirrored, // rotating a mirrored part keeps it mirrored
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

    /// <summary>
    /// Mirrors the selected part in place across the Y axis (left ↔ right hand). Like <see cref="RotateSelected"/>
    /// the placement is REPLACED with one whose Part carries the reflected geometry, plus an <c>IsMirrored</c>
    /// flag the DXF export also honours — so the CUT part is really mirrored, not just on screen.
    /// </summary>
    private void MirrorSelected()
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

      // Canonical transform = mirror(across Y, about origin) THEN rotate(Rotation): un-rotate to the source
      // frame, mirror there, re-rotate. This matches the exporter (reload original -> mirror -> rotate) and
      // stays consistent under further rotations (they just add to the outer rotation).
      var newPart = pp.Part.Rotate(-pp.Rotation).MirrorX().Rotate(pp.Rotation);
      var replacement = new PartPlacement(newPart)
      {
        X = cx - ((newPart.MinX + newPart.MaxX) / 2.0),
        Y = cy - ((newPart.MinY + newPart.MaxY) / 2.0),
        Rotation = pp.Rotation,
        IsMirrored = !pp.IsMirrored,
        Source = pp.Source,
        Id = pp.Id,
      };

      // Overlaps aren't allowed: refuse the mirror (brief red flash) if it would collide or leave the sheet.
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
