namespace DeepNestLib.IO
{
  using System.Collections.Generic;
  using System.IO;
  using System.Text;
  using IxMilia.Dxf;

  /// <summary>
  /// Saves a sheet as text without the column block the library puts on every MTEXT.
  /// </summary>
  /// <remarks>
  /// A block of text says which way it reads in its angle (50) and in its direction vector (11/21/31),
  /// and a CAD that finds both goes by the last one it reads. The library always ends an MTEXT with a
  /// column block whose own code 50 is the column count, zero, and an R2000 file has no columns, so a
  /// CAD reads that zero as the angle and lays every mark flat. The library offers no way to leave the
  /// block out, so it is taken out of what the library wrote.
  /// </remarks>
  public static class DxfR2000Writer
  {
    private static readonly HashSet<int> ColumnCodes = new HashSet<int> { 75, 76, 78, 79, 48, 49, 50 };

    public static void Save(DxfFile dxf, string path)
    {
      using (var file = File.Create(path))
      {
        Save(dxf, file);
      }
    }

    public static void Save(DxfFile dxf, Stream stream)
    {
      byte[] written;
      using (var buffer = new MemoryStream())
      {
        dxf.Save(buffer, true);
        written = buffer.ToArray();
      }

      // Latin-1 maps every byte to one character and back, so nothing the library encoded is changed.
      Encoding bytes = Encoding.GetEncoding(28591);
      byte[] cleaned = bytes.GetBytes(WithoutMTextColumns(bytes.GetString(written)));
      stream.Write(cleaned, 0, cleaned.Length);
    }

    private static string WithoutMTextColumns(string text)
    {
      string newLine = text.Contains("\r\n") ? "\r\n" : "\n";
      string[] lines = text.Split(new[] { newLine }, System.StringSplitOptions.None);
      var kept = new StringBuilder(text.Length);
      bool inMText = false;
      bool inColumns = false;
      int i = 0;
      for (; i + 1 < lines.Length; i += 2)
      {
        if (!int.TryParse(lines[i].Trim(), out int code))
        {
          break;
        }

        if (code == 0)
        {
          inMText = lines[i + 1].Trim() == "MTEXT";
        }

        inColumns = inMText && (code == 75 || (inColumns && ColumnCodes.Contains(code)));
        if (inColumns)
        {
          continue;
        }

        kept.Append(lines[i]).Append(newLine).Append(lines[i + 1]).Append(newLine);
      }

      // Whatever is left after the last pair (the empty piece after the final line break) goes back as is.
      for (; i < lines.Length; i++)
      {
        kept.Append(lines[i]);
        if (i < lines.Length - 1)
        {
          kept.Append(newLine);
        }
      }

      return kept.ToString();
    }
  }
}
