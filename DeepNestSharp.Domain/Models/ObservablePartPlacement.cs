namespace DeepNestSharp.Domain.Models
{
  using System.Collections.Concurrent;
  using System.Threading.Tasks;
  using System.Windows.Input;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;
  using Microsoft.Toolkit.Mvvm.Input;

  public class ObservablePartPlacement : ObservablePropertyObject, IPartPlacement
  {
    // Un-rotated exact geometry reloaded from the original DXF, cached per file so a result with
    // many copies only reads each source once.
    private static readonly ConcurrentDictionary<string, INfp> ExactGeometryCache = new ConcurrentDictionary<string, INfp>();

    private readonly IPartPlacement partPlacement;
    private readonly IPointXY originalPosition;
    private readonly double originalRotation;
    private RelayCommand resetCommand;
    private IAsyncRelayCommand loadExactCommand;

    public ObservablePartPlacement(IPartPlacement partPlacement, int order)
    {
      this.partPlacement = partPlacement;
      this.Order = order;
      this.originalPosition = new SvgPoint(partPlacement.X, partPlacement.Y);
      this.originalRotation = partPlacement.Rotation;
      this.PropertyChanged += this.ObservablePartPlacement_PropertyChanged;
    }

    /// <inheritdoc/>
    public bool IsDragging
    {
      get => partPlacement.IsDragging;
      set => SetProperty(nameof(IsDragging), () => partPlacement.IsDragging, v => partPlacement.IsDragging = v, value);
    }

    /// <inheritdoc/>
    public int Source
    {
      get => partPlacement.Source;
      set => SetProperty(nameof(Source), () => partPlacement.Source, v => partPlacement.Source = v, value);
    }

    /// <inheritdoc/>
    public int Id
    {
      get => partPlacement.Id;
      set => SetProperty(nameof(Id), () => partPlacement.Id, v => partPlacement.Id = v, value);
    }

    public ICommand ResetCommand => resetCommand ?? (resetCommand = new RelayCommand(OnReset, () => IsDirty));

    public IAsyncRelayCommand LoadExactCommand
    {
      get
      {
        if (loadExactCommand == null)
        {
          loadExactCommand = new AsyncRelayCommand(OnLoadExact, () => !IsExact);
        }

        return loadExactCommand;
      }
    }

    /// <inheritdoc/>
    public double X
    {
      get => partPlacement.X;
      set => SetProperty(nameof(X), () => partPlacement.X, v => partPlacement.X = v, value);
    }

    /// <inheritdoc/>
    public double Y
    {
      get => partPlacement.Y;
      set => SetProperty(nameof(Y), () => partPlacement.Y, v => partPlacement.Y = v, value);
    }

    ///// <inheritdoc/>
    //public INfp Hull
    //{
    //  get => partPlacement.Hull;
    //  set => SetProperty(nameof(Hull), () => partPlacement.Hull, v => partPlacement.Hull = v, value);
    //}

    ///// <inheritdoc/>
    //public INfp HullSheet
    //{
    //  get => partPlacement.HullSheet;
    //  set => SetProperty(nameof(HullSheet), () => partPlacement.HullSheet, v => partPlacement.HullSheet = v, value);
    //}

    /// <inheritdoc/>
    public override bool IsDirty
    {
      get
      {
        return this.originalPosition.X != this.partPlacement.X ||
               this.originalPosition.Y != this.partPlacement.Y ||
               this.originalRotation != this.partPlacement.Rotation;
      }
    }

    /// <inheritdoc/>
    public double MaxX => this.partPlacement.MaxX;

    /// <inheritdoc/>
    public double MaxY => this.partPlacement.MaxY;

    /// <inheritdoc/>
    public double? MergedLength => partPlacement.MergedLength;

    /// <inheritdoc/>
    public object MergedSegments
    {
      get => partPlacement.MergedSegments;
      set => SetProperty(nameof(MergedSegments), () => partPlacement.MergedSegments, v => partPlacement.MergedSegments = v, value);
    }

    /// <inheritdoc/>
    public double MinX => this.partPlacement.MinX;

    /// <inheritdoc/>
    public double MinY => this.partPlacement.MinY;

    /// <inheritdoc/>
    public INfp Part => partPlacement.Part;

    /// <inheritdoc/>
    public INfp PlacedPart => partPlacement.PlacedPart;

    /// <inheritdoc/>
    public double Rotation
    {
      get => partPlacement.Rotation;
      set => partPlacement.Rotation = value;
    }

    /// <inheritdoc/>
    public bool IsExact => Part.IsExact;

    public int Order { get; private set; }

    /// <inheritdoc/>
    private void ObservablePartPlacement_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
      if (e.PropertyName == nameof(IsDirty))
      {
        resetCommand?.NotifyCanExecuteChanged();
      }
    }

    /// <inheritdoc/>
    private void OnReset()
    {
      this.X = originalPosition.X;
      this.Y = originalPosition.Y;
      this.Rotation = originalRotation;
    }

    /// <inheritdoc/>
    public async Task OnLoadExact()
    {
      var raw = await DxfParser.LoadDxfFile(this.Part.Name);
      INfp loadedNfp;
      if (raw.TryConvertToNfp(this.Part.Source, out loadedNfp))
      {
        loadedNfp = loadedNfp.Rotate(this.Part.Rotation);
        this.Part.ReplacePoints(loadedNfp);
        OnPropertyChanged(nameof(IsExact));
        loadExactCommand?.NotifyCanExecuteChanged();
      }
    }

    /// <summary>
    /// Replaces this placement's simplified/inflated nesting geometry with the exact original from
    /// the source DXF (rotated to the placement), so the preview renders true part outlines instead
    /// of the coarse polygons used by the nesting math. No-op if already exact or the file is gone.
    /// </summary>
    internal void TryLoadExactForDisplay()
    {
      try
      {
        var name = this.Part?.Name;
        if (this.IsExact || string.IsNullOrEmpty(name) || !name.ToLower().Contains(".dxf"))
        {
          return;
        }

        if (!ExactGeometryCache.TryGetValue(name, out INfp baseNfp))
        {
          var raw = DxfParser.LoadDxfFile(name).GetAwaiter().GetResult();
          if (raw == null || !raw.TryConvertToNfp(this.Part.Source, out baseNfp))
          {
            return;
          }

          ExactGeometryCache[name] = baseNfp;
        }

        this.Part.ReplacePoints(baseNfp.Rotate(this.Part.Rotation));
        OnPropertyChanged(nameof(IsExact));
      }
      catch
      {
        // Keep the simplified geometry if the original can't be reloaded.
      }
    }
  }
}
