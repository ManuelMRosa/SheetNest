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

      this.commonLineCheck.IsChecked = part.CommonLine;
      this.spacingUpDown.IsEnabled = !part.CommonLine;

      var poly = LoadPolygon(part.Path);

      // Rotation is per-part only (no global rotation UI). A part that has never been edited gets a
      // GEOMETRY-BASED suggestion: 90°-rotation-symmetric shapes (circles, squares) default to
      // "No turn" — rotating them cannot change the nest. Everything else starts at 90° steps, which
      // the engine's best-of search narrows per job automatically (e.g. triangles win at {0,180}).
      if (part.Rotations > 0)
      {
        this.rotationSelector.Rotations = part.Rotations;
      }
      else if (poly != null && IsRotationSymmetric(poly))
      {
        this.rotationSelector.Rotations = 1;
        this.geoHint.Text = "Detected rotation-symmetric shape (circle/square): turning it can't improve the nest.";
        this.geoHint.Visibility = Visibility.Visible;
      }
      else
      {
        this.rotationSelector.Rotations = globalRotations;
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

    private void OnCommonLineChanged(object sender, RoutedEventArgs e)
    {
      if (this.spacingUpDown != null)
      {
        // Common-line = spacing 0 by definition; the field stays visible (greyed) so the previous
        // value is still there when the box is unticked.
        this.spacingUpDown.IsEnabled = this.commonLineCheck.IsChecked != true;
      }
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

      this.part.CommonLine = this.commonLineCheck.IsChecked == true;

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
