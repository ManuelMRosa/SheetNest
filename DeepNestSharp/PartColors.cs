namespace DeepNestSharp
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib.Placement;

  /// <summary>
  /// One colour per part type, shared by the nest viewer and the PDF report so the screen and the paper can
  /// never disagree about which part is which.
  /// <para>The key is the part's FILE NAME plus whether it is mirrored - not <c>IPartPlacement.Source</c>,
  /// which counts populations rather than types (a row with a mirrored quantity produces two of them, and
  /// excluded rows shift the rest). It is the same key the report already groups its part totals by, so a
  /// left-hand and a right-hand part get their own colour - which is what the operator needs, since the two
  /// are nearly indistinguishable on screen.</para>
  /// </summary>
  internal static class PartColors
  {
    /// <summary>
    /// Full colours, spread around the wheel, the way the trade's nesting software does it - the point is to
    /// spot a part across the sheet, and a muted palette on a near-white sheet just reads as dirty grey.
    /// <para>Two constraints shape them. They stay clear of every colour that already MEANS something in the
    /// viewer - navy selection (#000080), red invalid/lead-in/kerf (#D32F2F / #C62828), green offcut
    /// (#2E7D32) - so a part is never mistaken for a state. And none is dark: the near-black outline
    /// (#101010) is what separates two touching parts OF THE SAME TYPE, which is the normal case on a
    /// sheet, and on a dark fill that outline disappears and they merge into one blob.</para>
    /// </summary>
    private static readonly (byte R, byte G, byte B)[] Palette =
    {
      (0x1E, 0x88, 0xE5), // blue
      (0x00, 0xAC, 0xC1), // cyan
      (0x7C, 0xB3, 0x42), // green
      (0xFD, 0xD8, 0x35), // yellow
      (0xFB, 0x8C, 0x00), // orange
      (0xE9, 0x1E, 0x8C), // pink
      (0x8E, 0x24, 0xAA), // purple
      (0x5E, 0x35, 0xB1), // violet
    };

    /// <summary>The classic aluminium fill. Only a fallback now, for a placement no map knows about - every
    /// part that IS in the job gets a colour, including a job with a single part type.</summary>
    public static (byte R, byte G, byte B) Default => (0xB4, 0xB8, 0xBC); // aluminum gray

    /// <summary>How many distinct colours exist before the palette repeats.</summary>
    public static int PaletteLength => Palette.Length;

    public static (byte R, byte G, byte B) PaletteAt(int index) => Palette[((index % Palette.Length) + Palette.Length) % Palette.Length];

    /// <summary>
    /// What the report calls a placement: a mirrored copy is its own line item, because a left-hand and a
    /// right-hand part are different physical parts to count. This is TEXT ONLY - the colour comes from
    /// <see cref="ColourKeyFor(IPartPlacement)"/>, which does not split them.
    /// </summary>
    public static string LabelFor(IPartPlacement placement)
    {
      if (placement == null)
      {
        return "(part)";
      }

      return ColourKeyFor(placement) + (placement.IsMirrored ? " (mirrored)" : string.Empty);
    }

    /// <summary>
    /// What decides the colour: the part's file, mirrored or not. The parts list is the legend - one row,
    /// one thumbnail - so a colour on the sheet with no thumbnail to explain it would defeat the point.
    /// </summary>
    public static string ColourKeyFor(IPartPlacement placement) => ColourKeyFor(placement?.Part?.Name);

    public static string ColourKeyFor(string path) => DisplayName(path);

    /// <summary>
    /// Assigns a colour to every part file in the job, following the ORDER OF THE PART LIST: part 1 takes
    /// the first colour, part 2 the second, and so on round the palette. Feed it the whole part list of the
    /// project - not the parts of one sheet, or a part missing from that sheet would shift every other
    /// colour on it, and the thumbnail in the parts list would agree with neither.
    /// </summary>
    public static IReadOnlyDictionary<string, (byte R, byte G, byte B)> Build(IEnumerable<string> partPaths)
      => Build(partPaths?.Select(p => (p, -1)));

    /// <summary>
    /// As above, but each part may carry the colour its owner chose (0xRRGGBB, or -1 for none).
    /// <para>A chosen colour does NOT consume a palette slot: part 3 keeps the third colour whether or not
    /// part 2 was overridden, so recolouring one part never moves anyone else's.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, (byte R, byte G, byte B)> Build(IEnumerable<(string Path, int ChosenRgb)> parts)
    {
      var map = new Dictionary<string, (byte R, byte G, byte B)>(StringComparer.OrdinalIgnoreCase);
      int position = 0;
      foreach (var (path, chosen) in parts ?? Enumerable.Empty<(string, int)>())
      {
        if (string.IsNullOrWhiteSpace(path))
        {
          continue;
        }

        string key = ColourKeyFor(path);
        if (map.ContainsKey(key))
        {
          continue; // the same file listed twice is one part, and it keeps the place of its first row
        }

        map[key] = chosen >= 0 ? FromRgb(chosen) : PaletteAt(position);
        position++;
      }

      return map;
    }

    /// <summary>Unpacks a stored 0xRRGGBB.</summary>
    public static (byte R, byte G, byte B) FromRgb(int rgb)
      => ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

    /// <summary>Packs a colour for storing in the project.</summary>
    public static int ToRgb((byte R, byte G, byte B) colour) => (colour.R << 16) | (colour.G << 8) | colour.B;

    /// <summary>The colour for one placement. A job with a single part type is coloured too - seeing grey
    /// would just look broken - so the classic fill is only for a part the map has never heard of.</summary>
    public static (byte R, byte G, byte B) For(IReadOnlyDictionary<string, (byte R, byte G, byte B)> map, IPartPlacement placement)
      => For(map, placement?.Part?.Name);

    /// <summary>The colour for a part file, for callers that have a path rather than a placement (the parts
    /// list and its thumbnails).</summary>
    public static (byte R, byte G, byte B) For(IReadOnlyDictionary<string, (byte R, byte G, byte B)> map, string path)
    {
      if (map == null || string.IsNullOrWhiteSpace(path))
      {
        return Default;
      }

      return map.TryGetValue(ColourKeyFor(path), out var colour) ? colour : Default;
    }

    private static string DisplayName(string path)
    {
      try
      {
        return string.IsNullOrWhiteSpace(path) ? "(part)" : Path.GetFileName(path);
      }
      catch (ArgumentException)
      {
        return path ?? "(part)";
      }
    }
  }
}
