namespace DeepNestSharp.Ui.Services
{
  using System.IO;
  using System.Linq;
  using System.Threading.Tasks;
  using DeepNestLib;
  using DeepNestSharp.Domain.Services;
  using Microsoft.Win32;

  public class FileIoService : IFileIoService
  {
    private readonly IDispatcherService dispatcherService;

    public FileIoService(IDispatcherService dispatcherService)
    {
      this.dispatcherService = dispatcherService;
    }

    public bool Exists(string filePath)
    {
      var fileInfo = new FileInfo(filePath);
      return fileInfo.Exists;
    }

    public async Task<string> GetOpenFilePathAsync(string filter, string initialDirectory)
    {
      var filePaths = await GetOpenFilePathsAsync(filter, initialDirectory, false).ConfigureAwait(false);
      return filePaths.First();
    }

    public async Task<string[]> GetOpenFilePathsAsync(string filter, string initialDirectory, bool allowMultiSelect = true)
    {
      try
      {
        OpenFileDialog openFileDialog = new OpenFileDialog()
        {
          Filter = filter,
          Multiselect = allowMultiSelect,
        };

        openFileDialog.InitialDirectory = UsableFolder(initialDirectory);

        bool dialogResponse = false;
        await dispatcherService.InvokeAsync(() => dialogResponse = Show(openFileDialog)).ConfigureAwait(false);
        if (dialogResponse)
        {
          return openFileDialog.FileNames;
        }

        return new string[] { string.Empty };
      }
      catch (System.Exception ex)
      {
        throw;
      }
    }

    public string GetSaveFilePath(string fileDialogFilter, string? fileName = null, string? initialDirectory = null)
    {
      var dlg = new SaveFileDialog()
      {
        Filter = fileDialogFilter,
      };

      if (!string.IsNullOrWhiteSpace(fileName))
      {
        dlg.FileName = fileName;
      }

      dlg.InitialDirectory = UsableFolder(initialDirectory);
      return Show(dlg) ? dlg.FileName : string.Empty;
    }

    /// <summary>
    /// The folder to open a dialog in, or none. A project saved to a flash drive remembers that drive, and
    /// once the drive is pulled the shell refuses the dialog outright ("the system cannot find the drive
    /// specified"); at the laser that crashed the program while it asked whether to save on closing.
    /// </summary>
    internal static string UsableFolder(string? folder)
    {
      try
      {
        return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? folder : string.Empty;
      }
      catch (System.Exception)
      {
        return string.Empty;
      }
    }

    /// <summary>Shows the dialog; if the shell still refuses where it was told to open (a drive that went
    /// between the check and the call, a network folder that no longer answers), shows it again without.</summary>
    private static bool Show(FileDialog dialog)
    {
      try
      {
        return dialog.ShowDialog() == true;
      }
      catch (System.ComponentModel.Win32Exception)
      {
        dialog.InitialDirectory = string.Empty;
        string name = Path.GetFileName(dialog.FileName ?? string.Empty);
        dialog.FileName = name;
        return dialog.ShowDialog() == true;
      }
    }
  }
}