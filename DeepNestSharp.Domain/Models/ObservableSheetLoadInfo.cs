namespace DeepNestSharp.Domain.Models
{
  using DeepNestLib.NestProject;

  public class ObservableSheetLoadInfo : ObservablePropertyObject, IWrapper<ISheetLoadInfo, SheetLoadInfo>, ISheetLoadInfo
  {
    private readonly SheetLoadInfo sheetLoadInfo;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservableSheetLoadInfo"/> class.
    /// </summary>
    /// <param name="sheetLoadInfo">The ProjectInfo to wrap.</param>
    public ObservableSheetLoadInfo(SheetLoadInfo sheetLoadInfo) => this.sheetLoadInfo = sheetLoadInfo;

    public int Height
    {
      get => sheetLoadInfo.Height;
      set => SetProperty(nameof(Height), () => sheetLoadInfo.Height, v => sheetLoadInfo.Height = v, value);
    }

    public override bool IsDirty => true;

    public int Quantity
    {
      get => sheetLoadInfo.Quantity;
      set
      {
        SetProperty(nameof(Quantity), () => sheetLoadInfo.Quantity, v => sheetLoadInfo.Quantity = v, value);
        OnPropertyChanged(nameof(NestedInfo));
      }
    }

    public int Width
    {
      get => sheetLoadInfo.Width;
      set => SetProperty(nameof(Width), () => sheetLoadInfo.Width, v => sheetLoadInfo.Width = v, value);
    }

    private int nestedCount;

    /// <summary>
    /// Gets or sets how many sheets of this size the on-screen nest consumed (0 = no result).
    /// Display-only — never persisted.
    /// </summary>
    public int NestedCount
    {
      get => nestedCount;
      set
      {
        if (nestedCount != value)
        {
          nestedCount = value;
          OnPropertyChanged(nameof(NestedCount));
          OnPropertyChanged(nameof(NestedInfo));
        }
      }
    }

    /// <summary>
    /// Gets the always-visible "Available" indicator, SigmaNEST-style (Quantity Available):
    /// sheets of this size still available / total. The row's Quantity is already deducted while a
    /// result is on screen, so it IS the available count — no result: "31/31"; nest used 28: "3/31".
    /// </summary>
    public string NestedInfo => $"{Quantity}/{Quantity + nestedCount}";

    public SheetLoadInfo Item => this.sheetLoadInfo;
  }
}
