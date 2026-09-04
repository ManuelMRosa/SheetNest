namespace DeepNestSharp.Ui.Views
{
  using System.Windows;
  using System.Windows.Media;
  using DeepNestLib.NestProject;
  using DeepNestSharp.Ui.Converters;

  /// <summary>
  /// "Edit Part" dialog: part file + preview, required/extra quantity, per-part permitted
  /// orientations and nesting priority. Only writes back to the part on OK.
  /// </summary>
  public partial class EditPartWindow : Window
  {
    private readonly IDetailLoadInfo part;
    private readonly int globalRotations;
    private bool commonCuttingSelectionChanged;

    public EditPartWindow(IDetailLoadInfo part, int globalRotations, double defaultSpacing)
    {
      this.part = part;
      this.globalRotations = globalRotations;
      InitializeComponent();

      this.partFileText.Text = part.Path;
      this.previewImage.Source = new PartPreviewConverter().Convert(part, typeof(ImageSource), null, null) as ImageSource;

      this.requiredUpDown.Value = part.Quantity;
      this.extraUpDown.Value = part.Extra;
      this.mirroredUpDown.Value = part.MirrorQuantity;

      // Spacing is per-part; a part that has never been edited starts from the job default.
      this.spacingUpDown.Value = part.Spacing >= 0 ? part.Spacing : defaultSpacing;

      // Common cutting is a MODE, not a switch, and spacing stays live in all three: it is what the
      // part keeps to everything it may not share a cut with. SheetCam parts show their stored mode;
      // direct DXF/STEP parts show only their EFFECTIVE mode. Direct SVG/other formats stay hidden:
      // their exporter cannot emit the resolved CUT_OUTER + midpoint COMMON_LINE cluster contract.
      if (ShowsCommonCuttingSelector(part))
      {
        this.commonCuttingGroup.Visibility = Visibility.Visible;
        this.commonCuttingCombo.Items.Add("None");
        this.commonCuttingCombo.Items.Add("Unrestricted (compatible parts)");
        this.commonCuttingCombo.Items.Add("Same part");
        this.commonCuttingCombo.SelectedIndex = (int)part.EffectiveCommonCutting;
        this.commonCuttingCombo.SelectionChanged += (_, __) => this.commonCuttingSelectionChanged = true;

        if (string.IsNullOrWhiteSpace(part.NestSourcePath)
            && !part.DirectCommonCuttingEnabled
            && part.CommonCutting != CommonCuttingMode.None)
        {
          this.commonCuttingHint.Text = "A saved legacy common-cutting mode is inactive. Choose a mode to confirm it for this direct DXF/STEP part.";
          this.commonCuttingHint.Visibility = Visibility.Visible;
        }
      }

      var poly = LoadPolygon(part.Path);

      // Rotation is per-part only (no global rotation UI). A part that has never been edited gets a
      // GEOMETRY-BASED suggestion: 90°-rotation-symmetric shapes (circles, squares) default to
      // "No turn" — rotating them cannot change the nest. Everything else starts at 90° steps, which
      // the engine's best-of search narrows per job automatically (e.g. triangles win at {0,180}).
      // A part that has chosen nothing stays that way, and OK will not choose for it. This dialog used to
      // seed the job's number and write it straight back on every OK, with no way to say "follow the job",
      // so opening a part once and pressing OK pinned it for good: the job could then be set to free
      // rotation and that part would go on turning in ninety degree steps, which is what a user spent a
      // month reporting. The symmetric shape is still worth pointing out, but as a hint, not a decision.
      if (part.Rotations > 0)
      {
        this.rotationSelector.Rotations = part.Rotations;
      }
      else
      {
        this.rotationSelector.Rotations = UserControls.RotationSelector.InheritsJob;
        if (poly != null && IsRotationSymmetric(poly))
        {
          this.geoHint.Text = "Detected rotation-symmetric shape (circle/square): turning it can't improve the nest, so \"As drawn\" saves the search.";
          this.geoHint.Visibility = Visibility.Visible;
        }
      }

      // A DXF that is really a WHOLE NESTED SHEET (a frame rectangle full of part silhouettes) imports
      // as one giant part-with-holes — the inner gaps come from the original drawing, and no SheetNest
      // spacing setting can change them. Warn loudly instead of nesting garbage silently.
      if (poly != null && LooksLikeNestedSheet(poly))
      {
        this.warnHint.Text = $"⚠ This DXF looks like a FULL NESTED SHEET ({(poly.Children?.Count ?? 0)} shapes inside a "
          + $"{poly.MaxX - poly.MinX:0.#}×{poly.MaxY - poly.MinY:0.#} frame), not a single part. SheetNest imports each DXF "
          + "as ONE part, and the gaps between the inner shapes come from the original drawing. Export each part as its own DXF instead.";
        this.warnHint.Visibility = Visibility.Visible;
      }

      for (int p = 1; p <= 10; p++)
      {
        this.priorityCombo.Items.Add($"{p} ({LabelFor(p)})");
      }

      // 1 = highest → combo index 0. Clamp legacy/out-of-range values into 1..10.
      this.priorityCombo.SelectedIndex = System.Math.Max(1, System.Math.Min(10, part.Priority)) - 1;

      // 3D-unfolded parts get an editable K-factor + a read-only detected thickness. Changing K
      // re-unfolds the part (handled by the caller after OK). Hidden for plain 2D (DXF) parts.
      if (!string.IsNullOrEmpty(part.SourceStepPath))
      {
        this.unfold3DGroup.Visibility = Visibility.Visible;
        this.kFactor3DUpDown.Value = part.KFactor;
        this.std3DCombo.SelectedIndex = string.Equals(part.KFactorStandard, "din", System.StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        this.thickness3DText.Text = FormatThickness(part.ThicknessMm, part.UnfoldUnitInch);
      }
    }

    /// <summary>The mode named by the combo, or the existing raw mode when it has no selection.</summary>
    internal static CommonCuttingMode ChosenMode(int selectedIndex, CommonCuttingMode current)
      => selectedIndex < 0 ? current : (CommonCuttingMode)System.Math.Min(2, selectedIndex);

    /// <summary>Only formats with a cut-once output contract expose the selector.</summary>
    internal static bool ShowsCommonCuttingSelector(IDetailLoadInfo part)
      => part?.SupportsCommonCutting ?? false;

    /// <summary>
    /// Resolves the selector without treating its initial, effective value as a user choice. A direct
    /// DXF/STEP confirms common cutting only after the selection changes; selecting None withdraws that
    /// confirmation. SheetCam provenance already enables the stored mode, so its direct flag is untouched.
    /// </summary>
    internal static (CommonCuttingMode Mode, bool DirectEnabled) ChosenCommonCutting(
      int selectedIndex,
      bool selectionChanged,
      CommonCuttingMode current,
      bool directEnabled,
      bool fromSheetCam,
      bool supportsCommonCutting = true)
    {
      if (!supportsCommonCutting)
      {
        // The hidden control cannot be a user choice. Preserve raw project data; the model keeps it
        // ineffective until the part has a supported output contract.
        return (current, directEnabled);
      }

      if (!selectionChanged || selectedIndex < 0)
      {
        return (current, directEnabled);
      }

      var chosen = ChosenMode(selectedIndex, current);
      return (chosen, fromSheetCam ? directEnabled : chosen != CommonCuttingMode.None);
    }

    private static string FormatThickness(double thicknessMm, bool inch)
    {
      if (thicknessMm <= 0)
      {
        return "n/a";
      }

      return inch
        ? (thicknessMm / 25.4).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + " in"
        : thicknessMm.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " mm";
    }

    private static DeepNestLib.INfp LoadPolygon(string path)
    {
      try
      {
        var det = new DeepNestLib.NestExecutionHelper().LoadRawDetail(new System.IO.FileInfo(path));
        return det != null && det.TryConvertToNfp(0, out DeepNestLib.INfp poly) && poly.Points.Length >= 3 ? poly : null;
      }
      catch
      {
        return null;
      }
    }

    /// <summary>
    /// A frame-like outer contour (near-perfect rectangle filling its bbox) stuffed with several large
    /// inner shapes = a whole nested sheet imported as one part. Functional holes stay well under the
    /// area threshold, so a plate with bolt holes doesn't trip this.
    /// </summary>
    private static bool LooksLikeNestedSheet(DeepNestLib.INfp poly)
    {
      if (poly.Children == null || poly.Children.Count < 3 || poly.Points.Length > 8)
      {
        return false;
      }

      double bboxArea = (poly.MaxX - poly.MinX) * (poly.MaxY - poly.MinY);
      if (bboxArea <= 0)
      {
        return false;
      }

      double childArea = System.Linq.Enumerable.Sum(poly.Children, c => System.Math.Abs(c.NetArea));
      double outerFillsBox = (poly.NetArea + childArea) / bboxArea;
      double childShare = childArea / bboxArea;
      return outerFillsBox >= 0.98 && childShare >= 0.3;
    }

    /// <summary>
    /// True when the part looks the same rotated 90°, so rotation can't improve the nest. Two checks:
    /// (1) analytic circle — every outline vertex equidistant (±2%) from the bbox centre, which is
    /// robust to uneven arc tessellation (bulge polylines don't rotate onto themselves pixel-exactly);
    /// (2) exact coarse-mask comparison at 0° vs 90° (squares, symmetric flanges). Load failure = false.
    /// </summary>
    private static bool IsRotationSymmetric(DeepNestLib.INfp poly)
    {
      try
      {
        // Analytic circle test (outer contour only; a part with holes must pass the mask test instead,
        // since an off-centre hole breaks the symmetry the outline alone would suggest).
        if ((poly.Children == null || poly.Children.Count == 0) && poly.Points.Length >= 12)
        {
          double cx = (poly.MinX + poly.MaxX) / 2.0;
          double cy = (poly.MinY + poly.MaxY) / 2.0;
          double rMin = double.MaxValue;
          double rMax = 0;
          foreach (var p in poly.Points)
          {
            double r = System.Math.Sqrt(((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy)));
            rMin = System.Math.Min(rMin, r);
            rMax = System.Math.Max(rMax, r);
          }

          if (rMax > 0 && (rMax - rMin) <= 0.02 * rMax)
          {
            return true;
          }
        }

        var m0 = RasterNest.RasterUtil.Rasterize(poly, 8.0);
        var m90 = RasterNest.RasterUtil.Rasterize(poly.Rotate(90), 8.0);
        return m0.W == m90.W && m0.H == m90.H
          && System.MemoryExtensions.SequenceEqual<bool>(m0.Bits, m90.Bits);
      }
      catch
      {
        return false;
      }
    }

    private static string LabelFor(int priority)
    {
      // 1 = highest priority (nested first), 10 = lowest.
      if (priority <= 1)
      {
        return "Highest";
      }

      if (priority <= 3)
      {
        return "High";
      }

      if (priority <= 6)
      {
        return "Normal";
      }

      if (priority <= 9)
      {
        return "Low";
      }

      return "Lowest";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
      // Xceed up/downs only commit TYPED text on focus loss — pressing Enter (OK is IsDefault) would
      // otherwise save the stale previous value (e.g. a typed spacing silently ignored).
      this.requiredUpDown.CommitInput();
      this.extraUpDown.CommitInput();
      this.mirroredUpDown.CommitInput();
      this.spacingUpDown.CommitInput();

      this.part.Quantity = this.requiredUpDown.Value ?? this.part.Quantity;
      this.part.Extra = this.extraUpDown.Value ?? this.part.Extra;
      this.part.MirrorQuantity = this.mirroredUpDown.Value ?? this.part.MirrorQuantity;
      this.part.Spacing = System.Math.Max(0, this.spacingUpDown.Value ?? 0);

      var commonCutting = ChosenCommonCutting(
        this.commonCuttingCombo.SelectedIndex,
        this.commonCuttingSelectionChanged,
        this.part.CommonCutting,
        this.part.DirectCommonCuttingEnabled,
        !string.IsNullOrWhiteSpace(this.part.NestSourcePath),
        this.part.SupportsCommonCutting);
      this.part.CommonCutting = commonCutting.Mode;
      this.part.DirectCommonCuttingEnabled = commonCutting.DirectEnabled;

      int rotations = this.rotationSelector.Rotations;
      this.part.Rotations = rotations;

      // Best-effort mapping for the NFP (CPU) engine, whose per-part restriction is the coarse
      // AnglesEnum; the raster engine honours the exact per-part rotation count instead.
      this.part.StrictAngle = rotations <= 2 ? AnglesEnum.AsPreviewed
        : rotations <= 4 ? AnglesEnum.Rotate90
        : AnglesEnum.None;

      int priority = this.priorityCombo.SelectedIndex < 0 ? 5 : this.priorityCombo.SelectedIndex + 1; // combo 1..10
      this.part.Priority = priority;
      this.part.IsPriority = priority < 5; // legacy NFP flag "nest these first" — now 1 = highest

      // 3D parts: write the chosen K-factor/standard. The caller (OpenEditPart) compares against the
      // pre-dialog values and re-unfolds if they changed.
      if (!string.IsNullOrEmpty(this.part.SourceStepPath))
      {
        this.kFactor3DUpDown.CommitInput();
        this.part.KFactor = this.kFactor3DUpDown.Value ?? this.part.KFactor;
        this.part.KFactorStandard = this.std3DCombo.SelectedIndex == 1 ? "din" : "ansi";
      }

      this.DialogResult = true;
    }
  }
}
