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

    public int MirrorQuantity
    {
      get => detailLoadInfo.MirrorQuantity;
      set
      {
        SetProperty(nameof(MirrorQuantity), () => detailLoadInfo.MirrorQuantity, v => detailLoadInfo.MirrorQuantity = v, value);
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
    /// (quantity + spares + mirrored). No result: "30/30"; a nest that placed 26: "4/30".
    /// </summary>
    public string NestedInfo => $"{System.Math.Max(0, Quantity + Extra + MirrorQuantity - nestedCount)}/{Quantity + Extra + MirrorQuantity}";

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
          1001 => "90° only",   // orientation permission codes (see RotationCodes.PermittedSet)
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

    /// <summary>Flat part size "W × H" in the drawing's units (inches for 3D imports); empty until loaded.</summary>
    public string SizeLabel
    {
      get
      {
        var n = this.nfp;
        if (n == null || n.Length < 2)
        {
          return string.Empty;
        }

        return $"{n.WidthCalculated:0.00} × {n.HeightCalculated:0.00}";
      }
    }

    public AnglesEnum StrictAngle
    {
      get => detailLoadInfo.StrictAngle;
      set => SetProperty(nameof(StrictAngle), () => detailLoadInfo.StrictAngle, v => detailLoadInfo.StrictAngle = v, value);
    }

    public string SourceStepPath
    {
      get => detailLoadInfo.SourceStepPath;
      set => SetProperty(nameof(SourceStepPath), () => detailLoadInfo.SourceStepPath, v => detailLoadInfo.SourceStepPath = v, value);
    }

    public int UnfoldIndex
    {
      get => detailLoadInfo.UnfoldIndex;
      set => SetProperty(nameof(UnfoldIndex), () => detailLoadInfo.UnfoldIndex, v => detailLoadInfo.UnfoldIndex = v, value);
    }

    public double KFactor
    {
      get => detailLoadInfo.KFactor;
      set => SetProperty(nameof(KFactor), () => detailLoadInfo.KFactor, v => detailLoadInfo.KFactor = v, value);
    }

    public string KFactorStandard
    {
      get => detailLoadInfo.KFactorStandard;
      set => SetProperty(nameof(KFactorStandard), () => detailLoadInfo.KFactorStandard, v => detailLoadInfo.KFactorStandard = v, value);
    }

    public bool UnfoldUnitInch
    {
      get => detailLoadInfo.UnfoldUnitInch;
      set => SetProperty(nameof(UnfoldUnitInch), () => detailLoadInfo.UnfoldUnitInch, v => detailLoadInfo.UnfoldUnitInch = v, value);
    }

    public double ThicknessMm
    {
      get => detailLoadInfo.ThicknessMm;
      set => SetProperty(nameof(ThicknessMm), () => detailLoadInfo.ThicknessMm, v => detailLoadInfo.ThicknessMm = v, value);
    }

    public string NestSourcePath
    {
      get => detailLoadInfo.NestSourcePath;
      set => SetProperty(nameof(NestSourcePath), () => detailLoadInfo.NestSourcePath, v => detailLoadInfo.NestSourcePath = v, value);
    }

    public string NestPartName
    {
      get => detailLoadInfo.NestPartName;
      set => SetProperty(nameof(NestPartName), () => detailLoadInfo.NestPartName, v => detailLoadInfo.NestPartName = v, value);
    }

    public bool NestUnitInch
    {
      get => detailLoadInfo.NestUnitInch;
      set => SetProperty(nameof(NestUnitInch), () => detailLoadInfo.NestUnitInch, v => detailLoadInfo.NestUnitInch = v, value);
    }

    /// <summary>Gets whether this part was 3D-unfolded from a STEP/IGES (has an editable K-factor).</summary>
    public bool Is3D => !string.IsNullOrEmpty(detailLoadInfo.SourceStepPath);

    public DetailLoadInfo Item => detailLoadInfo;

    /// <summary>The thumbnail binds here so it refreshes once the (possibly slow, e.g. 3D unfold) geometry loads.</summary>
    public IDetailLoadInfo PreviewSource => this;

    internal INfp Nfp
    {
      get
      {
        _ = joinableTaskContext.Factory.RunAsync(async () =>
        {
          this.netArea = (int)(await this.LoadAsync().ConfigureAwait(false)).NetArea;
          await joinableTaskContext.Factory.SwitchToMainThreadAsync();
          this.OnPropertyChanged(nameof(this.PreviewSource));
          this.OnPropertyChanged(nameof(this.SizeLabel));
          this.OnPropertyChanged(nameof(this.NetArea));
        });
        return this.nfp;
      }
    }

    /// <summary>Drops the cached geometry so it reloads from the current <see cref="Path"/> (used after a
    /// 3D part is re-unfolded with a new K-factor) and refreshes the thumbnail / size / area.</summary>
    public void InvalidateGeometry()
    {
      this.nfp = null;
      this.netArea = null;
      OnPropertyChanged(nameof(this.PreviewSource));
      OnPropertyChanged(nameof(this.SizeLabel));
      OnPropertyChanged(nameof(this.NetArea));
      OnPropertyChanged(nameof(this.IsValid));
      _ = this.Nfp; // kick the async reload; it re-raises PreviewSource/SizeLabel/NetArea when done
    }

    public async Task<INfp> LoadAsync()
    {
      try
      {
        if (this.nfp == null)
        {
          // A 3D-imported part whose cached flat DXF is gone (temp reclaimed, or the project was saved
          // and reopened): rebuild the flat from the original STEP using this part's own K-factor/unit.
          if (!new FileInfo(detailLoadInfo.Path).Exists
              && !string.IsNullOrEmpty(detailLoadInfo.SourceStepPath)
              && new FileInfo(detailLoadInfo.SourceStepPath).Exists)
          {
            var rebuilt = await Task.Run(() => StepUnfoldService.GetUnfoldedParts(
              detailLoadInfo.SourceStepPath, detailLoadInfo.KFactor, detailLoadInfo.KFactorStandard, detailLoadInfo.UnfoldUnitInch));
            if (rebuilt.Paths.Count > 0)
            {
              int idx = (detailLoadInfo.UnfoldIndex >= 0 && detailLoadInfo.UnfoldIndex < rebuilt.Paths.Count) ? detailLoadInfo.UnfoldIndex : 0;
              detailLoadInfo.Path = rebuilt.Paths[idx];
            }
          }

          // Same story for a part imported from a SheetCam .nest: its DXF is a temp materialisation, so
          // rebuild it from the nest file when it has been reclaimed.
          if (!new FileInfo(detailLoadInfo.Path).Exists
              && !string.IsNullOrEmpty(detailLoadInfo.NestSourcePath)
              && new FileInfo(detailLoadInfo.NestSourcePath).Exists)
          {
            await Task.Run(() => SheetCamNestPartWriter.TryRebuild(
              detailLoadInfo.NestSourcePath,
              detailLoadInfo.NestPartName,
              detailLoadInfo.NestUnitInch ? 1d / 25.4d : 1d,
              detailLoadInfo.Path));
          }

          if (new FileInfo(detailLoadInfo.Path).Exists)
          {
            IRawDetail raw;
            if (StepUnfoldService.IsStepFile(detailLoadInfo.Path))
            {
              raw = await Task.Run(() => StepUnfoldService.LoadAsRawDetail(detailLoadInfo.Path));
            }
            else
            {
              raw = await DxfParser.LoadDxfFile(detailLoadInfo.Path);
            }

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
