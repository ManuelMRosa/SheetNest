namespace DeepNestSharp.Ui.Views
{
  using System.Windows;

  /// <summary>Small dialog for a custom stock sheet size (the Add Sheet menu's "Custom size…").</summary>
  public partial class AddSheetWindow : Window
  {
    public AddSheetWindow()
      : this(false)
    {
    }

    /// <summary>Units drive labels, limits and defaults: metric shops need 3000x1500 sheets.</summary>
    public AddSheetWindow(bool unitsMm)
    {
      InitializeComponent();
      if (unitsMm)
      {
        this.headerText.Text = "Sheet size in millimeters (width = the long side).";
        this.widthLabel.Text = "Width (mm)";
        this.heightLabel.Text = "Height (mm)";
        this.widthUpDown.Maximum = 25000;
        this.heightUpDown.Maximum = 25000;
        this.widthUpDown.Value = 3000;
        this.heightUpDown.Value = 1500;
      }
    }

    /// <summary>Reuses the dialog to EDIT an existing stock row: pre-filled values, edit wording.</summary>
    public void PrefillForEdit(int width, int height, int quantity, bool unlimited = false)
    {
      this.Title = "Edit sheet";
      this.okButton.Content = "OK";
      this.widthUpDown.Value = width;
      this.heightUpDown.Value = height;
      this.qtyUpDown.Value = quantity;
      this.unlimitedCheck.IsChecked = unlimited;
    }

    public int SheetWidth { get; private set; } = 120;

    public int SheetHeight { get; private set; } = 60;

    public int SheetQuantity { get; private set; } = 1;

    /// <summary>The job may take as many of this size as it needs.</summary>
    public bool SheetUnlimited { get; private set; }

    /// <summary>Save this size under "My sheets" in the Add Sheet menu.</summary>
    public bool RememberAsPreset => this.rememberCheck.IsChecked == true;

    /// <summary>Quantity means nothing while the size is unlimited, so it greys out. Its VALUE is left
    /// alone: unticking must give back the number that was there, not a default.</summary>
    private void OnUnlimitedChanged(object sender, RoutedEventArgs e)
      => this.qtyUpDown.IsEnabled = this.unlimitedCheck.IsChecked != true;

    private void OnOk(object sender, RoutedEventArgs e)
    {
      // Xceed up/downs only commit TYPED text on focus loss — commit before reading so pressing
      // Enter doesn't silently save the previous value.
      this.widthUpDown.CommitInput();
      this.heightUpDown.CommitInput();
      this.qtyUpDown.CommitInput();

      this.SheetWidth = this.widthUpDown.Value ?? 120;
      this.SheetHeight = this.heightUpDown.Value ?? 60;
      this.SheetQuantity = this.qtyUpDown.Value ?? 1;
      this.SheetUnlimited = this.unlimitedCheck.IsChecked == true;
      this.DialogResult = true;
    }
  }
}
