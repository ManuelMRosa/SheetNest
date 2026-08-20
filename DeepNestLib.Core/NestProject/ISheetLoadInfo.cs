namespace DeepNestLib.NestProject
{
  public interface ISheetLoadInfo
  {
    int Height { get; set; }

    int Quantity { get; set; }

    /// <summary>Gets or sets a value indicating whether there is as much of this size as the job needs.
    /// <see cref="Quantity"/> keeps whatever it held and is ignored while this is set.</summary>
    bool Unlimited { get; set; }

    int Width { get; set; }
  }
}