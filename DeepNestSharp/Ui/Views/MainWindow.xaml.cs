namespace DeepNestSharp.Ui.Views
{
  using System.Linq;
  using System.Windows;
  using DeepNestSharp.Domain.ViewModels;

  public partial class MainWindow : Window
  {
    public MainWindow(IMainViewModel viewModel)
    {
      InitializeComponent();
      this.DataContext = viewModel;
      viewModel.AboutDialogService = new AboutDialogService(() => new AboutDialog());
      this.Loaded += MainWindow_Loaded;
    }

    public IMainViewModel ViewModel => (IMainViewModel)DataContext;

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
      // Start ready: ensure a project exists so the operator can load parts/sheets immediately.
      if (ViewModel.ActiveDocument == null && ViewModel.CreateNestProjectCommand.CanExecute(null))
      {
        ViewModel.CreateNestProjectCommand.Execute(null);
      }
    }

    private async void OnExportDxf(object sender, RoutedEventArgs e)
    {
      var selected = ViewModel.NestMonitorViewModel?.SelectedItem;
      if (selected == null || selected.UsedSheets == null || selected.UsedSheets.Count == 0)
      {
        ViewModel.MessageService.DisplayMessageBox(
          "Run a nest and select a result first, then export.",
          "Export DXF",
          DeepNestLib.MessageBoxIcon.Information);
        return;
      }

      // Export every used sheet of the selected result.
      foreach (var sheetPlacement in selected.UsedSheets.ToList())
      {
        await ViewModel.ExportSheetPlacementAsync(sheetPlacement);
      }
    }
  }
}
