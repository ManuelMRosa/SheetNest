namespace DeepNestSharp.Ui.Views
{
  using System.Windows;
  using System.Windows.Media;
  using DeepNestLib.NestProject;
  using DeepNestSharp.Ui.Converters;

  /// <summary>
  /// Radan-style "Edit Part" dialog: part file + preview, required/extra quantity, per-part permitted
  /// orientations and nesting priority. Only writes back to the part on OK.
  /// </summary>
  public partial class EditPartWindow : Window
  {
    private readonly IDetailLoadInfo part;
    private readonly int globalRotations;

    public EditPartWindow(IDetailLoadInfo part, int globalRotations)
    {
      this.part = part;
      this.globalRotations = globalRotations;
      InitializeComponent();

      this.partFileText.Text = part.Path;
      this.previewImage.Source = new PartPreviewConverter().Convert(part, typeof(ImageSource), null, null) as ImageSource;

      this.requiredUpDown.Value = part.Quantity;
      this.extraUpDown.Value = part.Extra;

      // Rotation is per-part only (no global rotation UI); a part that has never been edited
      // starts from the engine's configured default.
      this.rotationSelector.Rotations = part.Rotations > 0 ? part.Rotations : globalRotations;

      for (int p = 0; p <= 10; p++)
      {
        this.priorityCombo.Items.Add($"{p} ({LabelFor(p)})");
      }

      this.priorityCombo.SelectedIndex = System.Math.Max(0, System.Math.Min(10, part.Priority));
    }

    private static string LabelFor(int priority)
    {
      if (priority <= 0)
      {
        return "Lowest";
      }

      if (priority <= 3)
      {
        return "Low";
      }

      if (priority <= 6)
      {
        return "Medium";
      }

      if (priority <= 9)
      {
        return "High";
      }

      return "Highest";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
      this.part.Quantity = this.requiredUpDown.Value ?? this.part.Quantity;
      this.part.Extra = this.extraUpDown.Value ?? this.part.Extra;

      int rotations = this.rotationSelector.Rotations;
      this.part.Rotations = rotations;

      // Best-effort mapping for the NFP (CPU) engine, whose per-part restriction is the coarse
      // AnglesEnum; the raster engine honours the exact per-part rotation count instead.
      this.part.StrictAngle = rotations <= 2 ? AnglesEnum.AsPreviewed
        : rotations <= 4 ? AnglesEnum.Rotate90
        : AnglesEnum.None;

      int priority = this.priorityCombo.SelectedIndex < 0 ? 5 : this.priorityCombo.SelectedIndex;
      this.part.Priority = priority;
      this.part.IsPriority = priority >= 6; // the NFP engine's priority is a flag: "nest these first"

      this.DialogResult = true;
    }
  }
}
