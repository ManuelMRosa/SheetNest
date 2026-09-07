# SheetNest — contexto para agentes

## Inicio rápido

SheetNest es una aplicación Windows de nesting para chapa metálica, derivada de DeepNestSharp. Importa DXF y piezas 3D, distribuye piezas, permite edición manual y exporta DXF y reportes PDF. C# / .NET 6 / WPF, motor Sparrow y geometría administrada/nativa.

Al retomar:
1. Ejecutar `git status --short --branch` y `git log -5 --oneline` en este repositorio.
2. Leer este archivo y abrir documentación adicional solo según la tarea.
3. Conservar cambios existentes y trabajar sobre la rama actual.
4. Si el usuario solo pide trabajar en SheetNest, resumir el estado y preguntar qué quiere hacer. Los commits previos no son tareas pendientes.

## Rutas de esta estación

- Código: C:/Users/rosam/source/repos/DeepNestSharp
- Origin: https://github.com/ManuelMRosa/SheetNest.git
- Upstream histórico: https://github.com/9swampy/DeepNestSharp.git
- Sitio web (repositorio separado): C:/Users/rosam/sheetnest-website
- Archivos de trabajo: C:/Users/rosam/SheetNest (projects/*.dnest, test-data/dxf, reports, releases, diagnostics).
- App instalada: C:/Users/rosam/AppData/Local/Programs/SheetNest/SheetNest.exe
- Datos de ejecución: C:/Users/rosam/AppData/Local/SheetNest
- SheetNest/backups contiene copias históricas, no el código activo.
- Las notas de junio/julio en SheetNest/docs y LEEME.txt son históricas; no determinan la rama, versión ni arquitectura actuales.

## Mapa del código

- DeepNestSharp/: app WPF; MainWindow.xaml.cs, Ui/, SessionState.cs.
- DeepNestSharp/RasterNest/: SparrowNestService.cs, CommonEdgeClearance.cs, RasterCompact.cs.
- DeepNestLib.Core/: IO/ para DXF, Placement/ para geometría y CommonEdge, NestProject/ para persistencia.
- DeepNestSharp.Domain/: modelos y configuración observable.
- SparrowEngine/: sparrow.exe y sheetnest-sparrow.patch.
- assets/freecad/: desplegado 3D offline.
- DeepNestSharp.CiTests/ y DeepNestLib.CiTests/: pruebas de aplicación y núcleo.
- NestBench/ y GpuNestLab/: benchmarks y laboratorio.
- installer/: empaquetado y política de versiones.

## Documentos según la tarea

- README.md: funciones del producto.
- BUILDING.md: requisitos y compilación.
- COMMON_LINE_IMPLEMENTATION.md: arquitectura, invariantes, límites y pruebas del corte por línea común. Leer antes de modificar esta función.
- installer/RELEASING.md: pruebas y publicación. Betas como ZIP portable; estables como MSI.
- DeepNestSharp/DeepNestSharp.csproj: versión declarada del código. No inferir la versión publicada por nombres de MSI o carpetas publish-*.

## Comandos desde la raíz del repositorio

Usar PowerShell. PowerShell 5.1 no admite && y ||.

```powershell
dotnet build .\DeepNestSharp\DeepNestSharp.csproj -c Release
dotnet test .\DeepNestSharp.CiTests\DeepNestSharp.CiTests.csproj -c Release
dotnet test .\DeepNestLib.CiTests\DeepNestLib.CiTests.csproj -c Release
& '.\DeepNestSharp\bin\Release\net6.0-windows\SheetNest.exe'
```

Ejecutar las suites por proyecto: installer/RELEASING.md documenta que dotnet test sobre toda la solución puede quedarse en restore. Elegir pruebas relacionadas con el cambio; no compilar ni ejecutar suites solo para recuperar contexto.

Para pruebas nativas de Hodgson/rendimiento seguir COMMON_LINE_IMPLEMENTATION.md y comprobar las rutas del motor/DXF. SPARROW_EXE y SHEETNEST_REQUIRE_SPARROW controlan la ejecución nativa. No presentar pruebas omitidas como verificadas.

## Estado observado el 2026-09-06

Verificar con Git al iniciar; esta sección es una referencia fechada.
- Rama: `seam-count-and-lead-bench`. HEAD: `953ca00`.
- **Hay mucho trabajo SIN COMITEAR sobre HEAD.** `git status` antes de tocar nada.
- Trabajo de esta sesión: se retiró por completo la integración con el otro programa de CAM (proyectos,
  archivos de importación/exportación, campos persistidos y la ruta legacy del corte común con sus
  tolerancias), y se limpió su nombre del código y de la documentación pública. Se devolvió el
  desplazamiento de vecinos del planificador directo, que se había quitado por velocidad, y se abarató
  en su lugar. Suites: 941 (núcleo) y 394 (aplicación), cero fallos.

### El corte común se valida contra DOS bancos, nunca uno

Es la lección más cara de esta rama. `Test2SamePartDirectRegressionFixture` usa **una sola pieza** y
`MixedCommonLineRegressionFixture` usa **piezas distintas**, y miden cosas diferentes: un cambio puede
dejar el primero clavado y vaciar el segundo. Quitar el desplazamiento de vecinos hizo exactamente eso,
y se publicó un MSI con trabajos mixtos dando cero cortes compartidos.

Valores que no deben moverse (con `SHEETNEST_BENCH_EFFORT=Fast`):

| banco | valor |
|---|---|
| test2, 16/17/24/25 copias | 16/16 · 17/17 · 24/26 · 25/27 |
| test2, 26 copias | 26 colocadas, 27 aristas, `isolated=1`, cadena 16 |
| test2, 27 copias | 26 colocadas, 28 aristas |
| mixto | 30 colocadas, 21 aristas |
| todos | `unsafePairs=0`, `minUnrecordedGap=0.24998` |

Necesitan `SPARROW_EXE` y `SHEETNEST_MIXED_COMMON_LINE_DXF_DIR` con `test2.dxf`, `Hodgson.dxf` e
`inhole.dxf`. **Sin `SPARROW_EXE` las pruebas nativas se SALTAN y se ven exactamente igual que
verdes**; así estuvieron cinco rotas un día entero en esta misma rama.

## Mantener el contexto

Al terminar trabajo relevante, actualizar el estado con fecha, cambios, validación realmente ejecutada y pendientes concretos. Mantenerlo breve y guardar detalles técnicos en el documento correspondiente. No almacenar credenciales ni conversaciones completas. Buscar primero con rg en el módulo relevante; evitar recorrer backups, bin, obj, publish-* y todo el perfil para orientarse.
