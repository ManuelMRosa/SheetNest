"""unfold_macro.py  -  Desdobla (unfold) una pieza de chapa 3D a un patron plano DXF, headless.

Uso:
    freecadcmd unfold_macro.py <entrada.step|.stp|.iges|.igs> <salida.dxf> [kfactor] [ansi|din]

Estrategia:
  1. Importa el 3D y toma el solido de chapa (mayor volumen).
  2. Auto-selecciona la cara base = mayor cara PLANA (heuristica; el issue #95 sigue abierto).
  3. Corre el unfolder V2 (getUnfold); si falla, cae al V1.
  4. Toma la mayor cara plana del solido desdoblado y exporta su contorno + agujeros a DXF.

Codigos de salida:
  0 = OK     2 = sin solido     3 = sin cara plana base
  4 = unfold fallo (V2 y V1)    5 = export DXF fallo
"""
import os
import sys
import Part
import FreeCAD


def user_args():
    """freecadcmd deja el script en argv[1]; devolvemos lo que va DESPUES del script."""
    me = os.path.basename(__file__) if "__file__" in globals() else "unfold_macro.py"
    for i, a in enumerate(sys.argv):
        if os.path.basename(str(a)) == me:
            return sys.argv[i + 1:]
    return sys.argv[1:]


RESULT_FILE = None  # sidecar <dxf>.result: el llamador (C#) lo lee porque FreeCAD no manda
                    # su stdout por el pipe redirigido de .NET.


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
    """Espesor de chapa (mm) de un solido, o None si no se puede estimar."""
    try:
        planar = [(i, f) for i, f in enumerate(solid.Faces)
                  if isinstance(f.Surface, Part.Plane)]
        if not planar:
            return None
        idx0, _ = max(planar, key=lambda p: p[1].Area)  # indice 0-based de la mayor cara plana
        from SheetMetalNewUnfolder import EstimateThickness
        t = EstimateThickness.using_best_method(solid, idx0)
        return float(t) if t and t > 0 else None
    except Exception as e:
        log("thickness fallo: %s" % e)
        return None


def unfold_object(obj, kfactor, standard, scale):
    """Desdobla UN solido (como document object). Devuelve un flat_shape 2D o None si no es chapa."""
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
        # Extraccion limpia: OuterWire topologico + agujeros, SIN lineas de doblez (headless-safe).
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
            log("extract_manually fallo (%s); TechDraw..." % ex)
            try:
                flat_shape = SketchExtraction.extract_with_techdraw(unfolded, root_normal)
            except Exception as ex2:
                log("TechDraw fallo (%s)" % ex2)
    except Exception as e:
        log("V2 fallo (%s); V1..." % e)
        try:
            import SheetMetalUnfolder
            res = SheetMetalUnfolder.getUnfold({1: kfactor}, obj, baseFace, standard)
            unfolded = res[0]
        except Exception as e2:
            log("V1 fallo (%s)" % e2)
            return None

    # Fallback: mayor cara plana del solido desdoblado.
    if flat_shape is None:
        if unfolded is None or not getattr(unfolded, "Faces", None):
            return None
        fp = [f for f in unfolded.Faces if isinstance(f.Surface, Part.Plane)]
        if not fp:
            return None
        flat_shape = max(fp, key=lambda f: f.Area)

    # Escala de unidad (mm -> unidad del nest). transformShape(checkScale=True) escala PRESERVANDO
    # lineas/arcos/circulos (transformGeometry los convierte a SPLINE, que DeepNest no soporta).
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
        fail(1, "faltan argumentos: <entrada> <salida_base.dxf> [kfactor] [ansi|din] [scale]")
    src = args[0]
    dst = args[1]  # ruta base; cada pieza sale como "<base> [i].dxf"
    kfactor = float(args[2]) if len(args) > 2 else 0.40
    standard = args[3] if len(args) > 3 else "ansi"
    scale = float(args[4]) if len(args) > 4 else 1.0
    mode = args[5] if len(args) > 5 else "unfold"  # "unfold" (default) | "probe"

    global RESULT_FILE
    RESULT_FILE = dst + ".result"

    doc = FreeCAD.newDocument("unfold")
    Part.insert(src, doc.Name)
    doc.recompute()

    # Reunir TODOS los solidos del archivo (un ensamble trae varios) -> cada uno es una pieza.
    solids = []
    for o in doc.Objects:
        if hasattr(o, "Shape") and o.Shape is not None:
            solids += list(o.Shape.Solids)
    if not solids:
        fail(2, "el archivo no contiene ningun solido")
    log("solidos encontrados: %d" % len(solids))

    if mode == "probe":
        # Solo detectar espesor por solido (para el dialogo de importacion); NO desdobla.
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
                log("solido %d: no es chapa desdoblable, se salta" % i)
                continue
            out_i = "%s [%d].dxf" % (base, i)
            fobj = doc.addObject("Part::Feature", "Flat_%d" % i)
            fobj.Shape = flat
            doc.recompute()
            importDXF.export([fobj], out_i)
            produced.append(out_i)
            bb = flat.BoundBox
            dims = sorted([bb.XLength, bb.YLength, bb.ZLength], reverse=True)[:2]
            log("solido %d -> %s  (%.1f x %.1f)" % (i, out_i, dims[0], dims[1]))
        except Exception as e:
            log("solido %d fallo: %s" % (i, e))
            continue

    if not produced:
        fail(4, "ningun solido se pudo desdoblar (no parece chapa de espesor constante)")

    # Pieza unica -> nombre sin " [1]".
    if len(produced) == 1:
        single = base + ".dxf"
        try:
            if os.path.exists(single):
                os.remove(single)
            os.rename(produced[0], single)
            produced[0] = single
        except Exception as e:
            log("rename single fallo: %s" % e)

    # UNFOLD_RESULT=OK:<produced>:<total>:<p1>|...   (split(':',3) en C# preserva las ':' de rutas)
    emit_result("UNFOLD_RESULT=OK:%d:%d:%s" % (len(produced), len(solids), "|".join(produced)))
    log("total piezas desdobladas: %d de %d solidos" % (len(produced), len(solids)))


main()
