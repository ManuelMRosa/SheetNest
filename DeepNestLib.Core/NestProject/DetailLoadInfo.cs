namespace DeepNestLib.NestProject
{
  using System.IO;
  using System.Text.Json;
  using System.Text.Json.Serialization;
  using DeepNestLib.IO;

  public class DetailLoadInfo : Saveable, IDetailLoadInfo
  {
    [JsonIgnore]
    public string Name => new FileInfo(Path).Name;

    public string Path { get; set; }

    public int Quantity { get; set; } = 1;

    public int Extra { get; set; } = 0;

    public int Rotations { get; set; } = -1; // per-part: 1 fixed, 2 = 0/180, 4 = 90° steps, 36 free; -1 = unset (geometry-based suggestion in Edit Part)

    public int Priority { get; set; } = 5;   // 0-10, higher nests first; 5 = normal

    public double Spacing { get; set; } = -1; // per-part gap to neighbours (drawing units); -1 = job default

    public bool CommonLine { get; set; } = false; // nest copies TOUCHING (shared edges cut once)

    [JsonIgnore]
    public bool IsExists => new FileInfo(this.Path).Exists;

    public bool IsIncluded { get; set; } = true;

    public bool IsPriority { get; set; } = false;

    public bool IsMultiplied { get; set; } = true;

    public AnglesEnum StrictAngle { get; set; } = AnglesEnum.None;

    public override string ToJson(bool writeIndented = false)
    {
      var options = new JsonSerializerOptions();
      options.WriteIndented = writeIndented;
      return JsonSerializer.Serialize(this, options);
    }
  }
}
