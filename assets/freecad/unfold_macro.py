"""unfold_macro.py  -  Unfolds a 3D sheet-metal part into a flat DXF pattern, headless.

Usage:
    freecadcmd unfold_macro.py <input.step|.stp|.iges|.igs> <output.dxf> [kfactor] [ansi|din]

Strategy:
  1. Import the 3D file and take the sheet-metal solid (largest volume).
  2. Auto-select the base face = largest PLANAR face (heuristic; issue #95 is still open).
  3. Run the V2 unfolder (getUnfold); if it fails, fall back to V1.
  4. Take the largest planar face of the unfolded solid and export its outline + holes to DXF.

Exit codes:
  0 = OK     2 = no solid       3 = no planar base face
  4 = unfold failed (V2 and V1) 5 = DXF export failed
"""
import os
import sys
import Part
import FreeCAD


def user_args():
    """freecadcmd leaves the script in argv[1]; return whatever comes AFTER the script."""
    me = os.path.basename(__file__) if "__file__" in globals() else "unfold_macro.py"
    for i, a in enumerate(sys.argv):
        if os.path.basename(str(a)) == me:
            return sys.argv[i + 1:]
    return sys.argv[1:]


RESULT_FILE = None  # sidecar <dxf>.result: the caller (C#) reads it because FreeCAD does not
                    # send its stdout through .NET's redirected pipe.


def log(msg):
    FreeCAD.Console.PrintMessage("[unfold] " + str(msg) + "\n")


def emit_result(line):
    print(line)
    try:
        if RESULT_FILE:
            with open(RESULT_FILE, "w") as fh:
                fh.write(line + "\n")
    except Exception:
        pass


def fail(code, msg):
    FreeCAD.Console.PrintError("[unfold] ERROR: " + str(msg) + "\n")
    emit_result("UNFOLD_RESULT=FAIL:" + str(code) + ":" + str(msg))
    sys.exit(code)


def largest_planar_face_name(shape):
    planar = [(i + 1, f) for i, f in enumerate(shape.Faces)
              if isinstance(f.Surface, Part.Plane)]
    if not planar:
        return None
    idx, _ = max(planar, key=lambda p: p[1].Area)
    return "Face%d" % idx


def solid_thickness(solid):
    """Sheet thickness (mm) of a solid, or None if it cannot be estimated."""
    try:
        planar = [(i, f) for i, f in enumerate(solid.Faces)
                  if isinstance(f.Surface, Part.Plane)]
        if not planar:
            return None
        idx0, _ = max(planar, key=lambda p: p[1].Area)  # 0-based index of the largest planar face
        from SheetMetalNewUnfolder import EstimateThickness
        t = EstimateThickness.using_best_method(solid, idx0)
        return float(t) if t and t > 0 else None
    except Exception as e:
        log("thickness failed: %s" % e)
        return None


def unfold_object(obj, kfactor, standard, scale):
    """Unfolds ONE solid (as a document object). Returns a 2D flat_shape or None if not sheet metal."""
    baseFace = largest_planar_face_name(obj.Shape)
    if baseFace is None:
        return None

    unfolded = None
    flat_shape = None
    try:
        import SheetMetalNewUnfolder
        from SheetMetalNewUnfolder import BendAllowanceCalculator, SketchExtraction
        bac = BendAllowanceCalculator.from_single_value(kfactor, standard)
        sel_face, unfolded, bend_lines, root_normal, bend_info = \
            SheetMetalNewUnfolder.getUnfold(bac, obj, baseFace)
        # Clean extraction: topological OuterWire + holes, WITHOUT bend lines (headless-safe).
        try:
            profile, inner_wires, hole_wires = SketchExtraction.extract_manually(unfolded, root_normal)
            tr = SketchExtraction.move_to_origin(profile, sel_face)
            edges = list(profile.transformed(tr).Edges)
            for w in inner_wires:
                edges += w.transformed(tr).Edges
            for w in hole_wires:
                edges += w.transformed(tr).Edges
            flat_shape = Part.makeCompound(edges)
        except Exception as ex:
            log("extract_manually failed (%s); TechDraw..." % ex)
            try:
                flat_shape = SketchExtraction.extract_with_techdraw(unfolded, root_normal)
            except Exception as ex2:
                log("TechDraw failed (%s)" % ex2)
    except Exception as e:
        log("V2 failed (%s); V1..." % e)
        try:
            import SheetMetalUnfolder
            res = SheetMetalUnfolder.getUnfold({1: kfactor}, obj, baseFace, standard)
            unfolded = res[0]
        except Exception as e2:
            log("V1 failed (%s)" % e2)
            return None

    # Fallback: largest planar face of the unfolded solid.
    if flat_shape is None:
        if unfolded is None or not getattr(unfolded, "Faces", None):
            return None
        fp = [f for f in unfolded.Faces if isinstance(f.Surface, Part.Plane)]
        if not fp:
            return None
        flat_shape = max(fp, key=lambda f: f.Area)

    # Unit scale (mm -> nest unit). transformShape(checkScale=True) scales while PRESERVING
    # lines/arcs/circles (transformGeometry converts them to SPLINE, which DeepNest does not support).
    if abs(scale - 1.0) > 1e-9:
        m = FreeCAD.Matrix()
        m.scale(scale, scale, scale)
        s = flat_shape.copy()
        s.transformShape(m, False, True)
        flat_shape = s

    return flat_shape


