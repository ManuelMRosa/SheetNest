namespace DeepNestSharp.Ui.Views
{
  using System.Windows;

  /// <summary>Small dialog for a custom stock sheet size (the Add Sheet menu's "Custom size…").</summary>
  public partial class AddSheetWindow : Window
  {
    public AddSheetWindow()
    {
      InitializeComponent();
    }

    public int SheetWidth { get; private set; } = 120;

    public int SheetHeight { get; private set; } = 60;

    public int SheetQuantity { get; private set; } = 1;

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
      this.DialogResult = true;
    }
  }
}
