namespace DeepNestSharp.Domain.Models
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Threading.Tasks;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestLib.NestProject;
  using Microsoft.VisualStudio.Threading;

  public class ObservableDetailLoadInfo : ObservablePropertyObject, IWrapper<IDetailLoadInfo, DetailLoadInfo>, IDetailLoadInfo
  {
    private readonly DetailLoadInfo detailLoadInfo;
    private int? netArea;
    private INfp nfp;

    private static JoinableTaskContext joinableTaskContext = new JoinableTaskContext();

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservableDetailLoadInfo"/> class.
    /// </summary>
    /// <param name="sheetLoadInfo">The ProjectInfo to wrap.</param>
    public ObservableDetailLoadInfo(DetailLoadInfo detailLoadInfo)
    {
      this.detailLoadInfo = detailLoadInfo;
      this.PropertyChanged += this.ObservableDetailLoadInfo_PropertyChanged;
    }

    private void ObservableDetailLoadInfo_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
      if (e.PropertyName != nameof(IsDirty))
      {
        OnPropertyChanged(nameof(IsDirty));
      }
    }

    public IList<AnglesEnum> AnglesList => Enum.GetValues(typeof(AnglesEnum)).OfType<AnglesEnum>().ToList();

    public override bool IsDirty => this.detailLoadInfo.IsDirty;

    public bool IsValid => this.IsExists && !(this.Nfp is InvalidNoFitPolygon);

    public bool IsExists => this.detailLoadInfo.IsExists;

    public bool IsIncluded
    {
      get => detailLoadInfo.IsIncluded;
      set => SetProperty(nameof(IsIncluded), () => detailLoadInfo.IsIncluded, v => detailLoadInfo.IsIncluded = v, value);
    }

    public bool IsMultiplied
    {
      get => detailLoadInfo.IsMultiplied;
      set => SetProperty(nameof(IsMultiplied), () => detailLoadInfo.IsMultiplied, v => detailLoadInfo.IsMultiplied = v, value);
    }

    public bool IsPriority
    {
      get => detailLoadInfo.IsPriority;
      set => SetProperty(nameof(IsPriority), () => detailLoadInfo.IsPriority, v => detailLoadInfo.IsPriority = v, value);
    }

    public string Name
    {
      get => detailLoadInfo.Name;
    }

    public string Path
    {
      get => detailLoadInfo.Path;
      set => SetProperty(nameof(Path), () => detailLoadInfo.Path, v => detailLoadInfo.Path = v, value);
    }

    public int Quantity
    {
      get => detailLoadInfo.Quantity;
      set
      {
        SetProperty(nameof(Quantity), () => detailLoadInfo.Quantity, v => detailLoadInfo.Quantity = v, value);
        OnPropertyChanged(nameof(NestedInfo));
      }
    }

    public int Extra
    {
      get => detailLoadInfo.Extra;
      set
      {
        SetProperty(nameof(Extra), () => detailLoadInfo.Extra, v => detailLoadInfo.Extra = v, value);
        OnPropertyChanged(nameof(NestedInfo));
      }
    }

    private int nestedCount;

    /// <summary>
    /// Gets or sets how many copies of this part the on-screen nest placed (0 = no result).
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
    /// Gets the always-visible "Available" indicator: copies still left to nest / total requested
    /// (quantity + spares). No result: "30/30"; a nest that placed 26: "4/30".
    /// </summary>
    public string NestedInfo => $"{System.Math.Max(0, Quantity + Extra - nestedCount)}/{Quantity + Extra}";

    public int Rotations
    {
      get => detailLoadInfo.Rotations;
      set => SetProperty(nameof(Rotations), () => detailLoadInfo.Rotations, v => detailLoadInfo.Rotations = v, value);
    }

    public int Priority
    {
      get => detailLoadInfo.Priority;
      set => SetProperty(nameof(Priority), () => detailLoadInfo.Priority, v => detailLoadInfo.Priority = v, value);
    }

    public double Spacing
    {
      get => detailLoadInfo.Spacing;
      set => SetProperty(nameof(Spacing), () => detailLoadInfo.Spacing, v => detailLoadInfo.Spacing = v, value);
    }

    public bool CommonLine
    {
      get => detailLoadInfo.CommonLine;
      set => SetProperty(nameof(CommonLine), () => detailLoadInfo.CommonLine, v => detailLoadInfo.CommonLine = v, value);
    }

    /// <summary>Human-readable nesting summary for the part card (spacing / common line / rotation).</summary>
    public string NestingLabel
    {
      get
      {
        string spacing = detailLoadInfo.CommonLine ? "common line"
          : detailLoadInfo.Spacing < 0 ? "spacing: default"
          : $"spacing {detailLoadInfo.Spacing:0.###}";
        string rot = detailLoadInfo.Rotations switch
        {
          1 => "as drawn",
          2 => "0/180°",
          4 => "4-way",
          1001 => "90° only",   // Radan orientation codes (see RasterNestService.PermittedSet)
          1002 => "0°+90°",
          1003 => "90°+270°",
          > 4 => "free",
          _ => "rot: auto",
        };
        return $"{spacing} · {rot}";
      }
    }

    public int NetArea
    {
      get
      {
        if (netArea == null)
        {
          this.netArea = (int)this.Nfp.NetArea;
        }

        return netArea.Value;
      }
    }

    public AnglesEnum StrictAngle
    {
      get => detailLoadInfo.StrictAngle;
      set => SetProperty(nameof(StrictAngle), () => detailLoadInfo.StrictAngle, v => detailLoadInfo.StrictAngle = v, value);
    }

    public DetailLoadInfo Item => detailLoadInfo;

    internal INfp Nfp
    {
      get
      {
        _ = joinableTaskContext.Factory.RunAsync(async () => this.netArea = (int)(await this.LoadAsync().ConfigureAwait(false)).NetArea);
        return this.nfp;
      }
    }

    public async Task<INfp> LoadAsync()
    {
      try
      {
        if (this.nfp == null)
        {
          if (new FileInfo(detailLoadInfo.Path).Exists)
          {
            var raw = await DxfParser.LoadDxfFile(detailLoadInfo.Path);
            this.nfp = raw.ToNfp();
          }
          else
          {
            this.nfp = new NoFitPolygon();
          }
        }
      }
      catch
      {
        this.nfp = new InvalidNoFitPolygon();
      }

      return this.nfp;
    }
  }
}
