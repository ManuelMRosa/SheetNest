namespace DeepNestLib.IO
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Threading.Tasks;
  using DeepNestLib.Geometry;
  using DeepNestLib.Placement;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;

  public class DxfExporter : ExporterBase, IExport
  {
    public override string SaveFileDialogFilter => "Dxf files (*.dxf)|*.dxf";

    public async Task Export(Stream stream, ISheetPlacement sheetPlacement, bool doMergeLines, bool differentiateChildren)
    {
      await Export(stream, sheetPlacement.PolygonsForExport, sheetPlacement.Sheet, doMergeLines, differentiateChildren);
    }

    public async Task Export(Stream stream, IEnumerable<INfp> polygons, ISheet sheet, bool doMergeLines, bool differentiateChildren)
    {
      DxfFile sheetdxf = GenerateDxfFile(polygons, sheet, sheet.Id, doMergeLines, differentiateChildren);
      var dxf = sheetdxf;
      var id = sheet.Id;

      // (Guard was `!= 1` when the sheet outline was always entity #0 — now any entity means content.)
      if (dxf.Entities.Count > 0)
      {
        await Task.Run(() =>
        {
          dxf.Save(stream, true);
          //dxf.Save(@"C:\Temp\DeepnestSharp.dxf");
        }).ConfigureAwait(false);
      }
    }

    protected async override Task Export(string path, IEnumerable<INfp> polygons, IEnumerable<ISheet> sheets, bool doMergeLines, bool differentiateChildren, IReadOnlyCollection<OffcutLine> offcutLines = null)
    {
      try
      {
        Dictionary<DxfFile, int> dxfexports = new Dictionary<DxfFile, int>();
        var sheetList = sheets.ToList();
        for (var i = 0; i < sheets.Count(); i++)
        {
          var sheet = sheetList[i];
          DxfFile sheetdxf = GenerateDxfFile(polygons, sheet, i, doMergeLines, differentiateChildren, offcutLines);
          dxfexports.Add(sheetdxf, sheet.Id);
        }

        for (var i = 0; i < dxfexports.Count(); i++)
        {
          var dxf = dxfexports.ElementAt(i).Key;
          var id = dxfexports.ElementAt(i).Value;

          if (dxf.Entities.Count > 0)
          {
            FileInfo fi = new FileInfo(path);
            await Task.Run(() =>
            {
              if (dxfexports.Count() == 1)
              {
                dxf.Save(fi.FullName, true);
              }
              else
              {
                dxf.Save($"{fi.FullName.Substring(0, fi.FullName.Length - 4)}{id}.dxf", true);
              }
            }).ConfigureAwait(false);
          }
        }
      }
      catch (Exception ex)
      {
        throw;
      }
    }

    private static DxfFile GenerateEmptyDxfFile()
    {
      // PARTS ONLY — no sheet outline: the plate rectangle used to be written as a first entity, but
      // downstream CAM treats every entity as geometry to cut, and reimporting such a file looks like
      // one giant nested-sheet part. The sheet size lives in the CAM/job setup, not in the cut file.
      var sheetdxf = new DxfFile();

      // Write DXF R2000 (AC1015) instead of the default R12: R12 symbol/table names can't contain
      // spaces, so a layer like "Plate H130 W70" makes AutoCAD reject the whole file. R2000 allows
      // spaces in names and preserves LWPOLYLINE/arcs more faithfully. Universally supported.
      sheetdxf.Header.Version = DxfAcadVersion.R2000;
      sheetdxf.Views.Clear();

      return sheetdxf;
    }

    private static IEnumerable<DxfEntity> GetOffsetDxfEntities(IEnumerable<INfp> polygons, ISheet sheet, int i, bool differentiateChildren)
    {
      foreach (var polygon in polygons)
      {
        RawDetail<DxfEntity> fl;
        if (polygon.Fitted == false || !polygon.Name.ToLower().Contains(".dxf") || polygon.Sheet.Id != sheet.Id)
        {
          continue;
        }
        else
        {
          try
          {
#if NCRUNCH
            if (polygon.IsExact && !(new FileInfo(polygon.Name).Exists))
            {
              fl = DxfParser.ConvertDxfToRawDetail("generated.dxf", polygon.ToDxfFile().Entities.ToArray());
            }
            else
#endif
            {
              fl = DxfParser.ConvertDxfToRawDetail(polygon.Name, DxfFile.Load(polygon.Name).Entities.ToArray());
            }
          }
          catch
          {
            throw new FileNotFoundException($"The file {polygon.Name} could not be loaded. When exporting the original files that generated the nest are used for precision, instead of the copies rotated and manipulated potentially many times during the nest; degrading potentially their accuracy. It would be possible to load and store the original files but that'd take some effort...");
          }
        }

        var sheetXoffset = -sheet.WidthCalculated * i;
        //double sheetYoffset = -sheet.Height * i;
        DxfPoint offsetdistance = new DxfPoint(polygon.X + sheetXoffset, polygon.Y, 0D);
        List<DxfEntity> newlist = OffsetToNest(fl.Outers, offsetdistance, polygon.Rotation, polygon.IsMirrored, differentiateChildren);
        foreach (DxfEntity ent in newlist)
        {
          yield return ent;
        }
      }
    }

    private static List<DxfEntity> OffsetToNest(IEnumerable<ILocalContour> contours, DxfPoint offsetdistance, double rotation, bool mirror, bool differentiateChildren)
    {
      var allEntities = new List<DxfEntity>();
      foreach (var contour in contours)
      {
        if (contour is LocalContour<DxfEntity> castContour)
        {
          if (differentiateChildren && castContour.IsChild)
          {
            foreach (var child in castContour.Entities)
            {
              child.Color = DxfColorHelpers.GetClosestDefaultIndexColor(255, 0, 0);
            }
          }

          allEntities.AddRange(castContour.Entities);
        }
      }

      return OffsetToNest(allEntities, offsetdistance, rotation, mirror);
    }

    private static List<DxfEntity> OffsetToNest(IList<DxfEntity> dxfEntities, DxfPoint offset, double rotationAngle, bool mirror)
    {
      var result = new List<DxfEntity>();
      List<DxfPoint> tmpPts;
      foreach (DxfEntity entity in dxfEntities)
      {
        switch (entity.EntityType)
        {
          case DxfEntityType.Arc:
            DxfArc dxfArc = (DxfArc)entity;
            dxfArc.Center = TransformLocation(mirror, rotationAngle, dxfArc.Center);
            dxfArc.Center += offset;
            if (mirror)
            {
              // Reflection across Y reverses the arc's CCW sweep: swap the endpoints and map θ -> 180-θ.
              double s = dxfArc.StartAngle, en = dxfArc.EndAngle;
              dxfArc.StartAngle = 180.0 - en + rotationAngle;
              dxfArc.EndAngle = 180.0 - s + rotationAngle;
            }
            else
            {
              dxfArc.StartAngle += rotationAngle;
              dxfArc.EndAngle += rotationAngle;
            }

            result.Add(dxfArc);
            break;

          case DxfEntityType.ArcAlignedText:
            DxfArcAlignedText dxfArcAligned = (DxfArcAlignedText)entity;
            dxfArcAligned.CenterPoint = TransformLocation(mirror, rotationAngle, dxfArcAligned.CenterPoint);
            dxfArcAligned.CenterPoint += offset;
            if (mirror)
            {
              double s = dxfArcAligned.StartAngle, en = dxfArcAligned.EndAngle;
              dxfArcAligned.StartAngle = 180.0 - en + rotationAngle;
              dxfArcAligned.EndAngle = 180.0 - s + rotationAngle;
            }
            else
            {
              dxfArcAligned.StartAngle += rotationAngle;
              dxfArcAligned.EndAngle += rotationAngle;
            }

            result.Add(dxfArcAligned);
            break;

          case DxfEntityType.Attribute:
            DxfAttribute dxfAttribute = (DxfAttribute)entity;
            dxfAttribute.Location = TransformLocation(mirror, rotationAngle, dxfAttribute.Location);
            dxfAttribute.Location += offset;
            result.Add(dxfAttribute);
            break;

          case DxfEntityType.AttributeDefinition:
            DxfAttributeDefinition dxfAttributecommon = (DxfAttributeDefinition)entity;
            dxfAttributecommon.Location = TransformLocation(mirror, rotationAngle, dxfAttributecommon.Location);
            dxfAttributecommon.Location += offset;
            result.Add(dxfAttributecommon);
            break;

          case DxfEntityType.Circle:
            DxfCircle dxfCircle = (DxfCircle)entity;
            dxfCircle.Center = TransformLocation(mirror, rotationAngle, dxfCircle.Center);
            dxfCircle.Center += offset;
            result.Add(dxfCircle);
            break;

          case DxfEntityType.Ellipse:
            DxfEllipse dxfEllipse = (DxfEllipse)entity;
            dxfEllipse.Center = TransformLocation(mirror, rotationAngle, dxfEllipse.Center);
            dxfEllipse.Center += offset;
            if (mirror)
            {
              var ma = dxfEllipse.MajorAxis;
              dxfEllipse.MajorAxis = new DxfVector(-ma.X, ma.Y, ma.Z); // reflect the major-axis direction
            }

            result.Add(dxfEllipse);
            break;

          case DxfEntityType.Image:
            DxfImage dxfImage = (DxfImage)entity;
            dxfImage.Location = TransformLocation(mirror, rotationAngle, dxfImage.Location);
            dxfImage.Location += offset;

            result.Add(dxfImage);
            break;

          case DxfEntityType.Leader:
            DxfLeader dxfLeader = (DxfLeader)entity;
            tmpPts = new List<DxfPoint>();

            foreach (DxfPoint vrt in dxfLeader.Vertices)
            {
              var tmppnt = TransformLocation(mirror, rotationAngle, vrt);
              tmppnt += offset;
              tmpPts.Add(tmppnt);
            }

            dxfLeader.Vertices.Clear();
            dxfLeader.Vertices.Concat(tmpPts);
            result.Add(dxfLeader);
            break;

          case DxfEntityType.Line:
            DxfLine dxfLine = (DxfLine)entity;
            dxfLine.P1 = TransformLocation(mirror, rotationAngle, dxfLine.P1);
            dxfLine.P2 = TransformLocation(mirror, rotationAngle, dxfLine.P2);
            dxfLine.P1 += offset;
            dxfLine.P2 += offset;
            result.Add(dxfLine);
            break;

          case DxfEntityType.LwPolyline:
            // A LwPolyline's entity type is DxfLwPolyline (NOT DxfPolyline); its vertices are
            // DxfLwPolylineVertex (X/Y/Bulge), not DxfVertex. The old (DxfPolyline) cast always threw.
            DxfLwPolyline dxfLwPoly = (DxfLwPolyline)entity;
            foreach (DxfLwPolylineVertex vrt in dxfLwPoly.Vertices)
            {
              var rotated = TransformLocation(mirror, rotationAngle, new DxfPoint(vrt.X, vrt.Y, 0));
              rotated += offset;
              vrt.X = rotated.X;
              vrt.Y = rotated.Y;
              if (mirror)
              {
                vrt.Bulge = -vrt.Bulge; // reflection flips the sign of the arc bulge
              }
            }

            result.Add(dxfLwPoly);
            break;

          case DxfEntityType.MLine:
            DxfMLine mLine = (DxfMLine)entity;
            tmpPts = new List<DxfPoint>();
            mLine.StartPoint += offset;

            mLine.StartPoint = TransformLocation(mirror, rotationAngle, mLine.StartPoint);

            foreach (DxfPoint vrt in mLine.Vertices)
            {
              var tmppnt = TransformLocation(mirror, rotationAngle, vrt);
              tmppnt += offset;
              tmpPts.Add(tmppnt);
            }

            mLine.Vertices.Clear();
            mLine.Vertices.Concat(tmpPts);
            result.Add(mLine);
            break;

          case DxfEntityType.Polyline:
            DxfPolyline polyline = (DxfPolyline)entity;

            List<DxfVertex> verts = new List<DxfVertex>();
            foreach (DxfVertex vrt in polyline.Vertices)
            {
              var tmppnt = vrt;
              tmppnt.Location = TransformLocation(mirror, rotationAngle, tmppnt.Location);
              tmppnt.Location += offset;
              if (mirror)
              {
                tmppnt.Bulge = -tmppnt.Bulge;
              }

              verts.Add(tmppnt);
            }

            DxfPolyline polyout = new DxfPolyline(verts);
            polyout.Location = polyline.Location + offset;
            polyout.IsClosed = polyline.IsClosed;
            polyout.Layer = polyline.Layer;
            result.Add(polyout);

            break;

          case DxfEntityType.Spline:
            {
              // An affine map of a NURBS is a NURBS with mapped control points, so the knots and weights
              // ride along untouched. Mirroring reflects the control points; the curve follows.
              DxfSpline dxfSpline = (DxfSpline)entity;
              Replace(dxfSpline.ControlPoints, dxfSpline.ControlPoints
                .Select(cp => new DxfControlPoint(TransformLocation(mirror, rotationAngle, cp.Point) + offset, cp.Weight)).ToList());
              Replace(dxfSpline.FitPoints, dxfSpline.FitPoints
                .Select(p => TransformLocation(mirror, rotationAngle, p) + offset).ToList());
              result.Add(dxfSpline);
              break;
            }

          case DxfEntityType.Body:
          case DxfEntityType.DgnUnderlay:
          case DxfEntityType.Dimension:
          case DxfEntityType.DwfUnderlay:
          case DxfEntityType.Face:
          case DxfEntityType.Helix:
          case DxfEntityType.Insert:
          case DxfEntityType.Light:
          case DxfEntityType.ModelerGeometry:
          case DxfEntityType.MText:
          case DxfEntityType.OleFrame:
          case DxfEntityType.Ole2Frame:
          case DxfEntityType.PdfUnderlay:
          case DxfEntityType.Point:
          case DxfEntityType.ProxyEntity:
          case DxfEntityType.Ray:
          case DxfEntityType.Region:
          case DxfEntityType.RText:
          case DxfEntityType.Section:
          case DxfEntityType.Seqend:
          case DxfEntityType.Shape:
          case DxfEntityType.Solid:
          case DxfEntityType.Text:
          case DxfEntityType.Tolerance:
          case DxfEntityType.Trace:
          case DxfEntityType.Underlay:
          case DxfEntityType.Vertex:
          case DxfEntityType.WipeOut:
          case DxfEntityType.XLine:
            throw new ArgumentException("unsupported entity type: " + entity.EntityType);
        }
      }

      return result;
    }

    private static DxfPoint RotateLocation(double rotationAngle, DxfPoint pt)
    {
      var angle = GeometryUtil.ToRadians(rotationAngle);
      var x = pt.X;
      var y = pt.Y;
      var x1 = (double)(x * Math.Cos(angle) - y * Math.Sin(angle));
      var y1 = (double)(x * Math.Sin(angle) + y * Math.Cos(angle));
      return new DxfPoint(x1, y1, pt.Z);
    }

    /// <summary>Applies the placement's transform to a point in the original file's frame: mirror across Y
    /// (negate X) first when the placement is mirrored, THEN rotate — matching how the on-screen/nested
    /// geometry was built (mirror-then-rotate), so the exported part lands exactly where it's shown.</summary>
    private static DxfPoint TransformLocation(bool mirror, double rotationAngle, DxfPoint pt)
    {
      if (mirror)
      {
        pt = new DxfPoint(-pt.X, pt.Y, pt.Z);
      }

      return RotateLocation(rotationAngle, pt);
    }

    /// <summary>Swaps the contents of a list in place; the entities own their collections.</summary>
    private static void Replace<T>(IList<T> target, IList<T> replacement)
    {
      target.Clear();
      foreach (T item in replacement)
      {
        target.Add(item);
      }
    }

    /// <summary>Rounds a coordinate to the export grid (micro-inch).</summary>
    internal static double Snap(double v) => Math.Round(v, 6);

    /// <summary>
    /// Snaps every entity's coordinates to the micro-inch grid (in place). Positions only — bulges
    /// are dimensionless ratios and stay untouched. Unknown entity types pass through unchanged.
    /// </summary>
    internal static IEnumerable<DxfEntity> SnapToGrid(IEnumerable<DxfEntity> entities)
    {
      foreach (var entity in entities)
      {
        switch (entity)
        {
          case DxfLine line:
            line.P1 = new DxfPoint(Snap(line.P1.X), Snap(line.P1.Y), Snap(line.P1.Z));
            line.P2 = new DxfPoint(Snap(line.P2.X), Snap(line.P2.Y), Snap(line.P2.Z));
            break;
          case DxfArc arc:
            arc.Center = new DxfPoint(Snap(arc.Center.X), Snap(arc.Center.Y), Snap(arc.Center.Z));
            arc.Radius = Snap(arc.Radius);
            arc.StartAngle = Snap(arc.StartAngle);
            arc.EndAngle = Snap(arc.EndAngle);
            break;
          case DxfCircle circle:
            circle.Center = new DxfPoint(Snap(circle.Center.X), Snap(circle.Center.Y), Snap(circle.Center.Z));
            circle.Radius = Snap(circle.Radius);
            break;
          case DxfLwPolyline lw:
            foreach (var v in lw.Vertices)
            {
              v.X = Snap(v.X);
              v.Y = Snap(v.Y);
            }

            break;
          case DxfPolyline poly:
            foreach (var v in poly.Vertices)
            {
              v.Location = new DxfPoint(Snap(v.Location.X), Snap(v.Location.Y), Snap(v.Location.Z));
            }

            break;
        }

        yield return entity;
      }
    }

    private DxfFile GenerateDxfFile(IEnumerable<INfp> polygons, ISheet sheet, int i, bool doMergeLines, bool differentiateChildren, IReadOnlyCollection<OffcutLine> offcutLines = null)
    {
      try
      {
        DxfFile sheetdxf = GenerateEmptyDxfFile();
        IEnumerable<DxfEntity> entities = GetOffsetDxfEntities(polygons.Where(o => o.Sheet.Id == sheet.Id), sheet, i, differentiateChildren).ToList();

        // Snap every coordinate to the micro-inch BEFORE merging: the per-part double transforms
        // leave coincident geometry (a common-line shared edge) ~1e-8 apart — irrelevant to the cut
        // but visible when measured in CAD, and noise the merger's tolerance has to absorb. On the
        // 1e-6 grid, coincident geometry is numerically IDENTICAL.
        entities = SnapToGrid(entities).ToList();

        if (doMergeLines)
        {
          entities = new DxfLineMerger().MergeLines(entities);
          //Have seen oddness where splitting out verticals and non verticals prevented an errant output; but then it just stopped happening!
          //var mergeLines = entities.Where(o => o is DxfLine).Cast<DxfLine>().Select(o => new MergeLine(o));
          //var over = mergeLines.Where(o => o.IsVertical && Math.Max(o.Left.Y, o.Right.Y) > sheet.MaxY);
          //var under = mergeLines.Where(o => o.IsVertical && Math.Min(o.Left.Y, o.Right.Y) < sheet.MinY);
          //if (over.Any() || under.Any())
          //{
          //  System.Diagnostics.Debugger.Break();
          //}

          //entities = mergeLines.Where(o => !o.IsVertical).Select(o => o.Line as DxfEntity).ToList();
          //entities.AddRange(mergeLines.Where(o => o.IsVertical).Select(o => o.Line));
        }

        // AFTER the merge (order matters): chain the exploded LINE/ARC soup into closed LWPOLYLINEs
        // so the laser receives real closed contours. Common-line sheets keep their merged shared
        // edges — a shared edge belongs to two contours, so those chains hit the joiner's
        // ambiguous-junction guard and stay as loose (already merged) lines, which is the correct
        // cut geometry. A join failure must never break the export.
        var entityList = entities as List<DxfEntity> ?? entities.ToList();
        try
        {
          FlatDxfJoiner.JoinEntities(entityList);
        }
        catch
        {
          // keep the unjoined entities
        }

        foreach (var entity in entityList)
        {
          sheetdxf.Entities.Add(entity);
        }

        // The offcut separation cut(s) (rectangular-offcut nests): ordinary cut geometry, added
        // AFTER the merge/join so they can never be fused into a part contour.
        if (offcutLines != null)
        {
          foreach (var offcutLine in offcutLines)
          {
            sheetdxf.Entities.Add(new DxfLine(
              new DxfPoint(offcutLine.X1, offcutLine.Y1, 0),
              new DxfPoint(offcutLine.X2, offcutLine.Y2, 0)));
          }
        }

        return sheetdxf;
      }
      catch (Exception ex)
      {
        throw;
      }
    }
  }
}