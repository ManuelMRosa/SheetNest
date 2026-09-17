namespace DeepNestLib.IO
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;

  /// <summary>
  /// What the drawing says a line is for.
  /// </summary>
  public enum DxfColorRole
  {
    /// <summary>Geometry to nest and cut. What anything not drawn in one of the other colours is, so a
    /// drawing that knows nothing of any of this behaves exactly as it always did.</summary>
    Cut = 0,

    /// <summary>A mark on the part: engraving, etching, a part number scribed into the material. It
    /// stays with the part and is never cut out of it.</summary>
    PartMark = 1,

    /// <summary>The text that says what a part is called and how many are wanted.</summary>
    Name = 2,
  }

  /// <summary>
  /// What each line of a drawing is for, read from the colour it is drawn in: yellow engraves, blue says
  /// what a part is called and how many, and everything else gets cut. It is how the machines these
  /// drawings are cut on already work, and it means the drawing says what it holds instead of SheetNest
  /// guessing from where a piece of text happens to sit.
  /// </summary>
  public static class DxfColorRoles
  {
    /// <summary>
    /// What the drawing says this entity is for. Anything whose colour cannot be read at all is cut,
    /// because losing geometry silently ends up as scrap metal while cutting a stray line does not.
    /// </summary>
    public static DxfColorRole RoleOf(DxfEntity entity, DxfLayerColors layers)
    {
      uint? colour = ColorOf(entity, layers);
      if (IsYellowish(colour))
      {
        return DxfColorRole.PartMark;
      }

      return IsBluish(colour) ? DxfColorRole.Name : DxfColorRole.Cut;
    }

    /// <summary>
    /// What there is to cut: everything except what is drawn in the marking colour. A blue line is cut
    /// like any other, because blue says a TEXT names the part and a line names nothing; only yellow
    /// changes what happens to geometry.
    /// </summary>
    public static IReadOnlyList<DxfEntity> Cuttable(IEnumerable<DxfEntity> entities, DxfLayerColors layers)
      => (entities ?? Enumerable.Empty<DxfEntity>())
        .Where(entity => RoleOf(entity, layers) != DxfColorRole.PartMark)
        .ToList();

    /// <summary>
    /// What there is to cut, refusing a drawing that is left with nothing. A drawing drawn entirely in
    /// the marking colour would otherwise be turned away further down as holding "only text, dimensions
    /// or other annotation", which is not true and gives the operator nothing to act on: only the code
    /// that dropped the geometry knows why it went.
    /// </summary>
    public static IReadOnlyList<DxfEntity> CuttableOrExplain(
      string name,
      IEnumerable<DxfEntity> entities,
      DxfLayerColors layers)
    {
      var all = (entities ?? Enumerable.Empty<DxfEntity>()).ToList();
      IReadOnlyList<DxfEntity> cuttable = Cuttable(all, layers);
      if (cuttable.Any(DxfParser.IsGeometry) || !all.Any(DxfParser.IsGeometry))
      {
        return cuttable;
      }

      throw new ArgumentException(
        $"'{name}' has nothing left to cut: everything drawn in it is in the marking colour, which is "
        + "engraved on the part rather than cut out of it.");
    }

    /// <summary>
    /// The colour an entity is actually drawn in: its own true colour, then its own colour index, and
    /// failing both the colour of the layer it is on, which is where a drawing from a CAD system usually
    /// keeps it. Null when there is nothing to read.
    /// </summary>
    /// <remarks>
    /// A true colour of zero is taken as "not set" rather than as black. DXF has no way to tell the two
    /// apart, and black is written as a colour index by everything that writes black on purpose.
    /// </remarks>
    public static uint? ColorOf(DxfEntity entity, DxfLayerColors layers)
    {
      if (entity == null)
      {
        return null;
      }

      if (entity.Color24Bit != 0)
      {
        return (uint)entity.Color24Bit;
      }

      uint? own = FromIndex(entity.Color);
      return own ?? (layers ?? DxfLayerColors.None).ColorOf(entity.Layer);
    }

    /// <summary>
    /// Whether a colour is one of the yellows. Asked of the colour itself rather than of a list of
    /// indexes, because yellow is a family: colour indexes 2, 50 and 52 are all yellow, and a drawing
    /// may carry a true colour that is in no table at all. Amber and chartreuse sit either side of it
    /// and are not it.
    /// </summary>
    public static bool IsYellowish(uint? color)
    {
      if (color == null)
      {
        return false;
      }

      (byte r, byte g, byte b) = DxfColorHelpers.ToRgb(color.Value);
      return r >= 150 && g >= 150 && Math.Abs(r - g) <= 40 && b <= Math.Min(r, g) / 2;
    }

    /// <summary>
    /// Whether a colour is one of the blues: blue clearly ahead of the other two, which is what tells it
    /// from cyan and from magenta, and pale blues in as well as deep ones.
    /// </summary>
    public static bool IsBluish(uint? color)
    {
      if (color == null)
      {
        return false;
      }

      (byte r, byte g, byte b) = DxfColorHelpers.ToRgb(color.Value);
      return b >= 100 && b - Math.Max(r, g) >= 40;
    }

    /// <summary>The colour an index stands for, or null when the entity said something else entirely:
    /// by layer, by block, or nothing.</summary>
    internal static uint? FromIndex(DxfColor color)
    {
      if (color == null || !color.IsIndex)
      {
        return null;
      }

      int index = Math.Abs(DxfColor.GetRawValue(color));
      return index >= 1 && index < DxfColor.DefaultColors.Count ? DxfColor.DefaultColors[index] : (uint?)null;
    }
  }

  /// <summary>
  /// The colours a drawing draws its layers in, so a line that says "whatever my layer is" can be
  /// answered. A shop usually sets the colour on the layer rather than on every line, which is why this
  /// has to be carried alongside the entities.
  /// </summary>
  public sealed class DxfLayerColors
  {
    /// <summary>No layer table to read, which is what a caller holding loose entities has. A line that
    /// follows its layer then has no colour, and no colour is cut.</summary>
    public static readonly DxfLayerColors None = new DxfLayerColors(new Dictionary<string, uint?>(StringComparer.OrdinalIgnoreCase));

    private readonly IReadOnlyDictionary<string, uint?> colors;

    private DxfLayerColors(IReadOnlyDictionary<string, uint?> colors)
    {
      this.colors = colors;
    }

    /// <summary>The colours of a drawing's layers.</summary>
    public static DxfLayerColors Of(DxfFile file)
    {
      if (file == null)
      {
        return None;
      }

      // By colour index only: a layer can carry a true colour in the file, but the library this reads
      // DXF with does not surface one for layers. Shops that colour their layers use the index, which is
      // what a yellow layer means on the machine anyway.
      var colors = new Dictionary<string, uint?>(StringComparer.OrdinalIgnoreCase);
      foreach (DxfLayer layer in file.Layers)
      {
        // Trimmed on the way in as well as on the way out: a layer called "ENGRAVE " with a space on
        // it has to answer to the marks that say they are on ENGRAVE.
        colors[string.IsNullOrWhiteSpace(layer.Name) ? "0" : layer.Name.Trim()] = DxfColorRoles.FromIndex(layer.Color);
      }

      return new DxfLayerColors(colors);
    }

    internal uint? ColorOf(string layerName)
    {
      string name = string.IsNullOrWhiteSpace(layerName) ? "0" : layerName.Trim();
      return this.colors.TryGetValue(name, out uint? color) ? color : null;
    }
  }
}
