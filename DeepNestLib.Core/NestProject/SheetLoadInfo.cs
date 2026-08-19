namespace DeepNestLib.NestProject
{
  using System.Text.Json;
  using System.Text.Json.Serialization;
  using DeepNestLib;
  using DeepNestLib.IO;

  public class SheetLoadInfo : Saveable, ISheetLoadInfo
  {
    public SheetLoadInfo(ISvgNestConfig config)
      : this(config.SheetWidth, config.SheetHeight, config.SheetQuantity)
    {
    }

    [JsonConstructor]
    public SheetLoadInfo(int width, int height, int quantity)
    {
      this.Width = width;
      this.Height = height;
      this.Quantity = quantity;
    }

    public virtual int Width { get; set; }

    public virtual int Height { get; set; }

    public virtual int Quantity { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the job may take as many of this size as it needs.
    /// <para>Deliberately NOT a constructor parameter: the [JsonConstructor] above is the contract every
    /// .dnest written so far was serialized against, and adding an argument to it would fail to bind on
    /// files that do not carry the field. As a settable property an older file simply leaves it false,
    /// which is exactly how the app behaved before it existed.</para>
    /// </summary>
    public virtual bool Unlimited { get; set; }

    public override string ToJson(bool writeIndented = false)
    {
      var options = new JsonSerializerOptions();
      options.WriteIndented = writeIndented;
      return JsonSerializer.Serialize(this, options);
    }
  }
}