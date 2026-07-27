namespace DeepNestSharp.Ui.Views
{
  using System;
  using System.Linq;
  using System.Windows;
  using DeepNestLib.IO;

  /// <summary>
  /// Shown when importing a 3D (STEP/IGES) file: reports the detected thickness and lets the user set
  /// the bend-allowance K-factor / standard / unit before unfolding. On OK the choices are written to
  /// <see cref="StepUnfoldService"/> so the unfold uses them.
  /// </summary>
  public partial class Import3DWindow : Window
  {
    private readonly double[] thicknessMm;

    public Import3DWindow(string fileName, int solidCount, double[] thicknessMm)
    {
      // Assign BEFORE InitializeComponent: the CheckBox's IsChecked="True" fires OnUnitChanged during
      // XAML load, which reads thicknessMm — it must not be null yet.
      this.thicknessMm = thicknessMm ?? Array.Empty<double>();
      InitializeComponent();

      this.fileText.Text = fileName;
      this.solidsText.Text = solidCount.ToString();
      this.kFactorUpDown.Value = StepUnfoldService.KFactor;
      this.standardCombo.SelectedIndex =
        string.Equals(StepUnfoldService.KFactorStandard, "din", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
      this.unitCheck.IsChecked = StepUnfoldService.UnfoldUnitInch;

      UpdateThickness();
    }

    private void OnUnitChanged(object sender, RoutedEventArgs e) => UpdateThickness();

    private void UpdateThickness()
    {
      if (this.thicknessMm == null || this.thicknessText == null)
      {
        return;
      }

      var vals = this.thicknessMm.Where(t => t > 0).ToArray();
      if (vals.Length == 0)
      {
        this.thicknessText.Text = "Not detected";
        return;
      }

      bool inch = this.unitCheck.IsChecked == true;
      double conv = inch ? 1.0 / 25.4 : 1.0;
      string unit = inch ? "in" : "mm";
      int digits = inch ? 4 : 3;

      var distinct = vals.Select(t => Math.Round(t * conv, digits)).Distinct().OrderBy(v => v).ToArray();
      this.thicknessText.Text = distinct.Length == 1
        ? $"{distinct[0]} {unit}"
        : $"varies ({distinct.First()}–{distinct.Last()} {unit})";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
      StepUnfoldService.KFactor = this.kFactorUpDown.Value ?? StepUnfoldService.KFactor;
      StepUnfoldService.KFactorStandard = this.standardCombo.SelectedIndex == 1 ? "din" : "ansi";
      StepUnfoldService.UnfoldUnitInch = this.unitCheck.IsChecked == true;
      this.DialogResult = true;
    }
  }
}
