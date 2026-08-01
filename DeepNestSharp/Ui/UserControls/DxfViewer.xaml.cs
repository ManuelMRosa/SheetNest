namespace DeepNestSharp.Ui.UserControls
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
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
    private const double OffcutCutPx = 4.0; // offcut cut line, same idea: it decides where the material is split

    // Measure snap: how close (device px) the cursor must be to a vertex to snap onto it.
    private const double SnapScreenPx = 12.0;

    private static readonly Brush SheetFill = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Brush SheetStroke = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush PartStroke = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
    private static readonly Brush PartFill = new SolidColorBrush(Color.FromArgb(0xC0, 0xB4, 0xB8, 0xBC)); // aluminum gray
    private static readonly Brush HoleFill = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Brush SelectedFill = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x80)); // classic navy selection
    // OPAQUE, like the per-type fills. At 47% alpha this composited to a pale salmon over the near-white
    // sheet, which read as "faded" rather than "wrong" once the parts around it became solid colours -
    // reported as moving a part that was "normal, not red" when the code had it flagged all along.
    private static readonly Brush InvalidFill = new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F));
    // The offcut cut line and its size label: the darkest thing on a near-white sheet, so they are spotted
    // without being looked for. Close to the part outline (#101010) on purpose - the line separates from it
    // by WEIGHT, four solid pixels against one, not by colour.
    private static readonly Brush OffcutStroke = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
    private static readonly Brush LeadStroke = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)); // lead-in/out cut path
    private static readonly Brush KerfBand = new SolidColorBrush(Color.FromArgb(0xE0, 0xC6, 0x28, 0x28)); // cut band (contour + leads), bold so it reads at any zoom

    // The directions the magnets probe. Nests are orthogonal, so a diagonal earns nothing.
    private static readonly (double X, double Y)[] Axes = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    // Distinct sheet layouts and how many physical sheets use each (the production plan).
    private readonly List<SheetGroup> groups = new List<SheetGroup>();

    // Brushes made from PartColours, one per part file, frozen and cached — a sheet can hold hundreds.
    private readonly Dictionary<string, Brush> partBrushes = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);

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

    // The cut band and lead-ins belonging to each placement, kept per part so a part being dragged takes
    // its cut with it. They used to go straight onto the canvas and nowhere else, and manual editing
    // repaints only what is in partPaths, so dragging a toolpathed part left its real cut drawn where the
    // part no longer was.
    private readonly Dictionary<IPartPlacement, List<UIElement>> partOverlays = new Dictionary<IPartPlacement, List<UIElement>>();

    // The offcut cut line and its size label. Same reason: the strip's edge follows the pack, so it has to
    // be redrawn as parts move rather than only when the whole view is rebuilt.
    private readonly List<UIElement> offcutShapes = new List<UIElement>();
    private IPartPlacement selectedPp;
    private bool isDraggingPart;
    private bool dragInvalid;
    private Point dragStartCanvas;
    private double dragStartX;
    private double dragStartY;
    private RasterNest.DragCollisionCache dragCache; // valid only for the drag in progress; null = query the live placements
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
    // edge segments for "nearest point on an edge" projection.
    private readonly List<Point> snapPoints = new List<Point>();
    private readonly List<(Point A, Point B)> snapSegments = new List<(Point, Point)>();

    // Set by every edit, cleared by BuildSnapPoints. Manual editing does NOT re-render - it repaints the one
    // part it moved - so these used to go on describing where the part WAS, and the measure tool kept
    // snapping there. Rebuilding is walking every contour on the sheet, far too much for an auto-repeating
    // arrow key, so it happens on the next snap query instead of on the edit.
    private bool snapPointsStale;

    // Undo/redo history of manual edits (cleared when a new result arrives).
    private readonly Stack<EditRecord> undoStack = new Stack<EditRecord>();
    private readonly Stack<EditRecord> redoStack = new Stack<EditRecord>();

    /// <summary>Effective per-part spacing (inches) keyed by the part's source DXF path — set by the
    /// window right before a nest result is shown, so manual edits enforce the same clearances the
    /// nester used: two parts must stay (spacingA + spacingB)/2 apart; common-line parts (0) may touch.</summary>
    public IDictionary<string, double> PartSpacings { get; set; }

    /// <summary>Fallback spacing for parts not found in <see cref="PartSpacings"/>.</summary>
    public double DefaultPartSpacing { get; set; }

    /// <summary>Common cutting mode per part file (keyed by <c>Part.Name</c>, i.e. its path), set by the
    /// window alongside <see cref="PartSpacings"/>. Absent = None. Without it, hand editing would demand
    /// full clearance between two parts the nester deliberately placed touching, and mark them red.</summary>
    internal IReadOnlyDictionary<string, DeepNestLib.NestProject.CommonCuttingMode> PartCommonCutting { get; set; }

    /// <summary>How far every part must stay from the sheet edge (drawing units) — the same margin the
    /// nester was given, so editing by hand cannot break what the job asked for. Set by the window.</summary>
    public double SheetEdgeMargin { get; set; }

    /// <summary>Colour per part file (see <see cref="PartColors"/>), set by the window from the PROJECT's
    /// part list — not derived from the result here, or a part that was excluded or did not fit would shift
    /// the other colours and the thumbnails in the parts list would stop matching the sheet.</summary>
    public IReadOnlyDictionary<string, (byte R, byte G, byte B)> PartColours { get; set; }

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
      v.undoStack.Clear();
      v.redoStack.Clear();
      v.BuildGroups();
      v.SeedInvalid();
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
      this.partBrushes.Clear();
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

    /// <summary>
    /// Some of a full sheet's parts, to stand as the plan's remainder sheet.
    /// <para>COPIES, never the originals. The two sheets are different layouts that the operator edits one
    /// at a time, and a drag writes X and Y in place: handing over the same objects meant moving a part on
    /// the remainder also moved it on the full sheet, silently, because only the sheet on screen is
    /// repainted. The polygon itself is shared, which is safe - it is never written to.</para>
    /// </summary>
    internal static List<IPartPlacement> SelectPlacements(ISheetPlacement master, Dictionary<int, int> take)
    {
      var need = new Dictionary<int, int>(take);
      var list = new List<IPartPlacement>();
      foreach (var pp in master.PartPlacements)
      {
        if (need.TryGetValue(pp.Source, out int n) && n > 0)
        {
          list.Add(PlacementSnapshot.Of(pp).ToPlacement());
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

      /// <summary>
      /// Whether an edit made here reaches the nest that gets exported and saved.
      /// <para>A condensed production plan proposes an arrangement ("cut 24 of A, 1 of B") that the
      /// engine's own sheets do not match one for one: its remainder is assembled from copies, and its
      /// full sheet stands for N physical sheets while being only one of them. Nothing carries an edit
      /// from there back to <c>Result.UsedSheets</c>, which is what the SheetCam writeback reads and what
      /// the project saves. The edit would sit on screen, go into the per-layout DXF, and be missing from
      /// the cut file and from the reopened project. Better to not offer it than to lose it quietly.</para>
      /// </summary>
      public bool IsEditable => this.Members != null;
    }

    private void Render()
    {
      this.dragCache = null; // placements may have been replaced wholesale — never answer from stale geometry
      this.canvas.Children.Clear();
      this.partPaths.Clear();
      this.partOverlays.Clear();
      this.offcutShapes.Clear();
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
      this.UpdateOverlapCount();
      this.UpdateHint(); // editability is per sheet, so the line has to follow the navigation

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

      // A selection can survive a re-render (undo, a settings change), so its panel has to catch up.
      this.UpdatePartInfo(measureGap: true);
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

      foreach (var pp in sheetPlacement.PartPlacements)
      {
        this.DrawKerfFor(pp, h, stroke);
      }
    }

    /// <summary>One part's cut band, recorded against the part so it can move with it.</summary>
    private void DrawKerfFor(IPartPlacement pp, double h, double stroke)
    {
      // kerf just flags "this part came in toolpathed"; the band width is the fixed on-screen one.
      if (this.KerfByPart == null || pp.Part?.Name == null
        || !this.KerfByPart.TryGetValue(pp.Part.Name, out double kerf) || kerf <= 0)
      {
        return;
      }

      var geometry = BuildPlacedGeometry(pp, h);
      if (geometry == null)
      {
        return;
      }

      // stroke is StrokeScreenPx (1) device pixel in drawing units; scale it up to the band width. The
      // multiplier rides along in Tag so a zoom keeps the band a band (see UpdateStrokeWidths).
      this.AddPartOverlay(pp, new System.Windows.Shapes.Path
      {
        Data = geometry,
        Fill = null,
        Stroke = KerfBand,
        StrokeThickness = stroke * KerfBandPx,
        StrokeLineJoin = PenLineJoin.Round,
        Tag = KerfBandPx,
      });
    }

    /// <summary>
    /// Whether a sheet-coordinate point is on this part, hole or no hole.
    /// <para>Clicking used to ask the drawn geometry, which is EvenOdd so a hole is a gap in it: pointing at
    /// a part right in the middle of a big cutout selected whatever was behind, or nothing. A hole is still
    /// part of the part as far as picking it up goes, so this asks the OUTER contour only. Even-odd ray
    /// crossing, which also handles a concave outline.</para>
    /// </summary>
    internal static bool OutlineContains(IPartPlacement pp, double x, double y)
      => ContourContains(pp?.PlacedPart, x, y);

    /// <summary>Whether the point is on the MATERIAL of this part: inside its outline and not down one of
    /// its holes.</summary>
    internal static bool MaterialContains(IPartPlacement pp, double x, double y)
    {
      var placed = pp?.PlacedPart;
      if (!ContourContains(placed, x, y))
      {
        return false;
      }

      if (placed.Children != null)
      {
        foreach (var hole in placed.Children)
        {
          if (ContourContains(hole, x, y))
          {
            return false;
          }
        }
      }

      return true;
    }

    /// <summary>
    /// The part the operator meant by clicking there, given the placements in draw order (topmost last).
    /// <para>Material first, topmost down, so a part parked in another part's cutout is the one that gets
    /// picked. Only if the click is on no part's material does the outline decide, which is what makes
    /// clicking the middle of a big cutout grab the part the cutout belongs to instead of falling through
    /// to whatever is behind. Asking the outline alone made the enclosing part answer first whenever it
    /// happened to sit later in the list, and the part inside its window could never be picked up again.
    /// </para>
    /// </summary>
    internal static IPartPlacement PickAt(IReadOnlyList<IPartPlacement> placements, double x, double y)
    {
      if (placements == null)
      {
        return null;
      }

      for (int i = placements.Count - 1; i >= 0; i--)
      {
        if (MaterialContains(placements[i], x, y))
        {
          return placements[i];
        }
      }

      for (int i = placements.Count - 1; i >= 0; i--)
      {
        if (OutlineContains(placements[i], x, y))
        {
          return placements[i];
        }
      }

      return null;
    }

    /// <summary>Even-odd ray crossing over one closed contour, which also handles a concave outline.</summary>
    private static bool ContourContains(INfp contour, double x, double y)
    {
      var points = contour?.Points;
      if (points == null || points.Length < 3)
      {
        return false;
      }

      bool inside = false;
      for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
      {
        double xi = points[i].X, yi = points[i].Y;
        double xj = points[j].X, yj = points[j].Y;
        if (((yi > y) != (yj > y)) && (x < (((xj - xi) * (y - yi) / (yj - yi)) + xi)))
        {
          inside = !inside;
        }
      }

      return inside;
    }

    /// <summary>Puts an overlay shape on the canvas and remembers whose it is.</summary>
    private void AddPartOverlay(IPartPlacement pp, UIElement shape)
    {
      this.canvas.Children.Add(shape);
      if (!this.partOverlays.TryGetValue(pp, out var list))
      {
        list = new List<UIElement>();
        this.partOverlays[pp] = list;
      }

      list.Add(shape);
    }

    /// <summary>Takes one part's overlay shapes off the canvas, e.g. before redrawing them where the part
    /// now is, or because the placement itself is being replaced by a rotation.</summary>
    private void DropPartOverlays(IPartPlacement pp)
    {
      if (pp == null || !this.partOverlays.TryGetValue(pp, out var list))
      {
        return;
      }

      foreach (var shape in list)
      {
        this.canvas.Children.Remove(shape);
      }

      this.partOverlays.Remove(pp);
    }

    /// <summary>Redraws one part's cut band and lead-ins where the part is now.</summary>
    private void RefreshPartOverlays(IPartPlacement pp)
    {
      if (pp == null)
      {
        return;
      }

      this.DropPartOverlays(pp);
      double stroke = this.CurrentStroke;
      this.DrawKerfFor(pp, this.currentSheetH, stroke);
      this.DrawLeadsFor(pp, this.currentSheetH, stroke);
    }

    /// <summary>Redraws the offcut cut line, which moves when the pack it measures moves.</summary>
    private void RefreshOffcutOverlay()
    {
      if (this.offcutShapes.Count == 0 && this.OffcutOptions == null)
      {
        return;
      }

      foreach (var shape in this.offcutShapes)
      {
        this.canvas.Children.Remove(shape);
      }

      this.offcutShapes.Clear();

      var group = this.CurrentGroup();
      if (group?.Representative?.Sheet != null)
      {
        this.DrawOffcutOverlay(group.Representative, 0, this.currentSheetW, this.currentSheetH, this.CurrentStroke);
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
        this.DrawLeadsFor(pp, h, stroke);
      }
    }

    /// <summary>One part's leads, recorded against the part so they move with it.</summary>
    private void DrawLeadsFor(IPartPlacement pp, double h, double stroke)
    {
      if (this.LeadPaths == null || pp.Part?.Name == null || !this.LeadPaths.TryGetValue(pp.Part.Name, out var paths))
      {
        return;
      }

      // Draw the lead as the same bold cut band as the outline when this part came in toolpathed;
      // otherwise a thin line.
      double kerf = 0;
      this.KerfByPart?.TryGetValue(pp.Part.Name, out kerf);
      bool asBand = kerf > 0;
      var leadBrush = asBand ? KerfBand : LeadStroke;
      double leadWidths = asBand ? KerfBandPx : 1.5;

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
        this.AddPartOverlay(pp, new System.Windows.Shapes.Path
        {
          Data = geometry,
          Stroke = leadBrush,
          StrokeThickness = stroke * leadWidths,
          StrokeStartLineCap = PenLineCap.Round,
          StrokeEndLineCap = PenLineCap.Round,
          StrokeLineJoin = PenLineJoin.Round,
          Tag = leadWidths,
        });
      }
    }

    /// <summary>
    /// Dashed cut line + size label over the clean rectangular offcut(s) on the genuine remainder
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

      // Draw the SAME cut the export writes (single source: OffcutGeometry.RemnantRects), edge overlap
      // included, so the line visibly runs past the sheet exactly as far as it will on the machine.
      // Empty list = no offcut worth advertising.
      foreach (var r in RasterNest.OffcutGeometry.RemnantRects(cutX, cutY, w, h, this.OffcutOptions.EdgeOverlap))
      {
        // Sheet coords are Y-up; the canvas flips Y, so a rect at sheet [r.Y..r.Y+r.H] lands at canvas
        // top [h-(r.Y+r.H)].
        this.DrawOffcutCut(r.Cut, new Rect(r.X, h - (r.Y + r.H), r.W, r.H), w, h, stroke);
      }
    }

    /// <summary>Sheet coordinates are Y-up and the canvas is Y-down. Its own method because the flip is
    /// hand-written all over this file and getting it wrong shows up late and reads as a geometry bug.</summary>
    internal static (Point A, Point B) CutToCanvas(DeepNestLib.IO.OffcutLine cut, double sheetHeight)
      => (new Point(cut.X1, sheetHeight - cut.Y1), new Point(cut.X2, sheetHeight - cut.Y2));

    /// <summary>
    /// The offcut's dashed CUT LINE with the remnant's size label beside it. Only the one edge that is
    /// really cut is drawn: the other three sides of the remnant are the sheet's own edges, and outlining
    /// the whole rectangle implied cuts that never happen.
    /// </summary>
    private void DrawOffcutCut(DeepNestLib.IO.OffcutLine cut, Rect rect, double w, double h, double stroke)
    {
      if (rect.Width <= 0 || rect.Height <= 0)
      {
        return;
      }

      var (a, b) = CutToCanvas(cut, h);
      var line = new System.Windows.Shapes.Path
      {
        Data = new LineGeometry(a, b),
        Stroke = OffcutStroke,
        StrokeThickness = stroke * OffcutCutPx, // fixed width on screen, like the kerf band
        IsHitTestVisible = false,
        Tag = OffcutCutPx, // the cut line is told apart by THICKNESS, so a zoom must not flatten it
      };
      this.canvas.Children.Add(line);
      this.offcutShapes.Add(line);

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
      this.offcutShapes.Add(label);
    }

    private Brush FillFor(IPartPlacement pp)
    {
      return this.invalid.Contains(pp) ? InvalidFill : pp == this.selectedPp ? SelectedFill : this.TypeFill(pp);
    }

    /// <summary>The fill for a part's FILE, so several different parts on one sheet can be told apart at a
    /// glance — the matching thumbnail in the parts list is what names the colour. Every part in the job
    /// gets one, a single-part job included; the classic aluminium fill is only the fallback for a part the
    /// window never gave a colour. Brushes are cached and frozen — a sheet can hold hundreds of placements.</summary>
    private Brush TypeFill(IPartPlacement pp)
    {
      string key = PartColors.ColourKeyFor(pp);
      if (this.PartColours == null || pp == null || !this.PartColours.ContainsKey(key))
      {
        return PartFill;
      }

      if (!this.partBrushes.TryGetValue(key, out Brush brush))
      {
        var (r, g, b) = this.PartColours[key];

        // OPAQUE on purpose. The classic fill was translucent, and any colour drawn that way blends a
        // quarter of the near-white sheet into itself and comes out pastel - which is what made the first
        // attempt at this look grey.
        brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        this.partBrushes[key] = brush;
      }

      return brush;
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

    /// <summary>One device pixel expressed in drawing units at the current zoom. Stroke widths are in
    /// canvas units, which the RenderTransform scales, so a line stays a constant width on screen only if
    /// it is divided by the scale.</summary>
    private double CurrentStroke => StrokeScreenPx / Math.Max(0.0001, this.scale.ScaleX);

    /// <summary>
    /// Keeps every drawn line the same width on screen regardless of zoom. Lines that are drawn DELIBERATELY
    /// thick carry their multiplier in Tag: the cut band and the offcut's cut line are told apart by
    /// thickness rather than by colour, and this used to stamp one width onto every path, so a single wheel
    /// tick flattened both of them to a hairline.
    /// </summary>
    private void UpdateStrokeWidths()
    {
      double t = this.CurrentStroke;
      foreach (var child in this.canvas.Children)
      {
        if (child is System.Windows.Shapes.Path path)
        {
          path.StrokeThickness = t * (path.Tag is double multiplier ? multiplier : 1);
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
      this.UpdateHint();
    }

    /// <summary>The line under the canvas: what the mouse and keys do here, or why they do nothing.</summary>
    private void UpdateHint()
    {
      if (this.hintText == null || this.MeasureMode)
      {
        return; // measuring writes its own running commentary
      }

      this.hintText.Text = !this.EditMode
        ? "scroll = zoom  ·  wheel-drag = pan  ·  right-click = fit"
        : this.CurrentSheetIsEditable()
          ? "click = select part  ·  drag = move  ·  arrows = nudge  ·  type a coordinate to place it exactly  ·  overlaps show red"
          : "this layout is a production-plan proposal, not one of the nested sheets, so it cannot be edited  ·  the sheets it stands for are the ones that get cut";
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

      if (this.MeasureMode)
      {
        if (this.hintText != null)
        {
          this.hintText.Text = "measure: click two points (snaps to corners)";
        }
      }
      else
      {
        this.UpdateHint();
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
      this.snapPointsStale = false;
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
      if (this.snapPointsStale)
      {
        this.BuildSnapPoints(); // an edit moved a part; catch up now that someone is actually measuring
      }

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
      // Nothing is selectable on a sheet whose edits would be dropped, and with no selection every
      // move, rotate, mirror and typed coordinate downstream falls out on its own null check.
      this.selectedPp = this.CurrentSheetIsEditable() ? pp : null;
      foreach (var (path, p) in this.partPaths)
      {
        path.Fill = this.FillFor(p);
      }

      this.UpdateEditButtons();
      this.UpdatePartInfo(measureGap: true);
    }

    /// <summary>
    /// Fills the panel that says where the selected part is. X and Y are its BOTTOM-LEFT CORNER on the
    /// sheet, which is the thing an operator can go and measure; the placement's own X/Y is an internal
    /// offset into the polygon's frame and would mean nothing.
    /// </summary>
    /// <param name="measureGap">Whether to work out the distance to the nearest neighbour, which is a
    /// search and so is only done when the part is at rest, not on every mouse move.</param>
    /// <param name="force">Rewrite the field being typed in as well. Normally it is left alone so the
    /// operator is not fought for it, but cancelling means putting back what they were editing.</param>
    private void UpdatePartInfo(bool measureGap, bool force = false)
    {
      if (this.partInfo == null)
      {
        return;
      }

      var pp = this.selectedPp;
      var placed = pp?.PlacedPart;
      if (placed == null || !this.EditMode)
      {
        this.partInfo.Visibility = Visibility.Collapsed;
        return;
      }

      this.partInfo.Visibility = Visibility.Visible;
      this.partInfoName.Text = string.IsNullOrEmpty(pp.Part?.Name)
        ? "part"
        : System.IO.Path.GetFileName(pp.Part.Name);

      // Don't fight the operator for a field they are typing in, unless they just cancelled.
      if (force || !this.partX.IsKeyboardFocusWithin)
      {
        this.partX.Text = Shown(placed.MinX, PositionFormat);
      }

      if (force || !this.partY.IsKeyboardFocusWithin)
      {
        this.partY.Text = Shown(placed.MinY, PositionFormat);
      }

      if (force || !this.partAngle.IsKeyboardFocusWithin)
      {
        this.partAngle.Text = Shown(pp.Rotation, AngleFormat);
      }

      if (measureGap)
      {
        this.partGap.Text = this.DescribeNearestGap(pp);
      }
    }

    /// <summary>How far the part is from its nearest neighbour, in words, or blank when nothing is near
    /// enough to be worth a number.</summary>
    private string DescribeNearestGap(IPartPlacement pp)
    {
      var group = this.CurrentGroup();
      if (group?.Representative?.PartPlacements == null)
      {
        return string.Empty;
      }

      // Asked with NO allowance for the cut. The bisection settles where the answer flips, so allowing for
      // it here would put every reading a whole kerf out and make two parts in contact report a kerf of
      // daylight. A distance is a measurement; whether an overlap matters is a separate question.
      double cap = this.UnitsMm ? 25.0 : 1.0;
      double gap = NearestGap(
        pp,
        group.Representative.PartPlacements,
        cap,
        (a, b, d) => RasterNest.PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, d, 0));

      string unit = this.UnitsMm ? "mm" : "in";
      return gap >= cap
        ? string.Empty
        : $"gap {gap.ToString("0.###", CultureInfo.CurrentCulture)} {unit}";
    }

    /// <summary>
    /// The distance from one placement to the nearest of its neighbours, capped: past <paramref name="cap"/>
    /// nothing is close enough to be worth measuring, and the cap is what keeps this cheap on a full sheet.
    /// Overlapping reads as 0.
    /// <para>Found by bisection on "are these closer than d", because that question is the one the collision
    /// rule can answer and it is the same rule that decides red, so the number and the colour can never tell
    /// different stories.</para>
    /// </summary>
    internal static double NearestGap(
      IPartPlacement pp,
      IReadOnlyList<IPartPlacement> all,
      double cap,
      Func<IPartPlacement, IPartPlacement, double, bool> tooClose)
    {
      double best = cap;
      foreach (var other in all)
      {
        if (other == pp || other?.PlacedPart == null)
        {
          continue;
        }

        if (!tooClose(pp, other, best))
        {
          continue; // further off than anything found so far
        }

        if (tooClose(pp, other, 0))
        {
          return 0; // touching or overlapping; nothing can beat that
        }

        // 12 halvings of a 1in cap land inside a quarter of a thou, which is far finer than a number on
        // screen needs and keeps the arrow keys from stuttering. Neighbours in contact never get here at
        // all: they answer 0 above and end the search.
        double lo = 0, hi = best;
        for (int i = 0; i < 12; i++)
        {
          double mid = (lo + hi) / 2;
          if (tooClose(pp, other, mid))
          {
            hi = mid;
          }
          else
          {
            lo = mid;
          }
        }

        best = lo;
      }

      return best;
    }

    /// <summary>
    /// Landing in one of these fields selects what is in it, so the next keystroke replaces the number
    /// instead of appending to it. That is what these fields are for: you come here to put the part
    /// somewhere else, not to touch up a digit. WPF does not do it on its own, and it takes both halves -
    /// this one covers arriving by Tab.
    /// </summary>
    private void OnPartFieldFocus(object sender, RoutedEventArgs e) => (sender as TextBox)?.SelectAll();

    /// <summary>
    /// The other half, for arriving by mouse: the click has to be swallowed, because letting it through
    /// drops the caret where it landed and throws the selection away again. A second click, once the field
    /// already has the keyboard, behaves normally - that is how you get in to edit one digit.
    /// </summary>
    private void OnPartFieldClick(object sender, MouseButtonEventArgs e)
    {
      if (sender is TextBox box && !box.IsKeyboardFocusWithin)
      {
        box.Focus();
        e.Handled = true;
      }
    }

    /// <summary>Enter applies what was typed, Escape puts the field back to where the part actually is.</summary>
    private void OnPartFieldKey(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Enter)
      {
        this.CommitPartFields();
        this.host.Focus();
        e.Handled = true;
      }
      else if (e.Key == Key.Escape)
      {
        // Put the typed text back BEFORE letting the focus go. Leaving it standing meant the blur that
        // follows read it and applied the very edit that was being cancelled.
        this.UpdatePartInfo(measureGap: false, force: true);
        this.host.Focus();
        e.Handled = true;
      }
    }

    private void OnPartFieldCommit(object sender, RoutedEventArgs e) => this.CommitPartFields();

    internal const string PositionFormat = "0.###";

    internal const string AngleFormat = "0.##";

    /// <summary>What the panel would show for this value.</summary>
    internal static string Shown(double value, string format) => value.ToString(format, CultureInfo.CurrentCulture);

    /// <summary>
    /// Whether the operator actually typed something other than what the field was showing.
    /// <para>The panel shows rounded text and the placement holds the exact number, so comparing the two
    /// directly made every field look edited: landing in a box and leaving it moved the part by whatever
    /// the display had rounded away, pushed an undo step and marked the project dirty. A part at 4.7503218
    /// shown as 4.75 drifted a third of a thou each time it was looked at.</para>
    /// </summary>
    internal static bool WasEdited(string text, double current, string format)
      => !string.Equals(text?.Trim(), Shown(current, format), StringComparison.Ordinal);

    /// <summary>
    /// Reads a typed number, accepting the decimal separator of the operator's Windows and the point every
    /// CAD tool writes. Only the local one was accepted before, so on a comma-decimal machine "12.5" was
    /// dropped without a word and the field quietly went back to the old value.
    /// </summary>
    internal static bool TryParseTyped(string text, out double value)
      => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Puts the selected part exactly where the numbers say. Typed coordinates are the part's bottom-left
    /// corner, so they become a translation; the angle goes through the same rotate the buttons use, which
    /// turns about the part's centre and records one undo step. A field nobody touched is left alone, and
    /// a number that cannot be read is said out loud rather than reverted in silence.
    /// </summary>
    private void CommitPartFields()
    {
      var pp = this.selectedPp;
      var placed = pp?.PlacedPart;
      if (placed == null)
      {
        return;
      }

      bool unreadable = false;

      if (WasEdited(this.partAngle.Text, pp.Rotation, AngleFormat))
      {
        if (TryParseTyped(this.partAngle.Text, out double angle))
        {
          double delta = angle - pp.Rotation;
          if (Math.Abs(delta) > 1e-9)
          {
            this.RotateSelected(delta);
            pp = this.selectedPp; // rotating REPLACES the placement
            placed = pp.PlacedPart;
          }
        }
        else
        {
          unreadable = true;
        }
      }

      double dx = 0, dy = 0;
      if (WasEdited(this.partX.Text, placed.MinX, PositionFormat))
      {
        if (TryParseTyped(this.partX.Text, out double x))
        {
          dx = x - placed.MinX;
        }
        else
        {
          unreadable = true;
        }
      }

      if (WasEdited(this.partY.Text, placed.MinY, PositionFormat))
      {
        if (TryParseTyped(this.partY.Text, out double y))
        {
          dy = y - placed.MinY;
        }
        else
        {
          unreadable = true;
        }
      }

      if (Math.Abs(dx) > 1e-9 || Math.Abs(dy) > 1e-9)
      {
        var before = PlacementSnapshot.Of(pp);
        pp.X += dx;
        pp.Y += dy;
        this.PushEdit(before, PlacementSnapshot.Of(pp), nudge: false);
        this.RefreshSelectedPath();
        this.RefreshInvalid();
        this.CommitManualEdit();
      }

      this.UpdatePartInfo(measureGap: true, force: true);

      if (unreadable && this.hintText != null)
      {
        this.hintText.Text = "that is not a number I can read  ·  the part has been left where it was";
      }
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

      if (this.EditMode && this.CurrentSheetIsEditable())
      {
        // Topmost part under the cursor gets selected and dragged; empty space just deselects.
        Point pt = e.GetPosition(this.canvas);
        double sheetX = pt.X;
        double sheetY = this.currentSheetH - pt.Y; // the canvas is Y-down, the placements are Y-up
        var hit = PickAt(this.partPaths.Select(p => p.Pp).ToList(), sheetX, sheetY);
        if (hit != null)
        {
          this.SelectPart(hit);
          this.isDraggingPart = true;
          this.dragInvalid = false;
          this.dragStartCanvas = pt;
          this.dragStartX = this.selectedPp.X;
          this.dragStartY = this.selectedPp.Y;
          this.dragCache = this.BuildDragCache(this.selectedPp);
          this.host.CaptureMouse();
          this.host.Cursor = Cursors.Hand;
          return;
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
    /// Settles an Alt-drop at the last valid position along the drag path. Same walk the arrows use, with
    /// no magnet past the drop point (tMax = 1) - the mouse is asking to go exactly where it was released
    /// and no further.
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

      // t=0 is where the drag began, which is NOT necessarily legal - a part that was already overlapping
      // can be picked up and dragged. TrySnap handles that by carrying it out to the first legal spot;
      // failing that, it goes back where it came from.
      ValidAt(TrySnap(ValidAt, 1, out double t) ? t : 0);
      this.RefreshSelectedPath();
    }

    /// <summary>
    /// Repaints just the part that moved. Manual editing deliberately does not re-render, so everything
    /// that belongs to that part has to be brought along by hand: its outline, its cut band and lead-ins,
    /// and the offcut line, whose position is measured off the pack.
    /// </summary>
    private void RefreshSelectedPath()
    {
      this.snapPointsStale = true; // the part's outline moved, so the measure tool's snap points did too
      for (int i = 0; i < this.partPaths.Count; i++)
      {
        if (this.partPaths[i].Pp == this.selectedPp)
        {
          this.partPaths[i].Path.Data = BuildPlacedGeometry(this.selectedPp, this.currentSheetH);
          break;
        }
      }

      this.RefreshPartOverlays(this.selectedPp);
      this.RefreshOffcutOverlay();

      // Position live, distance to the neighbour only when it stops: measuring that exactly is a search.
      this.UpdatePartInfo(measureGap: !this.isDraggingPart);
    }

    private void OnPanEnd(object sender, MouseButtonEventArgs e)
    {
      if (this.isDraggingPart)
      {
        this.isDraggingPart = false;
        this.host.ReleaseMouseCapture();
        this.host.Cursor = null;

        // A drop lands where it was released, on top of a neighbour if that is what was asked: rearranging a
        // full sheet means parking a part somewhere occupied while its neighbour is moved next, and sliding
        // it back to the last free spot - sometimes all the way to where the drag began - made that "almost
        // impossible" (reported). What it does NOT do is land a hair away from its clearance: within half an
        // inch of something the part settles against it, exactly. Deeper than that it stays put and goes red,
        // so parking on top still works. Alt keeps the long slide back down the drag path.
        if (this.dragInvalid && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
          this.ResolveDropToContact();
        }
        else
        {
          this.SnapDropToSpacing();
        }

        this.dragInvalid = false;

        bool moved = Math.Abs(this.selectedPp.X - this.dragStartX) > 1e-9 || Math.Abs(this.selectedPp.Y - this.dragStartY) > 1e-9;
        if (moved)
        {
          var after = PlacementSnapshot.Of(this.selectedPp);
          var before = after;
          before.X = this.dragStartX;
          before.Y = this.dragStartY;
          this.PushEdit(before, after, nudge: false);
        }

        this.dragCache = null; // the layout changed — every later query rebuilds from the live placements
        this.SetSelectedFill(SelectedFill);
        this.CommitManualEdit();
        return;
      }

      // Panning belongs to the MIDDLE button, so releasing the left one has no business ending it. It used
      // to, which meant a stray left click in the middle of a pan dropped the view where it stood.
      if (this.isPanning)
      {
        return;
      }

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

      // The turn always applies, even into a neighbour - "rotated and repositioned before being placed in
      // their final location" is the whole point of editing by hand. An overlap shows red instead.
      this.PushEdit(PlacementSnapshot.Of(pp), PlacementSnapshot.Of(replacement), nudge: false);
      list[index] = replacement;
      this.invalid.Remove(pp);
      this.DropPartOverlays(pp); // the placement itself is being replaced, so its old cut band goes with it
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

      // Like the turn, the mirror always applies; an overlap shows red rather than refusing the edit.
      this.PushEdit(PlacementSnapshot.Of(pp), PlacementSnapshot.Of(replacement), nudge: false);
      list[index] = replacement;
      this.invalid.Remove(pp);
      this.DropPartOverlays(pp); // the placement itself is being replaced, so its old cut band goes with it
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
      // Interactive drag: the neighbours and the dragged part's shape are fixed, so answer from the cache
      // built when the drag started (see BuildDragCache) instead of rebuilding every polygon per query.
      if (this.dragCache != null && ReferenceEquals(candidate, this.selectedPp) && ReferenceEquals(exclude, this.selectedPp))
      {
        var (bMinX, bMinY, bMaxX, bMaxY) = this.dragCache.BoundsAt(candidate.X, candidate.Y);
        if (IsOutsideUsableArea(bMinX, bMinY, bMaxX, bMaxY, this.currentSheetW, this.currentSheetH, this.SheetEdgeMargin))
        {
          return false;
        }

        return !this.dragCache.AnyTooClose(candidate.X, candidate.Y);
      }

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
      => RasterNest.PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, this.ClearanceBetween(a, b), this.SliverBetween(a, b));

    /// <summary>The clearance this pair must keep: (spacingA + spacingB) / 2, or the common-line gap when
    /// the two are allowed to share a cut (they may touch, only real overlap fails). Which of the two it is
    /// depends on WHO the pair is, exactly as it does in the nester: a Same-part item touches its own kind
    /// and keeps its distance from everything else.</summary>
    private double ClearanceBetween(IPartPlacement a, IPartPlacement b)
      => ClearanceFor(this.SpacingOf(a), this.SpacingOf(b), this.MayShare(a, b));

    /// <summary>The clearance rule itself, over plain numbers so it can be tested without a control.</summary>
    internal static double ClearanceFor(double spacingA, double spacingB, bool mayShare)
    {
      if (mayShare)
      {
        return RasterNest.RasterCompact.CommonLineGap;
      }

      double clearance = (spacingA + spacingB) / 2.0;
      return clearance <= 0 ? RasterNest.RasterCompact.CommonLineGap : clearance;
    }

    /// <summary>
    /// Same rule the nester applies, on the same identity: the same drawing AND the same hand. Static over
    /// its inputs for the same reason IsOutsideUsableArea is — one notion of "may these two touch", callable
    /// from a test without standing up a WPF control.
    /// </summary>
    internal static bool MayShare(
      DeepNestLib.NestProject.CommonCuttingMode ccA, string pathA, bool mirroredA,
      DeepNestLib.NestProject.CommonCuttingMode ccB, string pathB, bool mirroredB)
    {
      if (ccA == DeepNestLib.NestProject.CommonCuttingMode.None || ccB == DeepNestLib.NestProject.CommonCuttingMode.None)
      {
        return false;
      }

      if (ccA == DeepNestLib.NestProject.CommonCuttingMode.SamePart || ccB == DeepNestLib.NestProject.CommonCuttingMode.SamePart)
      {
        return string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase) && mirroredA == mirroredB;
      }

      return true;
    }

    private bool MayShare(IPartPlacement a, IPartPlacement b)
      => MayShare(this.CcOf(a), a?.Part?.Name, a?.IsMirrored ?? false, this.CcOf(b), b?.Part?.Name, b?.IsMirrored ?? false);

    private DeepNestLib.NestProject.CommonCuttingMode CcOf(IPartPlacement pp)
    {
      string key = pp?.Part?.Name;
      return key != null && this.PartCommonCutting != null && this.PartCommonCutting.TryGetValue(key, out var cc)
        ? cc
        : DeepNestLib.NestProject.CommonCuttingMode.None;
    }

    /// <summary>
    /// How deep this pair may bite into each other before it means anything: the width of the cut that is
    /// about to run between them.
    /// <para>A common-line pair shares ONE cut and that cut takes out a kerf of material, so an overlap
    /// shallower than the kerf is not there once the sheet is cut - it was in the slot. Where a clearance
    /// was asked for, what overlaps is the grown shells rather than the parts, so the same figure reads as
    /// clearance the pair is short of, and a cut's width of that is nothing either. Reported as parts
    /// turning red when two radii met: they were a THOUSANDTH of an inch into each other, on a job whose
    /// kerf is six.</para>
    /// <para>The kerf is known for a job that came in toolpathed from SheetCam, which is also the job whose
    /// shared edge really does get cut once. For a plain DXF nobody has said how wide the cut is, so
    /// nothing is forgiven beyond the noise in the numbers themselves.</para>
    /// </summary>
    private double SliverBetween(IPartPlacement a, IPartPlacement b)
      => RasterNest.PlacementCollision.SliverFor(this.KerfOf(a), this.KerfOf(b));

    private double KerfOf(IPartPlacement pp)
    {
      string key = pp?.Part?.Name;
      return key != null && this.KerfByPart != null && this.KerfByPart.TryGetValue(key, out double kerf)
        ? Math.Max(0, kerf)
        : 0;
    }

    /// <summary>Precomputes the geometry that stays put while <paramref name="dragged"/> is moved, so a
    /// drag costs one Clipper intersection per touching neighbour instead of rebuilding every path and
    /// re-running a ClipperOffset on every mouse move.</summary>
    private RasterNest.DragCollisionCache BuildDragCache(IPartPlacement dragged)
    {
      var group = this.CurrentGroup();
      if (group == null || dragged?.Part == null)
      {
        return null;
      }

      var others = new List<(INfp Placed, double Clearance, double Sliver)>();
      foreach (var other in group.Representative.PartPlacements)
      {
        if (other != dragged)
        {
          others.Add((other.PlacedPart, this.ClearanceBetween(dragged, other), this.SliverBetween(dragged, other)));
        }
      }

      return new RasterNest.DragCollisionCache(dragged.Part, others);
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
        this.SetSelectedFill(this.selectedPp != null ? this.FillFor(this.selectedPp) : PartFill);
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
      // Typing a coordinate is typing, not nesting: while a field has the keyboard, arrows move the caret
      // and Ctrl+Z undoes the text. This handler is on the whole viewer, so without this every keystroke
      // meant for a box also moved the part.
      if (Keyboard.FocusedElement is TextBox)
      {
        return;
      }

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
        // coordinates (the canvas is Y-flipped). Within half an inch of a neighbour or of the sheet's edge
        // margin the part settles against it at exactly the clearance it owes - see NudgeSelected.
        double step = NudgeStep(this.UnitsMm, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        double dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
        double dy = e.Key == Key.Up ? step : e.Key == Key.Down ? -step : 0;
        this.NudgeSelected(dx, dy);
        e.Handled = true;
      }
    }

    /// <summary>
    /// How far one arrow press moves a part, IN THE DRAWING'S OWN UNITS. A single pair of numbers cannot
    /// serve both: 0.05 and 0.25 are a fine and a coarse nudge in inches, but the same figures in a metric
    /// job are a quarter of a millimetre at most - which is what "the increments are still very small, even
    /// holding Shift" was. The metric pair is the round equivalent of the imperial one (1.27 and 6.35 mm).
    /// <para>Its own method so it can be tested: this class of bug - a constant that is silently in drawing
    /// units - has bitten this project before.</para>
    /// </summary>
    internal static double NudgeStep(bool unitsMm, bool shift)
    {
      if (unitsMm)
      {
        return shift ? 5.0 : 1.0;
      }

      return shift ? 0.25 : 0.05;
    }

    /// <summary>
    /// How close a part has to come before the arrows settle it against what it is approaching: "una margen
    /// de 0.5 inch, cuando sea menos que se vaya automáticamente al part spacing y sheet spacing". The
    /// metric figure is the same physical distance, so a part settles identically whichever units the job
    /// is drawn in.
    /// </summary>
    internal static double SnapZone(bool unitsMm) => unitsMm ? 12.7 : 0.5;

    /// <summary>
    /// Where an arrow press should leave the part, as a multiple of the step it asked for: 1 is the plain
    /// step, less than 1 is settled short of it, more than 1 is pulled on to what lies ahead. False means
    /// nowhere on that line is legal and the press does nothing.
    /// <para>The point is that an arrow never leaves a part overlapping. Instead of refusing the press (which
    /// left a badly placed part stuck) or allowing it (which is how a part "se está quedando overlaping"),
    /// the part LANDS at exactly the clearance it owes - the part spacing against a neighbour, the sheet
    /// spacing against the edge. Both come for free: <paramref name="validAt"/> is the same test that paints
    /// the part red, so the boundary it settles on is precisely the one the operator is shown.</para>
    /// <para>Four situations, one walk along the line. A clear step with nothing within reach stays put at 1;
    /// a wall within reach ahead pulls it on to contact; a step that overshot backs off to contact; and a
    /// part that was ALREADY overlapping is carried out to the first legal spot, which is the clearance
    /// exactly. Pure, and tested as such - the geometry arrives through the delegate.</para>
    /// </summary>
    /// <param name="validAt">Whether the part may rest at t along the line, t=0 being where it started.</param>
    /// <param name="tMax">How far past the plain step the magnet reaches, as a multiple of the step.</param>
    internal static bool TrySnap(Func<double, bool> validAt, double tMax, out double t)
    {
      const int Steps = 48;
      const int Refine = 24;

      // Narrows onto the boundary between a known-good t and a known-bad one; returns the good side, so the
      // part rests just inside what it is allowed - that is where "exact" comes from.
      double Settle(double good, double bad)
      {
        for (int i = 0; i < Refine; i++)
        {
          double mid = (good + bad) / 2;
          if (validAt(mid))
          {
            good = mid;
          }
          else
          {
            bad = mid;
          }
        }

        return good;
      }

      // Started overlapping: out to the first legal spot ahead, which is the clearance exactly. This is
      // asked FIRST on purpose. A plain step that happens to clear the neighbour would also be legal, but it
      // leaves a wider gap than the job asked for - the part belongs AT the spacing, not past it.
      if (!validAt(0))
      {
        double lastBad = 0;
        for (int i = 1; i <= Steps; i++)
        {
          double c = tMax * i / Steps;
          if (validAt(c))
          {
            t = Settle(c, lastBad); // narrow from the far side: the least it can move and still be legal
            return true;
          }

          lastBad = c;
        }

        t = 0;
        return false;
      }

      if (validAt(1))
      {
        double lastGood = 1;
        for (int i = 1; tMax > 1 && i <= Steps; i++)
        {
          double c = 1 + ((tMax - 1) * i / Steps);
          if (!validAt(c))
          {
            t = Settle(lastGood, c);
            return true;
          }

          lastGood = c;
        }

        t = 1; // nothing within reach: the plain step, untouched
        return true;
      }

      // Overshot: back off to where it just fits.
      double good = 0;
      for (int i = 1; i <= Steps; i++)
      {
        double c = (double)i / Steps;
        if (!validAt(c))
        {
          t = Settle(good, c);
          return true;
        }

        good = c;
      }

      t = good;
      return true;
    }

    /// <summary>
    /// Where a DROP should settle, as an offset from where the part was released. An arrow press has an
    /// obvious direction for the magnet to travel along; a drag has none - its vector points back to
    /// wherever the part was picked up, which may be nowhere near what it was dropped against - so this
    /// works outward from the drop instead, over the four axes, and the smallest correction wins.
    /// <para>Three outcomes. Dropped overlapping: pushed out by the least that makes it legal. Dropped free:
    /// drawn on to whatever is nearest within reach, resting at exactly the clearance. Already touching
    /// something: left alone - without that a part settled against its right-hand neighbour would slide off
    /// on its own to a wall 0.4in to its left.</para>
    /// <para>False means nothing is within reach, or the overlap is too deep to escape, and the part stays
    /// exactly where it was dropped. That is what keeps "park it on top while I move its neighbour" working,
    /// which is the whole reason a drop is allowed to overlap in the first place.</para>
    /// </summary>
    /// <param name="validAtOffset">Whether the part may rest this far from where it was dropped.</param>
    /// <param name="reach">How far the magnet reaches — see <see cref="SnapZone"/>.</param>
    internal static bool TrySnapToNearest(Func<double, double, bool> validAtOffset, double reach, out double ox, out double oy)
    {
      const double Tol = 1e-6;

      ox = 0;
      oy = 0;
      bool droppedFree = validAtOffset(0, 0);
      double best = double.MaxValue;

      foreach (var (dx, dy) in Axes)
      {
        if (!TrySnap(t => validAtOffset(t * reach * dx, t * reach * dy), 1, out double t))
        {
          continue; // nothing legal that way
        }

        if (droppedFree)
        {
          if (t >= 1 - Tol)
          {
            continue; // walked the whole reach without meeting anything
          }

          if (t <= Tol)
          {
            return false; // already resting against this - the drop is settled, leave it
          }
        }

        double distance = t * reach;
        if (distance < best)
        {
          best = distance;
          ox = distance * dx;
          oy = distance * dy;
        }
      }

      return best < double.MaxValue;
    }

    /// <summary>True when the part is already up against something: legal where it stands, but not if it
    /// moves a hair in any direction. A part that is settled must not be pulled off to somewhere else by
    /// either magnet — one resting against its right-hand neighbour would otherwise go looking for the next
    /// nearest thing and slide away on its own.</summary>
    internal static bool RestsAgainstSomething(Func<double, double, bool> validAtOffset, double hair)
    {
      if (!validAtOffset(0, 0))
      {
        return false; // overlapping, not resting
      }

      foreach (var (dx, dy) in Axes)
      {
        if (!validAtOffset(dx * hair, dy * hair))
        {
          return true;
        }
      }

      return false;
    }

    /// <summary>The two offsets along one axis that seat the part against a neighbour — before it or after
    /// it — at exactly the clearance the pair owes. With a common-line pair the clearance is 0, so the two
    /// edges land EXACTLY on top of each other and the exporter emits the shared edge as a single cut.</summary>
    internal static (double Before, double After) EdgeAlignOffsets(
      double partMin, double partMax, double otherMin, double otherMax, double clearance)
      => (otherMin - clearance - partMax, otherMax + clearance - partMin);

    /// <summary>
    /// Drops the part into the nearest seat its neighbours offer, X and Y TOGETHER. The shortest valid
    /// combination within reach wins; false leaves the part exactly where it is.
    /// <para>This is what a search along one axis at a time cannot do. Every direction of
    /// <see cref="TrySnapToNearest"/> has to reach a fully legal position ON ITS OWN, so a part wedged
    /// between two neighbours AND out by a little in Y matches nothing and there is nothing to snap to —
    /// "cuando está en rojo muy pegado de dos partes es muy difícil o imposible centrarlo en esas dos partes,
    /// digamos con common line".</para>
    /// <para>The seats are computed, not hunted for: with a clearance of 0 the legal band can be narrower
    /// than any sweep would sample, but `neighbour.MinX - part.MaxX` is exact arithmetic. Each candidate is
    /// still put to the same validity test as everything else, so a seat that would foul a third part is
    /// simply not taken.</para>
    /// </summary>
    internal static bool TryFitToNeighbours(
      Func<double, double, bool> validAtOffset,
      IReadOnlyList<double> xCandidates,
      IReadOnlyList<double> yCandidates,
      double reach,
      out double ox,
      out double oy)
    {
      const double Tol = 1e-9;

      ox = 0;
      oy = 0;
      double best = double.MaxValue;

      foreach (double x in xCandidates)
      {
        foreach (double y in yCandidates)
        {
          double d = Math.Sqrt((x * x) + (y * y));
          if (d <= Tol || d > reach || d >= best)
          {
            continue; // no move at all, out of reach, or already beaten - and no probe wasted on it
          }

          if (validAtOffset(x, y))
          {
            best = d;
            ox = x;
            oy = y;
          }
        }
      }

      return best < double.MaxValue;
    }

    /// <summary>The offsets that would seat the part against each neighbour within reach, and against the
    /// sheet's own usable edge. A 0 in each list so a correction on one axis alone is a candidate too.</summary>
    private (List<double> Xs, List<double> Ys) BuildFitCandidates(IPartPlacement pp)
    {
      var xs = new List<double> { 0 };
      var ys = new List<double> { 0 };
      double reach = SnapZone(this.UnitsMm);
      var placements = this.CurrentGroup()?.Representative?.PartPlacements;
      var placed = pp.PlacedPart;

      void Offer(List<double> into, (double Before, double After) offsets)
      {
        if (Math.Abs(offsets.Before) <= reach)
        {
          into.Add(offsets.Before);
        }

        if (Math.Abs(offsets.After) <= reach)
        {
          into.Add(offsets.After);
        }
      }

      for (int i = 0; placements != null && i < placements.Count; i++)
      {
        var other = placements[i];
        if (other == pp)
        {
          continue;
        }

        var o = other.PlacedPart;
        if (o.MinX > placed.MaxX + reach || o.MaxX < placed.MinX - reach
            || o.MinY > placed.MaxY + reach || o.MaxY < placed.MinY - reach)
        {
          continue; // too far away to seat against
        }

        double clearance = this.ClearanceBetween(pp, other);
        Offer(xs, EdgeAlignOffsets(placed.MinX, placed.MaxX, o.MinX, o.MaxX, clearance));
        Offer(ys, EdgeAlignOffsets(placed.MinY, placed.MaxY, o.MinY, o.MaxY, clearance));
      }

      double m = Math.Max(0, this.SheetEdgeMargin);
      Offer(xs, (m - placed.MinX, this.currentSheetW - m - placed.MaxX));
      Offer(ys, (m - placed.MinY, this.currentSheetH - m - placed.MaxY));
      return (xs, ys);
    }

    /// <summary>Seats the part against its neighbours if one of their edges is within reach of where it
    /// stands. Returns whether it moved.</summary>
    private bool TryFitInPlace(IPartPlacement pp)
    {
      double x = pp.X;
      double y = pp.Y;
      bool ValidAtOffset(double ox, double oy)
      {
        pp.X = x + ox;
        pp.Y = y + oy;
        return this.IsPositionValid(pp, pp);
      }

      var (xs, ys) = this.BuildFitCandidates(pp);
      bool fitted = TryFitToNeighbours(ValidAtOffset, xs, ys, SnapZone(this.UnitsMm), out double fx, out double fy);
      ValidAtOffset(fitted ? fx : 0, fitted ? fy : 0);
      return fitted;
    }

    /// <summary>Settles a dropped part against whatever it landed near, at exactly the clearance it owes:
    /// "moviéndolo con el mouse debería detectar el margen de .5 pulgadas y hacer el snap automático al part
    /// spacing o sheet spacing". Part spacing and sheet spacing both fall out of the same validity test that
    /// paints the part red, so the two never disagree.
    /// <para>The seat is tried before the slide because it is the more precise answer of the two: it lands on
    /// an edge exactly, and it can correct both axes at once. The slide is what is left for a part that is
    /// merely near something rather than near a seat.</para></summary>
    private void SnapDropToSpacing()
    {
      var pp = this.selectedPp;
      if (pp == null)
      {
        return;
      }

      double x = pp.X;
      double y = pp.Y;
      bool ValidAtOffset(double ox, double oy)
      {
        pp.X = x + ox;
        pp.Y = y + oy;
        return this.IsPositionValid(pp, pp);
      }

      double reach = SnapZone(this.UnitsMm);
      bool resting = RestsAgainstSomething(ValidAtOffset, reach * 1e-4);
      ValidAtOffset(0, 0);
      if (resting)
      {
        return; // already up against something: the drop is settled as it stands
      }

      if (this.TryFitInPlace(pp))
      {
        this.RefreshSelectedPath();
        return;
      }

      bool snapped = TrySnapToNearest(ValidAtOffset, reach, out double sx, out double sy);
      ValidAtOffset(snapped ? sx : 0, snapped ? sy : 0); // settled, or back to exactly where it was dropped
      if (snapped)
      {
        this.RefreshSelectedPath();
      }
    }

    /// <summary>
    /// How far along the step an arrow press lands. False refuses the press outright.
    /// <para>Three rules, one per thing the operator asked for. A properly placed part settles at exactly the
    /// clearance when there is something within reach ("que se vaya automáticamente al part spacing"), and is
    /// REFUSED when it would have to overlap to move at all ("no estando en rojo que no haga overlaping").
    /// A part that is already overlapping moves regardless: it settles if a legal spot is within reach, and
    /// otherwise takes the plain step and stays red — because a part buried deeper than the magnet reaches
    /// had all four arrows dead, reported as "los arrow keys no están funcionando cuando está en rojo". That
    /// it can be pushed further in is the price of the keyboard always being able to walk it out, and it is
    /// payable now that the red is opaque enough to tell the operator which mode they are in.</para>
    /// </summary>
    internal static bool TryNudgeLanding(Func<double, bool> validAt, double tMax, out double t)
    {
      const double Tol = 1e-6;

      bool startedFree = validAt(0);
      if (TrySnap(validAt, tMax, out t) && (!startedFree || t > Tol))
      {
        return true;
      }

      t = 1;
      return !startedFree;
    }

    private void NudgeSelected(double dx, double dy)
    {
      var pp = this.selectedPp;
      if (pp == null)
      {
        return;
      }

      var before = PlacementSnapshot.Of(pp);
      bool ValidAt(double t)
      {
        pp.X = before.X + (t * dx);
        pp.Y = before.Y + (t * dy);
        return this.IsPositionValid(pp, pp);
      }

      double step = Math.Sqrt((dx * dx) + (dy * dy));
      double tMax = 1 + (SnapZone(this.UnitsMm) / step);

      // Settling probes the line dozens of times, and the drag already has the answer to "what does this
      // one part hit while nothing else moves" - the same invariant holds for a keypress. Without it a full
      // sheet would run a Clipper offset per neighbour per probe and the key would feel like it stuck.
      bool landed;
      this.dragCache = this.BuildDragCache(pp);
      try
      {
        landed = TryNudgeLanding(ValidAt, tMax, out double t);
        if (landed && !ValidAt(t))
        {
          // Walked to a spot that still overlaps. If a seat against the neighbours is within reach of there,
          // drop into it - that is how a part wedged between two others finds the one position that fits,
          // which stepping along a single axis never can.
          this.TryFitInPlace(pp);
        }
      }
      finally
      {
        this.dragCache = null;
      }

      if (!landed)
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
      var replaced = list[record.Index]; // the object leaves the sheet; it must leave the red set with it
      list[record.Index] = placement;
      this.selectedPp = placement;
      this.invalid.Remove(replaced);
      this.SeedInvalidOn(group); // the undone step may have cleared or created red elsewhere on THIS layout

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

    /// <summary>Whether the sheet on screen is one an edit can actually be made to
    /// (see <see cref="SheetGroup.IsEditable"/>).</summary>
    private bool CurrentSheetIsEditable() => this.CurrentGroup()?.IsEditable == true;

    /// <summary>
    /// After a manual move/rotate: mirror the representative's placements onto every physical copy of
    /// this layout (so "cut ×24" copies stay identical and the export matches), then re-evaluate which
    /// parts overlap or hang off the sheet (drawn red — the operator decides what to do about it).
    /// </summary>
    private void CommitManualEdit()
    {
      this.snapPointsStale = true;
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
          // Belongs to another layout, which this sweep has no way to judge: leaving it flagged is the
          // only honest answer. Clearing it instead meant one arrow key on layout 1 wiped the red off
          // layout 2, and since only a new result or an undo re-seeds, the export went on refusing over
          // parts that were no longer drawn red anywhere.
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

      this.UpdateOverlapCount();
    }

    /// <summary>Says in words how many parts are still wrong, because on a full sheet the offending one is
    /// often off screen and its red is easy to miss. It does not name the fault: a part can be red for
    /// overlapping or for standing in the sheet's edge margin, and this counter does not keep which. The
    /// refusal dialog does the full sweep and says which; claiming one here would be a guess.</summary>
    private void UpdateOverlapCount()
    {
      if (this.overlapCount == null)
      {
        return;
      }

      // Only what is wrong on the sheet being shown: `invalid` keeps entries from other layouts until they
      // are re-checked, and a count that counts another sheet's parts would send the operator hunting.
      var group = this.CurrentGroup();
      int n = group?.Representative?.PartPlacements == null
        ? 0
        : this.invalid.Count(p => group.Representative.PartPlacements.Contains(p));

      this.overlapCount.Text = n == 1 ? "1 part not fit to cut" : $"{n} parts not fit to cut";
      this.overlapCount.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Outside the usable area — which is the sheet less its edge margin on all four sides, the
    /// same margin the nester packed to. Without it a part could be dragged right to the sheet edge and the
    /// nest would quietly stop honouring what the job asked for.</summary>
    private bool OutOfSheet(IPartPlacement pp)
      => IsOutsideUsableArea(pp, this.currentSheetW, this.currentSheetH, this.SheetEdgeMargin);

    private static bool IsOutsideUsableArea(IPartPlacement pp, double sheetWidth, double sheetHeight, double margin)
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
    /// The placements on one sheet that are not fit to cut: overlapping a neighbour, or hanging off the
    /// sheet. Both sides of an overlap count, the same way both go red on screen.
    /// <para>Takes the sheet's OWN size rather than reading the viewer's: <see cref="OutOfSheet"/> measures
    /// against the sheet being LOOKED AT, which is right while editing one layout and wrong for checking a
    /// whole job - every other layout would be judged by the visible one's dimensions.</para>
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
    /// The same sweep, handing back WHICH parts and WHY rather than a number: the two faults need
    /// different things done about them (move a part, or re-nest with a smaller margin), and the operator
    /// is the one who has to tell them apart. <see cref="Unfit.All"/> is the union, since a part can be
    /// both on top of a neighbour and in the edge margin.
    /// </summary>
    /// <param name="sliverBetween">How deep a pair may bite into each other before it means anything - the
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
      sliverBetween = sliverBetween ?? ((a, b) => RasterNest.PlacementCollision.PlacementNoise);
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
          if (b?.PlacedPart != null && RasterNest.PlacementCollision.TooClose(placedA, b.PlacedPart, clearanceBetween(a, b), sliverBetween(a, b)))
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

    /// <summary>
    /// Layouts that must not be cut yet, and how many parts are wrong on each. Empty means the job is fit
    /// to export. Checks EVERY layout, not the one on screen: a part left overlapping on sheet two is just
    /// as much scrap, and the operator cannot see it from here.
    /// </summary>
    public IReadOnlyList<(string Layout, int Parts, int Overlapping, int InMargin)> FindUnfitLayouts()
    {
      var unfit = new List<(string Layout, int Parts, int Overlapping, int InMargin)>();
      for (int i = 0; i < this.groups.Count; i++)
      {
        var found = this.UnfitOn(this.groups[i]);
        if (found != null && found.All.Count > 0)
        {
          unfit.Add((
            string.IsNullOrWhiteSpace(this.groups[i].Name) ? $"Layout {i + 1}" : this.groups[i].Name,
            found.All.Count,
            found.Overlapping.Count,
            found.OutsideMargin.Count));
        }
      }

      return unfit;
    }

    /// <summary>What is wrong on one layout's representative sheet, judged by that sheet's OWN size and the
    /// current job's clearances. Null when the group has nothing to judge.</summary>
    private Unfit UnfitOn(SheetGroup group)
    {
      var sheet = group?.Representative;
      if (sheet?.PartPlacements == null || sheet.Sheet == null)
      {
        return null;
      }

      return FindUnfit(
        sheet.PartPlacements.ToList(),
        sheet.Sheet.WidthCalculated,
        sheet.Sheet.HeightCalculated,
        this.SheetEdgeMargin,
        this.ClearanceBetween,
        this.SliverBetween);
    }

    /// <summary>
    /// Flag everything that is not fit to cut, across every layout, so the red on screen says the same
    /// thing the export gate does.
    /// <para>Without this a nest arrives with <c>invalid</c> empty and only ever grows by what the operator
    /// touches: nothing is drawn red, the bar reads zero parts overlapping, and Export still refuses and
    /// tells them to go and clear the red parts. Which is what happened, on a nest nobody had edited.</para>
    /// </summary>
    private void SeedInvalid()
    {
      this.invalid.Clear();
      foreach (var group in this.groups)
      {
        this.SeedInvalidOn(group);
      }
    }

    /// <summary>
    /// The same judgement over ONE layout, leaving the other layouts' flags alone.
    /// <para>What undo and redo need: a step belongs to a single layout, and re-judging every one of them
    /// per keystroke put a pairwise sweep of the whole job on the dispatcher thread, so holding Ctrl+Z on
    /// a dense multi-layout nest crawled. Nothing here can decide anything about another layout, which is
    /// also why <c>RefreshInvalid</c> now leaves those flags standing rather than clearing them.</para>
    /// </summary>
    private void SeedInvalidOn(SheetGroup group)
    {
      var sheet = group?.Representative;
      if (sheet?.PartPlacements == null)
      {
        return;
      }

      foreach (var pp in sheet.PartPlacements)
      {
        this.invalid.Remove(pp);
      }

      var found = this.UnfitOn(group);
      if (found == null)
      {
        return;
      }

      foreach (var pp in found.All)
      {
        this.invalid.Add(pp);
      }
    }
  }
}
