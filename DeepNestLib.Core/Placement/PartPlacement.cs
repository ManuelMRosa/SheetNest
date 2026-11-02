namespace DeepNestLib.Placement
{
  using System;
  using System.Linq;
  using System.Text.Json.Serialization;

  public class PartPlacement : IPartPlacement
  {
    public PartPlacement(INfp part)
    {
      this.Part = part;
    }

    /// <inheritdoc />
    [JsonIgnore]
    public double? MergedLength
    {
      get
      {
        return null;
      }

      set
      {
        throw new NotImplementedException();
      }
    }

    /// <inheritdoc />
    [JsonIgnore]
    public object MergedSegments
    {
      get
      {
        return null;
      }

      set
      {
        throw new NotImplementedException();
      }
    }

    /// <inheritdoc />
    public int Id { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public bool IsExact => Part.IsExact;

    /// <inheritdoc />
    [JsonIgnore]
    public bool IsDragging { get; set; }

    /// <inheritdoc />
    //[JsonIgnore]
    //public INfp Hull { get; set; }

    /// <inheritdoc />
    //[JsonIgnore]
    //public INfp HullSheet { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public double MaxX => this.X + this.Part.Points.Max(p => p.X);

    /// <inheritdoc />
    [JsonIgnore]
    public double MaxY => this.Y + this.Part.Points.Max(p => p.Y);

    /// <inheritdoc />
    [JsonIgnore]
    public double MinX => this.X + this.Part.Points.Min(p => p.X);

    /// <inheritdoc />
    [JsonIgnore]
    public double MinY => this.Y + this.Part.Points.Min(p => p.Y);

    /// <inheritdoc />
    [JsonConverter(typeof(DoublePrecisionConverter))]
    public double Rotation { get; set; }

    /// <inheritdoc />
    /// <remarks>Omitted from JSON when false so pre-mirror snapshots/files stay byte-identical.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsMirrored { get; set; }

    /// <inheritdoc />
    [JsonConverter(typeof(DoublePrecisionConverter))]
    public double X { get; set; }

    /// <inheritdoc />
    [JsonConverter(typeof(DoublePrecisionConverter))]
    public double Y { get; set; }

    /// <inheritdoc />
    public int Source { get; set; }

    /// <inheritdoc />
    public INfp Part { get; }

    /// <inheritdoc />
    /// <remarks>
    /// The shifted copy is kept and handed back for as long as nothing it was built from has changed: the
    /// position (compared bit for bit) and the point arrays of the part and of every contour inside it. Points
    /// are replaced by installing a new array (ReplacePoints, AddPoint), so a changed part is a different
    /// array. Building the copy on every read was half of a common-line nest's time, because the planner and
    /// the clearance checks read the same part at the same place over and over. What comes back is exactly
    /// what <see cref="INfp.Shift(IPartPlacement)"/> builds, so nothing downstream sees a different number.
    /// </remarks>
    [JsonIgnore]
    public INfp PlacedPart
    {
      get
      {
        var snapshot = this.placed;
        if (snapshot != null && snapshot.Matches(this.X, this.Y, this.Part))
        {
          return snapshot.Placed;
        }

        var fresh = new PlacedSnapshot(this.X, this.Y, this.Part, this.Part.Shift(this));
        this.placed = fresh; // one reference: a reader on another thread sees the old snapshot or the new one
        return fresh.Placed;
      }
    }

    [JsonIgnore]
    private PlacedSnapshot placed;

    /// <summary>A shifted copy of the part and what it was built from. Never changed once made.</summary>
    private sealed class PlacedSnapshot
    {
      private readonly long xBits;
      private readonly long yBits;
      private readonly object[] arrays;

      public PlacedSnapshot(double x, double y, INfp part, INfp placed)
      {
        this.xBits = BitConverter.DoubleToInt64Bits(x);
        this.yBits = BitConverter.DoubleToInt64Bits(y);
        var arrays = new System.Collections.Generic.List<object>();
        Collect(part, arrays);
        this.arrays = arrays.ToArray();
        this.Placed = placed;
      }

      public INfp Placed { get; }

      public bool Matches(double x, double y, INfp part)
      {
        if (BitConverter.DoubleToInt64Bits(x) != this.xBits || BitConverter.DoubleToInt64Bits(y) != this.yBits)
        {
          return false;
        }

        int at = 0;
        return Same(part, ref at) && at == this.arrays.Length;
      }

      // The point array of every contour, in the order Shift walks them, then how many children each has.
      private static void Collect(INfp contour, System.Collections.Generic.List<object> arrays)
      {
        arrays.Add(contour.Points);
        int children = contour.Children?.Count ?? 0;
        arrays.Add(children);
        for (int i = 0; i < children; i++)
        {
          Collect(contour.Children[i], arrays);
        }
      }

      private bool Same(INfp contour, ref int at)
      {
        if (at + 2 > this.arrays.Length || !ReferenceEquals(this.arrays[at], contour.Points))
        {
          return false;
        }

        int children = contour.Children?.Count ?? 0;
        if (!(this.arrays[at + 1] is int count) || count != children)
        {
          return false;
        }

        at += 2;
        for (int i = 0; i < children; i++)
        {
          if (!Same(contour.Children[i], ref at))
          {
            return false;
          }
        }

        return true;
      }
    }
  }
}
