namespace DeepNestLib.CiTests.IO
{
  using System;
  using System.Diagnostics;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Security.Cryptography;
  using System.Text;
  using DeepNestLib.IO;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// Chaining a drawing's segments into contours used to compare every step with every segment left, so a
  /// perforated sheet (1393 holes, 88 000 segments) took four minutes to open, and again on every nest and
  /// every export, because both read the drawing afresh.
  /// </summary>
  public class DxfChainingSpeedFixture
  {
    [Fact]
    public void APerforatedSheetOpensInSeconds()
    {
      string path = Path.Combine(Path.GetTempPath(), $"sheetnest_perforated_{Guid.NewGuid():N}.dxf");
      try
      {
        var file = new DxfFile();
        file.Header.Version = DxfAcadVersion.R2000;
        file.Entities.Add(new DxfLwPolyline(new[]
        {
          new DxfLwPolylineVertex { X = 0, Y = 0 },
          new DxfLwPolylineVertex { X = 1000, Y = 0 },
          new DxfLwPolylineVertex { X = 1000, Y = 600 },
          new DxfLwPolylineVertex { X = 0, Y = 600 },
        }) { IsClosed = true });

        // 1500 round holes, each chained from 60+ segments: 100 000 segments in all.
        for (int i = 0; i < 50; i++)
        {
          for (int j = 0; j < 30; j++)
          {
            file.Entities.Add(new DxfCircle(new DxfPoint(15 + (i * 19.5), 15 + (j * 19.5), 0), 6));
          }
        }

        file.Save(path);

        var sw = Stopwatch.StartNew();
        var raw = new NestExecutionHelper().LoadRawDetail(new FileInfo(path));
        sw.Stop();

        raw.TryConvertToNfp(0, out INfp nfp).Should().BeTrue();
        nfp.Children.Should().HaveCount(1500);
        sw.Elapsed.TotalSeconds.Should().BeLessThan(20, "it took minutes when every step scanned every segment");
      }
      finally
      {
        File.Delete(path);
      }
    }

    /// <summary>The exporter joins a whole sheet's lines into closed polylines, and compared every end with every
    /// end on the sheet to do it.</summary>
    [Fact]
    public void ASheetOfLineDrawnPartsJoinsInSeconds()
    {
      var entities = new System.Collections.Generic.List<DxfEntity>();
      for (int i = 0; i < 100; i++)
      {
        for (int j = 0; j < 50; j++)
        {
          double x = i * 12, y = j * 12;
          entities.Add(new DxfLine(new DxfPoint(x, y, 0), new DxfPoint(x + 10, y, 0)));
          entities.Add(new DxfLine(new DxfPoint(x + 10, y, 0), new DxfPoint(x + 10, y + 10, 0)));
          entities.Add(new DxfLine(new DxfPoint(x + 10, y + 10, 0), new DxfPoint(x, y + 10, 0)));
          entities.Add(new DxfLine(new DxfPoint(x, y + 10, 0), new DxfPoint(x, y, 0)));
        }
      }

      var sw = Stopwatch.StartNew();
      FlatDxfJoiner.JoinEntities(entities).Should().BeTrue();
      sw.Stop();

      entities.Should().HaveCount(5000).And.AllBeOfType<DxfLwPolyline>();
      sw.Elapsed.TotalSeconds.Should().BeLessThan(10, "every end used to be compared with every other end on the sheet");
    }

    /// <summary>
    /// Opt-in (SHEETNEST_CHAIN_DUMP = output file, SHEETNEST_CHAIN_DIRS = ';'-separated folders): one line per
    /// drawing with a hash of the contours it chains into, so two builds can be compared drawing for drawing.
    /// </summary>
    [Fact]
    public void DumpChainedContours()
    {
      string output = Environment.GetEnvironmentVariable("SHEETNEST_CHAIN_DUMP");
      string dirs = Environment.GetEnvironmentVariable("SHEETNEST_CHAIN_DIRS");
      if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(dirs))
      {
        return;
      }

      var log = new StringBuilder();
      foreach (var file in dirs.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(d => Directory.EnumerateFiles(d, "*.dxf", SearchOption.AllDirectories))
        .Where(f => !long.TryParse(Environment.GetEnvironmentVariable("SHEETNEST_CHAIN_MAXKB"), out long kb) || new FileInfo(f).Length <= kb * 1024)
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
      {
        string line;
        var sw = Stopwatch.StartNew();
        try
        {
          var entities = DxfFile.Load(file).Entities.ToArray();
          var raw = DxfParser.ConvertDxfToRawDetail(file, entities);
          var text = new StringBuilder();
          foreach (var contour in raw.Outers.OfType<LocalContour<DxfEntity>>())
          {
            text.Append(contour.IsChild ? 'c' : 'o').Append(contour.Entities.Count).Append(':');
            foreach (var p in contour.Points)
            {
              text.Append(p.X.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(p.Y.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            }

            text.Append('|');
          }

          line = $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16]} {raw.Outers.Count()}";
        }
        catch (Exception ex)
        {
          line = "error " + ex.GetType().Name;
        }

        log.AppendLine(FormattableString.Invariant($"{file}\t{line}\t{sw.ElapsedMilliseconds}ms"));
      }

      File.WriteAllText(output, log.ToString());
    }
  }
}
