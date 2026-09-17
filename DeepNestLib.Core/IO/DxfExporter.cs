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
    public const string CutInnerLayer = "01_CUT_INNER";
    public const string CommonLineLayer = "02_COMMON_LINE";
    public const string CutOuterLayer = "03_CUT_OUTER";
    public const string OffcutLayer = "04_OFFCUT";

    public override string SaveFileDialogFilter => "Dxf files (*.dxf)|*.dxf";

    public override async Task Export(
      string path,
      ISheetPlacement sheetPlacement,
      bool doMergeLines,
      bool differentiateChildren,
      IReadOnlyCollection<OffcutLine> offcutLines = null)
    {
      if (sheetPlacement.CommonEdges == null || sheetPlacement.CommonEdges.Count == 0)
      {
        await base.Export(path, sheetPlacement, doMergeLines, differentiateChildren, offcutLines).ConfigureAwait(false);
        return;
      }

      DxfFile dxf = GenerateCommonLineDxfFile(sheetPlacement, differentiateChildren, offcutLines);
      if (dxf.Entities.Count == 0)
      {
        return;
      }

      await Task.Run(() => dxf.Save(path, true)).ConfigureAwait(false);
    }

    public async Task Export(Stream stream, ISheetPlacement sheetPlacement, bool doMergeLines, bool differentiateChildren)
    {
      if (sheetPlacement.CommonEdges != null && sheetPlacement.CommonEdges.Count > 0)
      {
        DxfFile dxf = GenerateCommonLineDxfFile(sheetPlacement, differentiateChildren);
        if (dxf.Entities.Count > 0)
        {
          await Task.Run(() => dxf.Save(stream, true)).ConfigureAwait(false);
        }

        return;
      }

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

    /// <summary>
    /// What a part carries engraved on it, moved onto the sheet with the part. Marks are not contours,
    /// so they come from the part's file rather than from the geometry measured out of it, and they are
    /// written on the layer the operator drew them on: a shop that colours its layers leaves the mark
    /// saying "whatever my layer is", and the layer has to go with it or the colour that makes it a mark
    /// is lost.
    /// </summary>
    private static List<DxfEntity> PlacedMarks(
      DxfFile into,
      string source,
      DxfPoint offset,
      double rotation,
      bool mirror)
    {
      IReadOnlyList<DxfEntity> marks = DxfPartMarks.Fresh(source);
      if (marks.Count == 0)
      {
        return new List<DxfEntity>();
      }

      foreach (DxfLayer layer in DxfPartMarks.LayersFor(source))
      {
        AddLayer(into, layer.Name, layer.Color);
      }

      return OffsetToNest(marks.ToList(), offset, rotation, mirror);
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
              DxfFile source = DxfFile.Load(polygon.Name);
              fl = DxfParser.ConvertDxfToRawDetail(
                polygon.Name,
                DxfColorRoles.Cuttable(source.Entities, DxfLayerColors.Of(source)));
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
            DxfVector majorAxis = dxfEllipse.MajorAxis;
            DxfPoint transformedMajorAxis = TransformLocation(
              mirror,
              rotationAngle,
              new DxfPoint(majorAxis.X, majorAxis.Y, majorAxis.Z));
            dxfEllipse.MajorAxis = new DxfVector(
              transformedMajorAxis.X,
              transformedMajorAxis.Y,
              transformedMajorAxis.Z);

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

          case DxfEntityType.Text:
            {
              DxfText dxfText = (DxfText)entity;
              (double textAngle, bool flipText) = DxfPartMarks.TextPlacement(mirror, rotationAngle, dxfText.Rotation);
              dxfText.Rotation = textAngle;
              if (IsStretched(dxfText))
              {
                // Aligned and fit text is stretched BETWEEN its two points, so the second one is the far
                // end of the run and not somewhere it could be moved to.
                dxfText.Location = TransformLocation(mirror, rotationAngle, dxfText.Location) + offset;
                dxfText.SecondAlignmentPoint =
                  TransformLocation(mirror, rotationAngle, dxfText.SecondAlignmentPoint) + offset;
              }
              else
              {
                // A TEXT carries two points and which one a reader honours depends on its justification:
                // left off the baseline it is the first, anything else the second. Turning a part over
                // changes the justification, so both points go to where the mark actually is; moving only
                // the one that reads well in a test leaves the other one pointing at the sheet origin,
                // and that is the one the machine would follow.
                DxfPoint placed = TransformLocation(mirror, rotationAngle, Anchor(dxfText)) + offset;
                dxfText.Location = placed;
                dxfText.SecondAlignmentPoint = placed;
              }

              if (flipText)
              {
                dxfText.HorizontalTextJustification = Flipped(dxfText.HorizontalTextJustification);
              }

              result.Add(dxfText);
              break;
            }

          case DxfEntityType.MText:
            {
              DxfMText dxfMText = (DxfMText)entity;
              dxfMText.InsertionPoint = TransformLocation(mirror, rotationAngle, dxfMText.InsertionPoint) + offset;
              (double blockAngle, bool flipBlock) = DxfPartMarks.TextPlacement(mirror, rotationAngle, dxfMText.RotationAngle);
              dxfMText.RotationAngle = blockAngle;
              if (flipBlock)
              {
                dxfMText.AttachmentPoint = Flipped(dxfMText.AttachmentPoint);
              }

              result.Add(dxfMText);
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

    /// <summary>The point a reader actually hangs this text from: the first one only when it is left
    /// justified off the baseline, the second one in every other case.</summary>
    private static DxfPoint Anchor(DxfText text)
      => text.HorizontalTextJustification == DxfHorizontalTextJustification.Left
        && text.VerticalTextJustification == DxfVerticalTextJustification.Baseline
        ? text.Location
        : text.SecondAlignmentPoint;

    /// <summary>Whether this text is stretched between its two points rather than hung from one.</summary>
    private static bool IsStretched(DxfText text)
      => text.HorizontalTextJustification == DxfHorizontalTextJustification.Aligned
        || text.HorizontalTextJustification == DxfHorizontalTextJustification.Fit;

    /// <summary>Left and right swap over when text is turned over, so the words still grow away from the
    /// point they are anchored at instead of back across the part.</summary>
    private static DxfHorizontalTextJustification Flipped(DxfHorizontalTextJustification justification)
    {
      switch (justification)
      {
        case DxfHorizontalTextJustification.Left: return DxfHorizontalTextJustification.Right;
        case DxfHorizontalTextJustification.Right: return DxfHorizontalTextJustification.Left;
        default: return justification;
      }
    }

    /// <summary>The same swap for the nine points a block of text can hang from.</summary>
    private static DxfAttachmentPoint Flipped(DxfAttachmentPoint attachment)
    {
      switch (attachment)
      {
        case DxfAttachmentPoint.TopLeft: return DxfAttachmentPoint.TopRight;
        case DxfAttachmentPoint.TopRight: return DxfAttachmentPoint.TopLeft;
        case DxfAttachmentPoint.MiddleLeft: return DxfAttachmentPoint.MiddleRight;
        case DxfAttachmentPoint.MiddleRight: return DxfAttachmentPoint.MiddleLeft;
        case DxfAttachmentPoint.BottomLeft: return DxfAttachmentPoint.BottomRight;
        case DxfAttachmentPoint.BottomRight: return DxfAttachmentPoint.BottomLeft;
        default: return attachment;
      }
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

    private static DxfFile GenerateCommonLineDxfFile(
      ISheetPlacement sheetPlacement,
      bool differentiateChildren,
      IReadOnlyCollection<OffcutLine> offcutLines = null)
    {
      IReadOnlyDictionary<string, DxfSourceSnapshot> sourceSnapshots
        = CaptureSourceSnapshots(
          sheetPlacement.PartPlacements,
          sheetPlacement.GeometryTolerance);
      CommonLineGeometryResult geometry = CommonLineGeometryResolver.Resolve(
        sheetPlacement,
        sourceSnapshots.ToDictionary(
          pair => pair.Key,
          pair => pair.Value.Catalog,
          StringComparer.OrdinalIgnoreCase));
      var clusteredPartIds = new HashSet<int>(geometry.Clusters.SelectMany(cluster => cluster.PartIds));
      DxfFile dxf = GenerateEmptyDxfFile();
      AddCommonLineLayers(dxf);

      // Preserve original DXF primitives for ordinary parts and all holes. Only the outside contour of
      // a cluster member is suppressed; it is replaced by the one closed cluster boundary below.
      foreach (IPartPlacement placement in sheetPlacement.PartPlacements)
      {
        bool clustered = clusteredPartIds.Contains(placement.Id);
        foreach ((bool IsChild, List<DxfEntity> Entities) contour in LoadPlacedContours(
          placement,
          sourceSnapshots))
        {
          if (clustered && !contour.IsChild)
          {
            EnsureClusterOuterSourceIsSupported(contour.Entities, placement.Id);
            continue;
          }

          string layer = contour.IsChild ? CutInnerLayer : CutOuterLayer;
          List<DxfEntity> closed = EnsureClosedContour(
            contour.Entities,
            layer,
            sheetPlacement.GeometryTolerance);
          if (differentiateChildren && contour.IsChild)
          {
            foreach (DxfEntity entity in closed)
            {
              entity.Color = DxfColorHelpers.GetClosestDefaultIndexColor(255, 0, 0);
            }
          }

          foreach (DxfEntity entity in closed)
          {
            dxf.Entities.Add(entity);
          }
        }

        foreach (DxfEntity mark in PlacedMarks(
          dxf,
          placement.Part.Name,
          new DxfPoint(placement.X, placement.Y, 0D),
          placement.Rotation,
          placement.IsMirrored))
        {
          dxf.Entities.Add(mark);
        }
      }

      foreach (CommonLineCluster cluster in geometry.Clusters)
      {
        foreach (CommonLinePath boundary in cluster.InnerBoundaries)
        {
          if (!boundary.IsClosed || boundary.Points.Count < 3)
          {
            throw new CommonLineValidationException("CUT_INNER contains an open or degenerate cluster contour.");
          }

          var polyline = new DxfLwPolyline(boundary.Points.Select((point, index) => new DxfLwPolylineVertex
          {
            X = Snap(point.X),
            Y = Snap(point.Y),
            Bulge = boundary.Bulges[index],
          }))
          {
            IsClosed = true,
            Layer = CutInnerLayer,
          };
          if (differentiateChildren)
          {
            polyline.Color = DxfColorHelpers.GetClosestDefaultIndexColor(255, 0, 0);
          }

          dxf.Entities.Add(polyline);
        }

        foreach (CommonLinePath boundary in cluster.OuterBoundaries)
        {
          if (!boundary.IsClosed || boundary.Points.Count < 3)
          {
            throw new CommonLineValidationException("CUT_OUTER contains an open or degenerate cluster contour.");
          }

          var polyline = new DxfLwPolyline(boundary.Points.Select((point, index) => new DxfLwPolylineVertex
          {
            X = Snap(point.X),
            Y = Snap(point.Y),
            Bulge = boundary.Bulges[index],
          }))
          {
            IsClosed = true,
            Layer = CutOuterLayer,
          };
          dxf.Entities.Add(polyline);
        }

        foreach (CommonCenterCurve common in cluster.CommonCurves)
        {
          if (common.GeometryKind == CommonLineGeometryKind.Line)
          {
            double dx = common.End.X - common.Start.X;
            double dy = common.End.Y - common.Start.Y;
            if (Math.Sqrt((dx * dx) + (dy * dy)) <= sheetPlacement.GeometryTolerance)
            {
              throw new CommonLineValidationException("COMMON_LINE contains a zero-length or tiny segment.");
            }

            dxf.Entities.Add(new DxfLine(
              new DxfPoint(Snap(common.Start.X), Snap(common.Start.Y), 0D),
              new DxfPoint(Snap(common.End.X), Snap(common.End.Y), 0D))
            {
              Layer = CommonLineLayer,
            });
            continue;
          }

          if (common.GeometryKind != CommonLineGeometryKind.CircularArc
            || common.Center == null
            || common.Radius <= sheetPlacement.GeometryTolerance
            || Math.Abs(common.SweepRadians) * common.Radius <= sheetPlacement.GeometryTolerance)
          {
            throw new CommonLineValidationException("COMMON_LINE contains an invalid or unsupported exact curve.");
          }

          double sweep = common.SweepRadians;
          double angularTolerance = sheetPlacement.GeometryTolerance / common.Radius;
          if (Math.Abs(sweep) >= (2D * Math.PI) - angularTolerance)
          {
            dxf.Entities.Add(new DxfCircle(
              new DxfPoint(Snap(common.Center.X), Snap(common.Center.Y), 0D),
              Snap(common.Radius))
            {
              Layer = CommonLineLayer,
            });
            continue;
          }

          // A DXF ARC stores an implicit endpoint (center/radius/angle).  Independently rounding an
          // adjoining LINE therefore re-opens an otherwise canonical T/+ node after a save/load.
          // A two-vertex bulged LWPOLYLINE is the same exact circular primitive but stores both
          // endpoints explicitly, so every incident entity retains byte-identical node coordinates.
          dxf.Entities.Add(new DxfLwPolyline(new[]
          {
            new DxfLwPolylineVertex
            {
              X = Snap(common.Start.X),
              Y = Snap(common.Start.Y),
              Bulge = Math.Tan(sweep / 4D),
            },
            new DxfLwPolylineVertex
            {
              X = Snap(common.End.X),
              Y = Snap(common.End.Y),
            },
          })
          {
            IsClosed = false,
            Layer = CommonLineLayer,
          });
        }
      }

      if (offcutLines != null)
      {
        foreach (OffcutLine offcut in offcutLines)
        {
          dxf.Entities.Add(new DxfLine(
            new DxfPoint(offcut.X1, offcut.Y1, 0D),
            new DxfPoint(offcut.X2, offcut.Y2, 0D))
          {
            Layer = OffcutLayer,
          });
        }
      }

      EnsureSourceSnapshotsAreCurrent(sourceSnapshots, sheetPlacement.GeometryTolerance);
      ValidateCommonLineDxf(dxf, sheetPlacement.GeometryTolerance);

      return dxf;
    }

    /// <summary>
    /// The resolver can preserve LINE and circular geometry from polylines, ARC/CIRCLE entities, or an
    /// exploded LINE+ARC contour. Keep this independent export-side guard so a future regression cannot
    /// suppress ELLIPSE/SPLINE/unknown geometry and silently replace it with chords.
    /// </summary>
    private static void EnsureClusterOuterSourceIsSupported(
      IReadOnlyCollection<DxfEntity> entities,
      int partId)
    {
      bool supported = entities != null
        && entities.Count > 0
        && entities.All(entity => entity is DxfLine
          || entity is DxfArc
          || entity is DxfCircle
          || entity is DxfLwPolyline
          || entity is DxfPolyline);
      if (!supported)
      {
        throw new CommonLineValidationException(
          $"Part {partId} has outside source geometry the common-line cluster cannot preserve exactly.");
      }
    }

    private static IReadOnlyDictionary<string, DxfSourceSnapshot> CaptureSourceSnapshots(
      IReadOnlyList<IPartPlacement> placements,
      double endpointTolerance)
    {
      var snapshots = new Dictionary<string, DxfSourceSnapshot>(StringComparer.OrdinalIgnoreCase);
      foreach (IPartPlacement placement in placements)
      {
        string source = placement?.Part?.Name;
#if NCRUNCH
        if (placement?.Part?.IsExact == true && !new FileInfo(source).Exists)
        {
          continue;
        }
#endif
        if (string.IsNullOrWhiteSpace(source)
          || !source.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
        {
          throw new FileNotFoundException(
            $"The file {source} could not be loaded for validated common-line export.");
        }

        string path = Path.GetFullPath(source);
        if (!snapshots.ContainsKey(path))
        {
          try
          {
            snapshots.Add(path, DxfContourCatalog.ReadSnapshot(path, endpointTolerance));
          }
          catch (Exception ex)
          {
            throw new FileNotFoundException(
              $"The file {source} could not be loaded for validated common-line export.",
              ex);
          }
        }
      }

      return snapshots;
    }

    private static void EnsureSourceSnapshotsAreCurrent(
      IReadOnlyDictionary<string, DxfSourceSnapshot> snapshots,
      double endpointTolerance)
    {
      foreach (DxfSourceSnapshot snapshot in snapshots.Values)
      {
        try
        {
          DxfContourCatalog current = DxfContourCatalog.Load(
            snapshot.Path,
            endpointTolerance);
          if (!string.Equals(
            current.SourceFingerprint,
            snapshot.Catalog.SourceFingerprint,
            StringComparison.Ordinal))
          {
            throw new CommonLineValidationException(
              $"Source DXF '{snapshot.Path}' changed during common-line export; export was cancelled.");
          }
        }
        catch (CommonLineValidationException)
        {
          throw;
        }
        catch (Exception)
        {
          throw new CommonLineValidationException(
            $"Source DXF '{snapshot.Path}' changed or became unavailable during common-line export.");
        }
      }
    }

    private static IEnumerable<(bool IsChild, List<DxfEntity> Entities)> LoadPlacedContours(
      IPartPlacement placement,
      IReadOnlyDictionary<string, DxfSourceSnapshot> sourceSnapshots)
    {
      RawDetail<DxfEntity> detail;
      string source = placement.Part.Name;
      try
      {
#if NCRUNCH
        if (placement.Part.IsExact && !new FileInfo(source).Exists)
        {
          detail = DxfParser.ConvertDxfToRawDetail("generated.dxf", placement.Part.ToDxfFile().Entities.ToArray());
        }
        else
#endif
        {
          if (string.IsNullOrWhiteSpace(source) || !source.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
          {
            throw new FileNotFoundException();
          }

          string path = Path.GetFullPath(source);
          if (!sourceSnapshots.TryGetValue(path, out DxfSourceSnapshot snapshot))
          {
            throw new FileNotFoundException();
          }

          DxfFile opened = snapshot.OpenFile();
          detail = DxfParser.ConvertDxfToRawDetail(
            source,
            DxfColorRoles.Cuttable(opened.Entities, DxfLayerColors.Of(opened)));
        }
      }
      catch
      {
        throw new FileNotFoundException($"The file {source} could not be loaded for validated common-line export.");
      }

      // SheetPlacement coordinates are local to their sheet. Sheet.Id is identity metadata, not a
      // horizontal page index; applying Width * Id here shifts source contours away from the resolved
      // cluster geometry whenever an application assigns a non-zero/non-contiguous sheet id.
      var offset = new DxfPoint(placement.X, placement.Y, 0D);
      foreach (ILocalContour contour in detail.Outers)
      {
        if (contour is not LocalContour<DxfEntity> cast)
        {
          throw new CommonLineValidationException("A DXF contour could not be reconstructed for common-line export.");
        }

        List<DxfEntity> transformed = OffsetToNest(
          cast.Entities.ToList(),
          offset,
          placement.Rotation,
          placement.IsMirrored);
        yield return (cast.IsChild, SnapToGrid(transformed).ToList());
      }
    }

    private static List<DxfEntity> EnsureClosedContour(
      List<DxfEntity> entities,
      string layer,
      double geometryTolerance)
    {
      if (entities == null || entities.Count == 0)
      {
        throw new CommonLineValidationException($"{layer} contains an empty contour.");
      }

      if (entities.Count == 1)
      {
        if (entities[0] is DxfLwPolyline lightweight)
        {
          ClosePolyline(lightweight, geometryTolerance);
        }
        else if (entities[0] is DxfPolyline polyline)
        {
          ClosePolyline(polyline, geometryTolerance);
        }
        else if (entities[0] is DxfSpline spline
          && !spline.IsClosed
          && SplineDeclaresClosed(spline, geometryTolerance))
        {
          // Some CAD writers repeat the endpoint but omit bit 1 of the SPLINE flags. The source curve
          // is geometrically closed; set the DXF flag so a reader receives an explicitly closed contour.
          spline.IsClosed = true;
        }
      }

      if (entities.Count != 1 || !IsClosedCutEntity(entities[0], geometryTolerance))
      {
        try
        {
          FlatDxfJoiner.JoinEntities(entities, geometryTolerance);
        }
        catch
        {
          throw new CommonLineValidationException($"{layer} could not be rebuilt as a closed contour.");
        }
      }

      if (entities.Count != 1 || !IsClosedCutEntity(entities[0], geometryTolerance))
      {
        throw new CommonLineValidationException($"{layer} contains geometry that is not completely closed.");
      }

      entities[0].Layer = layer;
      return entities;
    }

    private static void ClosePolyline(DxfLwPolyline polyline, double geometryTolerance)
    {
      if (polyline.IsClosed)
      {
        return;
      }

      if (polyline.Vertices.Count > 3
        && Coincide(
          new DxfPoint(polyline.Vertices[0].X, polyline.Vertices[0].Y, 0D),
          new DxfPoint(polyline.Vertices[^1].X, polyline.Vertices[^1].Y, 0D),
          geometryTolerance))
      {
        polyline.Vertices.RemoveAt(polyline.Vertices.Count - 1);
        polyline.IsClosed = true;
      }
    }

    private static void ClosePolyline(DxfPolyline polyline, double geometryTolerance)
    {
      if (polyline.IsClosed)
      {
        return;
      }

      if (polyline.Vertices.Count > 3
        && Coincide(polyline.Vertices[0].Location, polyline.Vertices[^1].Location, geometryTolerance))
      {
        polyline.Vertices.RemoveAt(polyline.Vertices.Count - 1);
        polyline.IsClosed = true;
      }
    }

    private static bool IsClosedCutEntity(DxfEntity entity, double geometryTolerance)
      => (entity is DxfCircle circle
          && IsFinitePoint(circle.Center)
          && double.IsFinite(circle.Radius)
          && circle.Radius > geometryTolerance)
        || (entity is DxfEllipse ellipse
          && IsFinitePoint(ellipse.Center)
          && double.IsFinite(ellipse.MajorAxis.X)
          && double.IsFinite(ellipse.MajorAxis.Y)
          && double.IsFinite(ellipse.MajorAxis.Z)
          && IsFullUsableEllipse(ellipse, geometryTolerance))
        || (entity is DxfSpline spline
          && SplineDeclaresClosed(spline, geometryTolerance)
          && TrySampleSpline(spline, geometryTolerance, out _))
        || (entity is DxfLwPolyline lightweight
          && IsUsableClosedPolyline(lightweight, geometryTolerance))
        || (entity is DxfPolyline polyline
          && IsUsableClosedPolyline(polyline, geometryTolerance));

    private static bool IsUsableClosedPolyline(
      DxfLwPolyline polyline,
      double geometryTolerance)
    {
      if (!polyline.IsClosed
        || polyline.Vertices.Count < 3
        || !double.IsFinite(polyline.Elevation)
        || polyline.Vertices.Any(vertex => !double.IsFinite(vertex.X)
          || !double.IsFinite(vertex.Y)
          || !double.IsFinite(vertex.Bulge)))
      {
        return false;
      }

      for (int i = 0; i < polyline.Vertices.Count; i++)
      {
        DxfLwPolylineVertex start = polyline.Vertices[i];
        DxfLwPolylineVertex end = polyline.Vertices[(i + 1) % polyline.Vertices.Count];
        if (!IsUsableChord(start.X, start.Y, end.X, end.Y, geometryTolerance))
        {
          return false;
        }
      }

      return true;
    }

    private static bool IsUsableClosedPolyline(
      DxfPolyline polyline,
      double geometryTolerance)
    {
      if (!polyline.IsClosed
        || polyline.Vertices.Count < 3
        || polyline.Vertices.Any(vertex => !IsFinitePoint(vertex.Location)
          || !double.IsFinite(vertex.Bulge)))
      {
        return false;
      }

      for (int i = 0; i < polyline.Vertices.Count; i++)
      {
        DxfPoint start = polyline.Vertices[i].Location;
        DxfPoint end = polyline.Vertices[(i + 1) % polyline.Vertices.Count].Location;
        if (!IsUsableChord(start.X, start.Y, end.X, end.Y, geometryTolerance))
        {
          return false;
        }
      }

      return true;
    }

    private static bool IsUsableChord(
      double startX,
      double startY,
      double endX,
      double endY,
      double geometryTolerance)
    {
      double dx = endX - startX;
      double dy = endY - startY;
      double length = Math.Sqrt((dx * dx) + (dy * dy));
      return double.IsFinite(length) && length > geometryTolerance;
    }

    private static bool IsFullUsableEllipse(DxfEllipse ellipse, double tolerance)
    {
      double major = Math.Sqrt(
        (ellipse.MajorAxis.X * ellipse.MajorAxis.X)
        + (ellipse.MajorAxis.Y * ellipse.MajorAxis.Y));
      double minor = major * Math.Abs(ellipse.MinorAxisRatio);
      double sweep = Math.Abs(ellipse.EndParameter - ellipse.StartParameter);
      double angularTolerance = Math.Max(1e-10, tolerance / Math.Max(major, tolerance));
      return double.IsFinite(major)
        && double.IsFinite(minor)
        && major > tolerance
        && minor > tolerance
        && double.IsFinite(sweep)
        && Math.Abs(sweep - (2D * Math.PI)) <= angularTolerance;
    }

    private static bool SplineDeclaresClosed(DxfSpline spline, double tolerance)
    {
      if (spline.IsClosed || spline.IsPeriodic)
      {
        return true;
      }

      if (spline.FitPoints.Count >= 2
        && Coincide(spline.FitPoints[0], spline.FitPoints[^1], tolerance))
      {
        return true;
      }

      return spline.ControlPoints.Count >= 2
        && Coincide(
          spline.ControlPoints[0].Point,
          spline.ControlPoints[^1].Point,
          tolerance);
    }

    private static bool TrySampleSpline(
      DxfSpline spline,
      double tolerance,
      out List<DxfPoint> points)
    {
      points = new List<DxfPoint>();
      try
      {
        RawDetail<DxfEntity> sampled = DxfParser.ConvertDxfToRawDetail(
          "common-line-validation.dxf",
          new DxfEntity[] { spline });
        ILocalContour contour = sampled.Outers.SingleOrDefault();
        if (contour == null)
        {
          return false;
        }

        points.AddRange(contour.Points.Select(point => new DxfPoint(point.X, point.Y, 0D)));
        if (points.Count > 1 && Coincide(points[0], points[^1], tolerance))
        {
          points.RemoveAt(points.Count - 1);
        }

        if (points.Count < 3 || points.Any(point => !IsFinitePoint(point)))
        {
          return false;
        }

        for (int i = 0; i < points.Count; i++)
        {
          DxfPoint start = points[i];
          DxfPoint end = points[(i + 1) % points.Count];
          if (!IsUsableChord(start.X, start.Y, end.X, end.Y, tolerance))
          {
            return false;
          }
        }

        return true;
      }
      catch
      {
        points.Clear();
        return false;
      }
    }

    private static bool Coincide(DxfPoint a, DxfPoint b, double geometryTolerance)
      => Math.Abs(a.X - b.X) <= geometryTolerance
        && Math.Abs(a.Y - b.Y) <= geometryTolerance;

    internal static void ValidateCommonLineDxf(DxfFile dxf, double geometryTolerance)
    {
      foreach (DxfEntity entity in dxf.Entities)
      {
        if (entity.Layer == CutInnerLayer || entity.Layer == CutOuterLayer)
        {
          if (!IsClosedCutEntity(entity, geometryTolerance))
          {
            throw new CommonLineValidationException(
              $"{entity.Layer} contains an open, non-finite, tiny, or otherwise invalid entity.");
          }

          if (entity.Layer == CutOuterLayer
            && IsSelfIntersectingCutOuter(entity, geometryTolerance))
          {
            throw new CommonLineValidationException("CUT_OUTER contains a self-intersecting contour.");
          }
        }
        else if (entity.Layer == CommonLineLayer)
        {
          if (entity is DxfLine line)
          {
            double dx = line.P2.X - line.P1.X;
            double dy = line.P2.Y - line.P1.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (!IsFinitePoint(line.P1)
              || !IsFinitePoint(line.P2)
              || !double.IsFinite(length)
              || length <= geometryTolerance)
            {
              throw new CommonLineValidationException(
                "COMMON_LINE contains a non-finite, zero-length, or tiny LINE entity.");
            }
          }
          else if (entity is DxfArc arc)
          {
            if (!IsFinitePoint(arc.Center)
              || !double.IsFinite(arc.Radius)
              || !double.IsFinite(arc.StartAngle)
              || !double.IsFinite(arc.EndAngle))
            {
              throw new CommonLineValidationException(
                "COMMON_LINE contains a non-finite ARC center, radius, or angle.");
            }

            double rawSweep = arc.EndAngle - arc.StartAngle;
            if (!double.IsFinite(rawSweep))
            {
              throw new CommonLineValidationException(
                "COMMON_LINE contains ARC angles whose sweep is non-finite.");
            }

            double sweep = rawSweep % 360D;
            if (sweep <= 0D)
            {
              sweep += 360D;
            }

            double arcLength = arc.Radius * sweep * Math.PI / 180D;
            if (arc.Radius <= geometryTolerance
              || !double.IsFinite(sweep)
              || !double.IsFinite(arcLength)
              || arcLength <= geometryTolerance)
            {
              throw new CommonLineValidationException("COMMON_LINE contains a zero-length or invalid ARC.");
            }
          }
          else if (entity is DxfCircle circle)
          {
            if (!IsFinitePoint(circle.Center)
              || !double.IsFinite(circle.Radius)
              || circle.Radius <= geometryTolerance)
            {
              throw new CommonLineValidationException(
                "COMMON_LINE contains a non-finite, zero-length, or invalid CIRCLE.");
            }
          }
          else if (entity is DxfLwPolyline lightweight)
          {
            if (lightweight.Vertices.Count < 2)
            {
              throw new CommonLineValidationException("COMMON_LINE contains a degenerate LWPOLYLINE.");
            }

            int segmentCount = lightweight.IsClosed
              ? lightweight.Vertices.Count
              : lightweight.Vertices.Count - 1;
            for (int i = 0; i < segmentCount; i++)
            {
              DxfLwPolylineVertex start = lightweight.Vertices[i];
              DxfLwPolylineVertex end = lightweight.Vertices[(i + 1) % lightweight.Vertices.Count];
              double dx = end.X - start.X;
              double dy = end.Y - start.Y;
              if (!double.IsFinite(start.X)
                || !double.IsFinite(start.Y)
                || !double.IsFinite(end.X)
                || !double.IsFinite(end.Y)
                || !double.IsFinite(start.Bulge)
                || Math.Sqrt((dx * dx) + (dy * dy)) <= geometryTolerance)
              {
                throw new CommonLineValidationException("COMMON_LINE contains an invalid or tiny LWPOLYLINE segment.");
              }
            }
          }
          else
          {
            throw new CommonLineValidationException(
              "COMMON_LINE contains an entity other than LINE, circular LWPOLYLINE, ARC, or CIRCLE.");
          }
        }
      }
    }

    private static bool IsFinitePoint(DxfPoint point)
      => double.IsFinite(point.X)
        && double.IsFinite(point.Y)
        && double.IsFinite(point.Z);

    private static bool IsSelfIntersectingCutOuter(DxfEntity entity, double tolerance)
    {
      List<(DxfPoint Start, DxfPoint End, double Bulge)> segments;
      if (entity is DxfLwPolyline lightweight)
      {
        segments = Enumerable.Range(0, lightweight.Vertices.Count)
          .Select(i =>
          {
            DxfLwPolylineVertex start = lightweight.Vertices[i];
            DxfLwPolylineVertex end = lightweight.Vertices[(i + 1) % lightweight.Vertices.Count];
            return (
              new DxfPoint(start.X, start.Y, 0D),
              new DxfPoint(end.X, end.Y, 0D),
              start.Bulge);
          })
          .ToList();
      }
      else if (entity is DxfPolyline polyline)
      {
        segments = Enumerable.Range(0, polyline.Vertices.Count)
          .Select(i =>
          {
            DxfVertex start = polyline.Vertices[i];
            DxfVertex end = polyline.Vertices[(i + 1) % polyline.Vertices.Count];
            return (start.Location, end.Location, start.Bulge);
          })
          .ToList();
      }
      else if (entity is DxfSpline spline
        && TrySampleSpline(spline, tolerance, out List<DxfPoint> splinePoints))
      {
        segments = Enumerable.Range(0, splinePoints.Count)
          .Select(i => (
            splinePoints[i],
            splinePoints[(i + 1) % splinePoints.Count],
            0D))
          .ToList();
      }
      else
      {
        // A usable full ellipse and circle are inherently simple; unsupported/invalid entities have
        // already failed IsClosedCutEntity before this method is reached.
        return false;
      }

      double effectiveTolerance = Math.Max(0D, tolerance);
      var sampled = new List<List<(DxfPoint Start, DxfPoint End)>>(segments.Count);
      foreach ((DxfPoint Start, DxfPoint End, double Bulge) segment in segments)
      {
        if (CutDistance(segment.Start, segment.End) <= effectiveTolerance)
        {
          return true;
        }

        sampled.Add(SampleCutSegment(segment.Start, segment.End, segment.Bulge, effectiveTolerance));
      }

      for (int i = 0; i < sampled.Count; i++)
      {
        int iNext = (i + 1) % sampled.Count;
        for (int j = i + 1; j < sampled.Count; j++)
        {
          int jNext = (j + 1) % sampled.Count;
          if (iNext == j || jNext == i)
          {
            continue; // neighbouring source segments intentionally meet at their shared vertex
          }

          if (sampled[i].Any(first => sampled[j].Any(second =>
            CutSegmentsConflict(first.Start, first.End, second.Start, second.End, effectiveTolerance))))
          {
            return true;
          }
        }
      }

      return false;
    }

    private static List<(DxfPoint Start, DxfPoint End)> SampleCutSegment(
      DxfPoint start,
      DxfPoint end,
      double bulge,
      double tolerance)
    {
      if (Math.Abs(bulge) < 1e-12)
      {
        return new List<(DxfPoint, DxfPoint)> { (start, end) };
      }

      double theta = 4D * Math.Atan(bulge);
      double dx = end.X - start.X;
      double dy = end.Y - start.Y;
      double chord = Math.Sqrt((dx * dx) + (dy * dy));
      double sinHalf = Math.Sin(theta / 2D);
      if (chord <= tolerance || Math.Abs(sinHalf) < 1e-12)
      {
        return new List<(DxfPoint, DxfPoint)> { (start, end) };
      }

      double signedRadius = chord / (2D * sinHalf);
      double radius = Math.Abs(signedRadius);
      double midX = (start.X + end.X) / 2D;
      double midY = (start.Y + end.Y) / 2D;
      double apothem = signedRadius * Math.Cos(theta / 2D);
      double centerX = midX + (apothem * (-dy / chord));
      double centerY = midY + (apothem * (dx / chord));
      double firstAngle = Math.Atan2(start.Y - centerY, start.X - centerX);

      // Match the parser's practical 1..6 degree tessellation while using the active tolerance to
      // tighten ordinary-sized arcs. This keeps validation bounded for very large-radius CAD geometry.
      double chordTolerance = Math.Max(1e-9, tolerance);
      double cosHalf = 1D - (chordTolerance / radius);
      cosHalf = Math.Max(-1D, Math.Min(1D, cosHalf));
      double step = 2D * Math.Acos(cosHalf);
      step = Math.Max(Math.PI / 180D, Math.Min(Math.PI / 30D, step));
      int count = Math.Max(1, (int)Math.Ceiling(Math.Abs(theta) / step));

      var result = new List<(DxfPoint Start, DxfPoint End)>(count);
      DxfPoint prior = start;
      for (int i = 1; i <= count; i++)
      {
        double angle = firstAngle + ((theta * i) / count);
        DxfPoint next = i == count
          ? end
          : new DxfPoint(
            centerX + (radius * Math.Cos(angle)),
            centerY + (radius * Math.Sin(angle)),
            0D);
        result.Add((prior, next));
        prior = next;
      }

      return result;
    }

    private static bool CutSegmentsConflict(
      DxfPoint a,
      DxfPoint b,
      DxfPoint c,
      DxfPoint d,
      double tolerance)
    {
      double abC = CutCross(a, b, c);
      double abD = CutCross(a, b, d);
      double cdA = CutCross(c, d, a);
      double cdB = CutCross(c, d, b);
      double scale = Math.Max(1D, Math.Max(CutDistance(a, b), CutDistance(c, d)));
      double epsilon = tolerance * scale;
      if (((abC > epsilon && abD < -epsilon) || (abC < -epsilon && abD > epsilon))
        && ((cdA > epsilon && cdB < -epsilon) || (cdA < -epsilon && cdB > epsilon)))
      {
        return true;
      }

      return (Math.Abs(abC) <= epsilon && CutPointOnSegment(c, a, b, tolerance))
        || (Math.Abs(abD) <= epsilon && CutPointOnSegment(d, a, b, tolerance))
        || (Math.Abs(cdA) <= epsilon && CutPointOnSegment(a, c, d, tolerance))
        || (Math.Abs(cdB) <= epsilon && CutPointOnSegment(b, c, d, tolerance));
    }

    private static bool CutPointOnSegment(DxfPoint point, DxfPoint start, DxfPoint end, double tolerance)
      => point.X >= Math.Min(start.X, end.X) - tolerance
        && point.X <= Math.Max(start.X, end.X) + tolerance
        && point.Y >= Math.Min(start.Y, end.Y) - tolerance
        && point.Y <= Math.Max(start.Y, end.Y) + tolerance;

    private static double CutCross(DxfPoint a, DxfPoint b, DxfPoint point)
      => ((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X));

    private static double CutDistance(DxfPoint a, DxfPoint b)
    {
      double dx = b.X - a.X;
      double dy = b.Y - a.Y;
      return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static void AddCommonLineLayers(DxfFile dxf)
    {
      AddLayer(dxf, CutInnerLayer, DxfColorHelpers.GetClosestDefaultIndexColor(255, 0, 0));
      AddLayer(dxf, CommonLineLayer, DxfColorHelpers.GetClosestDefaultIndexColor(0, 255, 0));
      AddLayer(dxf, CutOuterLayer, DxfColorHelpers.GetClosestDefaultIndexColor(0, 0, 255));
      AddLayer(dxf, OffcutLayer, DxfColorHelpers.GetClosestDefaultIndexColor(255, 255, 0));
    }

    private static void AddLayer(DxfFile dxf, string name, DxfColor color)
    {
      if (!dxf.Layers.Any(layer => string.Equals(layer.Name, name, StringComparison.Ordinal)))
      {
        dxf.Layers.Add(new DxfLayer(name, color));
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

        // What the parts carry engraved, added AFTER the merge and the join, the same way the offcut
        // cuts are: a mark must never be chained into a part's contour.
        var sheetXoffset = -sheet.WidthCalculated * i;
        foreach (INfp polygon in polygons.Where(o => o.Sheet.Id == sheet.Id && o.Fitted))
        {
          foreach (DxfEntity mark in PlacedMarks(
            sheetdxf,
            polygon.Name,
            new DxfPoint(polygon.X + sheetXoffset, polygon.Y, 0D),
            polygon.Rotation,
            polygon.IsMirrored))
          {
            sheetdxf.Entities.Add(mark);
          }
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
