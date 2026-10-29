namespace DeepNestSharp
{
  using System;
  using System.Globalization;
  using System.IO;
  using System.Text;
  using System.Windows;
  using DeepNestLib;
  using DeepNestSharp.Domain.Services;
  using DeepNestSharp.Domain.ViewModels;
  using DeepNestSharp.Ui.Services;
  using DeepNestSharp.Ui.ViewModels;
  using DeepNestSharp.Ui.Views;
  using Microsoft.Extensions.DependencyInjection;
  using Microsoft.Extensions.Hosting;
  using SheetNest.Localization;

  /// <summary>
  /// Interaction logic for App.xaml.
  /// </summary>
  public partial class App : Application
  {
    private readonly IHost host;

    public App()
    {
      // Consent-based crash reporting: log locally, offer a pre-filled GitHub issue, never send
      // anything by itself. The nest path has its own non-fatal catch in MainWindow.OnNest; these
      // are the last-resort nets for everything else.
      this.DispatcherUnhandledException += (s, e) =>
      {
        e.Handled = true; // keep WPF from tearing down before the dialog shows
        CrashReporter.Show(e.Exception, "ui", Current?.MainWindow);
        Environment.Exit(1); // state is unknown — exit without running half-broken save prompts
      };
      AppDomain.CurrentDomain.UnhandledException += (s, e) =>
      {
        var ex = e.ExceptionObject as Exception;
        CrashReporter.Save(ex, "background");
        try
        {
          this.Dispatcher.Invoke(() => CrashReporter.Show(ex, "background", Current?.MainWindow));
        }
        catch
        {
          // The process is dying; the log on disk is what matters.
        }
      };
      System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
      {
        CrashReporter.Save(e.Exception, "task");
        e.SetObserved();
      };

      // Before any window exists: every text is read from the translation when its window is built.
      Languages.Apply(Languages.Resolve(
        SessionState.Load()?.Language,
        CultureInfo.CurrentUICulture,
        Languages.Available(AppContext.BaseDirectory)));

      host = new HostBuilder()
           .ConfigureServices((hostContext, services) =>
           {
             services.AddScoped<ISettingsService, SettingsService>();
             services.AddScoped<IMouseCursorService, MouseCursorService>();
             services.AddScoped<IFileIoService, FileIoService>();
             services.AddScoped<IMessageService, MessageBoxService>();
             services.AddTransient<INestProjectViewModel, NestProjectViewModel>();
             services.AddSingleton(SvgNest.Config);
             services.AddSingleton<IDispatcherService>(new DispatcherService(this.Dispatcher));
             services.AddSingleton<IMainViewModel, DockingMainViewModel>();
             services.AddSingleton<MainWindow>();
           }).Build();

      using (var serviceScope = host.Services.CreateScope())
      {
        var services = serviceScope.ServiceProvider;
        try
        {
          var mainWindow = services.GetRequiredService<MainWindow>();
          mainWindow?.Show();

          var args = Environment.GetCommandLineArgs();
          if (args != null && args.Length > 0)
          {
            var mainViewModel = services.GetRequiredService<IMainViewModel>();

            foreach (var arg in args)
            {
              if (arg.StartsWith("--", StringComparison.Ordinal))
              {
                continue; // a switch, not a file
              }

              var fileInfo = new FileInfo(arg);
              if (fileInfo.Exists)
              {
                if (fileInfo.Extension == ".dll" ||
                    fileInfo.Extension == ".exe")
                {
                  //NOP
                }
                else if (fileInfo.Extension == ".dnr")
                {
                  mainViewModel.LoadNestResult(fileInfo.FullName);
                }
                else if (fileInfo.Extension == ".dnest")
                {
                  mainViewModel.OnLoadNestProject(fileInfo.FullName);
                }
                else if (fileInfo.Extension == ".dnsp")
                {
                  mainViewModel.LoadSheetPlacement(fileInfo.FullName);
                }
                else if (fileInfo.Extension == ".dxf" ||
                         fileInfo.Extension == ".dnpoly" ||
                         DeepNestLib.IO.StepUnfoldService.IsStepFile(fileInfo.FullName))
                {
                  mainViewModel.LoadPart(fileInfo.FullName);
                }
                else
                {
                  var message = new StringBuilder();
                  message.AppendLine(string.Format(Strings.App_MsgUnableToLoadFormat, fileInfo.Name));
                  message.AppendLine(Strings.App_MsgFilesSupported);
                  message.AppendLine(Strings.App_FileTypeProject);
                  message.AppendLine(Strings.App_FileTypeResult);
                  message.AppendLine(Strings.App_FileTypeSheetPlacement);
                  message.AppendLine(Strings.App_FileTypeDxf);
                  message.AppendLine(Strings.App_FileTypePolygon);
                  message.AppendLine(Strings.App_FileType3D);
                  if (MessageBox.Show(message.ToString(), "DeepNest", MessageBoxButton.OKCancel) == System.Windows.MessageBoxResult.Cancel)
                  {
                    Application.Current.MainWindow.Close();
                  }
                }
              }
            }
          }
        }
        catch (Exception ex)
        {
          Console.WriteLine(ex.Message);
          throw;
        }
      }
    }
  }
}