def main():
    args = user_args()
    if len(args) < 2:
        fail(1, "missing arguments: <input> <output_base.dxf> [kfactor] [ansi|din] [scale]")
    src = args[0]
    dst = args[1]  # base path; each part comes out as "<base> [i].dxf"
    kfactor = float(args[2]) if len(args) > 2 else 0.40
    standard = args[3] if len(args) > 3 else "ansi"
    scale = float(args[4]) if len(args) > 4 else 1.0
    mode = args[5] if len(args) > 5 else "unfold"  # "unfold" (default) | "probe"

    global RESULT_FILE
    RESULT_FILE = dst + ".result"

    doc = FreeCAD.newDocument("unfold")
    Part.insert(src, doc.Name)
    doc.recompute()

    # Gather ALL solids in the file (an assembly brings several) -> each one is a part.
    solids = []
    for o in doc.Objects:
        if hasattr(o, "Shape") and o.Shape is not None:
            solids += list(o.Shape.Solids)
    if not solids:
        fail(2, "the file contains no solid")
    log("solids found: %d" % len(solids))

    if mode == "probe":
        # Only detect thickness per solid (for the import dialog); does NOT unfold.
        ths = []
        for solid in solids:
            t = solid_thickness(solid)
            ths.append("%.4f" % t if t else "0")
        emit_result("PROBE_RESULT=OK:%d:%s" % (len(solids), "|".join(ths)))
        return

    import importDXF
    base, _ext = os.path.splitext(dst)
    produced = []
    for i, solid in enumerate(solids, start=1):
        try:
            obj = doc.addObject("Part::Feature", "Solid_%d" % i)
            obj.Shape = solid
            doc.recompute()
            flat = unfold_object(obj, kfactor, standard, scale)
            if flat is None:
                log("solid %d: not unfoldable sheet metal, skipping" % i)
                continue
            out_i = "%s [%d].dxf" % (base, i)
            fobj = doc.addObject("Part::Feature", "Flat_%d" % i)
            fobj.Shape = flat
            doc.recompute()
            importDXF.export([fobj], out_i)
            produced.append(out_i)
            bb = flat.BoundBox
            dims = sorted([bb.XLength, bb.YLength, bb.ZLength], reverse=True)[:2]
            log("solid %d -> %s  (%.1f x %.1f)" % (i, out_i, dims[0], dims[1]))
        except Exception as e:
            log("solid %d failed: %s" % (i, e))
            continue

    if not produced:
        fail(4, "no solid could be unfolded (does not look like constant-thickness sheet metal)")

    # Single part -> name without " [1]".
    if len(produced) == 1:
        single = base + ".dxf"
        try:
            if os.path.exists(single):
                os.remove(single)
            os.rename(produced[0], single)
            produced[0] = single
        except Exception as e:
            log("rename single failed: %s" % e)

    # UNFOLD_RESULT=OK:<produced>:<total>:<p1>|...   (split(':',3) on the C# side keeps the ':' in paths)
    emit_result("UNFOLD_RESULT=OK:%d:%d:%s" % (len(produced), len(solids), "|".join(produced)))
    log("total parts unfolded: %d of %d solids" % (len(produced), len(solids)))


main()
