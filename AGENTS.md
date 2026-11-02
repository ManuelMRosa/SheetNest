# SheetNest — contexto para agentes

## Inicio rápido

SheetNest es una aplicación Windows de nesting para chapa metálica, derivada de DeepNestSharp. Importa DXF y piezas 3D, distribuye piezas, permite edición manual y exporta DXF y reportes PDF. C# / .NET 10 / WPF, motor Sparrow y geometría administrada/nativa.

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
- Localization/: todos los textos que ve el usuario (Strings.resx en inglés, Strings.es.resx en español) y Languages.cs (idioma al arrancar, menú Language). Un texto nuevo va a los dos resx; guía en Localization/README.md.
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
& '.\DeepNestSharp\bin\Release\net10.0-windows\SheetNest.exe'
```

Ejecutar las suites por proyecto: installer/RELEASING.md documenta que dotnet test sobre toda la solución puede quedarse en restore. Elegir pruebas relacionadas con el cambio; no compilar ni ejecutar suites solo para recuperar contexto.

Para pruebas nativas de Hodgson/rendimiento seguir COMMON_LINE_IMPLEMENTATION.md y comprobar las rutas del motor/DXF. SPARROW_EXE y SHEETNEST_REQUIRE_SPARROW controlan la ejecución nativa. No presentar pruebas omitidas como verificadas.

## Estado observado el 2026-10-29

Verificar con Git al iniciar; esta sección es una referencia fechada.
- Publicada la **1.1.12** desde `main` (`Release 1.1.12`, etiqueta `v1.1.12`). Entraron: motor sparrow 0.3.0,
  planificador de corte común mucho más rápido con el mismo resultado, luz de lámina en las piezas, aviso de
  líneas abiertas (#11), botones Export / Nest report, la app en español y el idioma en el instalador.
- Idiomas: textos en `Localization/`; el idioma elegido vive en `HKCU\Software\SheetNest\Language`
  (`auto`/`en`/`es`), lo escriben el MSI y Configuración > Idioma.
- Suites antes de publicar: núcleo 1047 + 1 de tiempo inestable
  (`ClipperFixture.CurrentImplementationOfScaleUpPathsShouldBeTheFastest`, falla 1 de cada 5 sin cambios),
  app completa con el motor real 470/470.

## Estado observado el 2026-10-29 (noche): rama `nest-time`, sin commit

- Fast/Normal/Best se sustituyó por un **tiempo de anidado** en el menú de ANIDAR y en Configuración
  avanzada: 15 s por defecto; 15 s, 30 s, 1, 2, 5 y 10 min. `RasterNest/NestRounds.cs` corre rondas fijas
  (1 = Fast, 2 = Normal, 3 = Best, 4+ = Best con otras semillas); cada ronda sale exacta y el tiempo solo
  decide cuántas caben. La ronda que corta el límite se tira entera; la primera siempre termina. `NestEffort`
  queda para pruebas y bancos. La sesión guarda `NestTimeSeconds`; la clave vieja `NestEffort` se ignora.
- Validado: app con el motor real 489/489; núcleo 1048 + 3 omitidas de siempre; regresiones de corte común
  idénticas a `main`; Hodgson x150 Fast y Normal con el mismo hash que `main`; cada prueba nueva vista en
  rojo contra un sabotaje. `TimedNestRoundsFixture` comprueba que las rondas 1-3 son los anidados
  Fast/Normal/Best y tarda unos 3 min.
- App real con la demo de 57 piezas: Fast 20 s, Normal 236 s. A 15 s se pasa y entrega Fast; a 2 min corta
  la Normal a las 2:00 justas y entrega Fast; a 5 min gana la Normal. Cancelar sigue tirando todo.
  Hodgson x150 a 15 s se pasa y entrega Fast (72 s, 0 cortes compartidos).
- Patrón en filas (`RasterNest/RowPatternPacker.cs`): ronda 0 de `NestForTime` para un solo dibujo casi
  rectangular con common line. test2 ×800, 0/90, lámina 120x60 ilimitada: el patrón tarda 5.6 s y la app
  entrega a los 15 s (Fast se corta en el límite). Da 31 láminas, 26 por lámina, 0 piezas sin corte compartido
  y 865 cortes. Fast solo tarda 53-57 s, con 930 cortes. Las rondas se ordenan además por "menos piezas
  sueltas", antes del corte compartido. La etapa final lee cada DXF una vez: mismos hash, unos 2.5 s menos.
  Validado: 9 sabotajes en rojo; bancos y hash de Hodgson Fast/Normal iguales a `main`; app real 0:15;
  app con el motor real 510/510, núcleo 1048 + 3 omitidas.
- 2026-10-30, misma rama: el patrón también lleva **barras ranuradas** (Hodgson). Un lado puede tener varias
  aristas rectas (todas con pareja del mismo tramo) y, si un lado solo casa con la copia girada 180°, la banda o la
  rejilla alternan las dos. La regla del 97 % de la caja se sustituyó: o ninguna copia entra en la caja de otra más
  de 1/4 del lado corto (suma de Minkowski, `CopiesKeepToTheirBoxes`), o todo el trabajo cabe en una lámina de la
  medida más chica. Hodgson entra por la segunda: los dedos de sus puntas sí caben en las ranuras de una copia.
  Hodgson x150, 4 giros, una lámina 120x60, a 15 s: patrón 3.2 s, entrega a los 15.1 s (app real 0:16), 0 piezas sueltas,
  389 aristas y 1404.5 in (Fast: 73 s y 0; Normal: 166 s y 1181 in). Pruebas públicas con una barra inventada;
  la nativa es `HodgsonRowPatternJobFixture`. Un trabajo de Hodgson que no cabe en una lámina sigue como antes.
  Suites: núcleo 1048 + 3 omitidas; app con el motor real 519/520. Falló `NestCardFixture` por una carrera que
  ya estaba en `main`: `NestReportLayoutFixture` pone `Strings.Culture` en español mientras otras clases corren en
  paralelo. Sola pasa.
- 2026-10-30, misma rama: **la compactación ya no tira el plan de corte común.** Si tras compactar los grupos falla
  alguna de las 4 pruebas, `MapAndCompact` vuelve primero a la posición del planificador (mismas 4 pruebas y al menos
  tantas piezas en la lámina como la base) y solo si no pasa vuelve a la base. Causa medida: la compactación rígida
  dejaba dos grupos de Hodgson a 0.2498-0.2499 in y la holgura exacta rechazaba la lámina entera. Fast x150: 0 → 211
  aristas, 1001 in, 1 suelta; Normal x150: 299 → 306 aristas, 1208.6 in. Hodgson x600 a 15 s (2 láminas, sin
  patrón): 0 → 636 aristas, 23 sueltas; sigue tardando 15 min. Bancos test2 x16-27 y mixto idénticos. Prueba nativa
  `HodgsonFastCommonLineJobFixture`; el conteo de pares inseguros de las pruebas de Hodgson acepta las juntas en T
  como el validador. Suites: núcleo 1048 + 3 omitidas; app con el motor real 521/521. App real x600 a 15 s: 15:37,
  2 láminas, 600 colocadas, 636 cortes compartidos.
- 2026-10-30, misma rama: **el patrón también llena varias láminas** si la pieza ocupa al menos el 80 % de su caja
  (`RowPatternPacker.FillsItsBoxes`), aunque sus copias se metan en la caja de otra: esa copia solo puede recuperar el
  aire de la caja. Hodgson ocupa ~84 %; una U de muesca honda (72 %) sigue con la búsqueda. Hodgson x600 a 15 s,
  lámina 120x60 ilimitada: de 903 s a 11.3 s (app real 0:13), 2 láminas (485 + 115 en vez de 419 + 181), de 636 a
  1286 aristas, de 23 a 0 piezas sueltas. Bancos test2 x16-27 y mixto y hash Fast/Normal de Hodgson idénticos.
  Sabotaje sin la regla: la U poco honda x200 y la nativa x600 en rojo (897 s, hash viejo). Además, si el límite
  cortaba la ronda mientras el planificador ordenaba su haz, `List.Sort` envolvía la cancelación en una
  `InvalidOperationException` y el anidado fallaba; `NestRounds.IsCancellation` ya la reconoce (prueba
  `ARoundCutWhileSortingIsDiscardedWhole`, roja sin el arreglo). Suites: núcleo 1048 + 3 omitidas; app con el motor
  real 527/527.
- 2026-10-30, misma rama: **el largo de lámina va antes del corte compartido.** Los tres sitios que eligen
  (`RowPatternPacker.Ranks`, `SparrowNestService.PickBest`, `NestRounds.IsBetter`) ordenan: colocadas → menos sueltas
  → menos largo → más corte compartido. `PickBest` cuenta ahora las sueltas: sin eso la Normal dejaba dos barras de
  Hodgson solas por 1.57 in. Hodgson x150 a 15 s: largo 50.04 → 37.39 in, 389 → 375 aristas (1367.6 in), 0 sueltas;
  app real 0:15 con retazo de 82.17 × 60 in. x600: última lámina 50.04 → 29.08 in, 1286 → 1277 aristas. Fast x150:
  43.64 in y 0 sueltas (antes 43.40 in y 1). Normal x150: 41.78 in y 283 aristas (antes 42.32 in y 306). Bancos test2
  x16-27 y mixto idénticos. Hash nuevos: patrón x150 `4E51B6D1DCF5E507`, x600 `5BC082DB16F494B0`, test2 x800
  `502365B12D92070F`, Fast `B8F17693F1C4C3BE`, Normal `BD42FA3E675B9ED6`. Sabotaje de cada orden y de la regla de
  sueltas en rojo. Suites: núcleo 1048 + 3 omitidas; nativas 56/56; app con el motor real 526/527, falla
  `NestingLabelCommonCuttingFixture` por la misma carrera de `Strings.Culture`; sola pasa.
- 2026-10-31, misma rama: **trabajos de muchas piezas distintas.** Banco nuevo `MixedJobBenchFixture` (nativo,
  opcional: `SHEETNEST_MIXED_BENCH=1`, `SHEETNEST_MIXED_BENCH_JOBS=A,C`, `_TIME`, `_EFFORT`), seis trabajos A-F con
  piezas inventadas (las de la demo, un marco con ventana y cuadritos), lámina 120x60 ilimitada. Cambios:
  (1) la compactación solo pasa a Clipper los contornos cuya caja toca la otra pieza (mismos hash; C a 30
  iteraciones de 22.0 a 13.8 s); (2) ronda 0 rápida de 150 iteraciones (`QuickSearchIterations`) para todo trabajo
  que el patrón no toma; Fast ya se corta y solo empieza si queda el doble de lo que tardó la apertura;
  (3) huecos: cada lámina rellena los huecos de sus marcos antes de `DeferUnfit`, `HoldBackForHoles` no manda al
  motor lo que entrará en ellos, y dentro de un hueco se respeta la holgura completa (`HoleSlack`; además
  `RasterCompact.Inflate` encoge también los huecos: la compactación dejaba cuadritos a 0.126 in del marco);
  (4) `SelectBatch` toma un dibujo de cada en turno, y los de common line van enteros en su turno. A 15 s (antes y
  después): A 20.9 y 15.1 s (91.81 y 96.31 in); B 35.8 y 14.7 s, 4 y 3 sueltas; C 153 y 36 s; D 64.5 y 8.3 s, 2 y
  1 lámina; E 233 y 80 s, 18 y 8 sueltas; F 301 y 44 s, 4 y 3 láminas. En Fast: C última lámina 48.06 y 41.93 in,
  D 2 y 1 lámina, F 4 y 3, E 18 y 43 sueltas (el corte común entre dibujos distintos depende de dónde los deja el
  motor). Sin cambios: test2 x16-27, Hodgson Fast/Normal y los trabajos del patrón; el mixto termina igual (30
  colocadas, 36 aristas) y su registro ya no tiene el choque de holgura del acomodo base. Probado y descartado:
  ordenar los planes del planificador por piezas soldadas (ningún banco cambió). Pruebas nuevas, todas rojas contra
  un sabotaje: `AfterTheQuickSearchRoundOneNeedsTwiceItsTimeLeft`, `PartsGoInsideTheFramesOnTheirSheetAtTheFullSpacing`
  (3 casos), `PartsThatFitInTheFramesDoNotLengthenTheSheet` y `TheFirstSheetOfAMixedJobGetsSomeOfEachDrawing` (por
  dibujo: 4 láminas; en turno: 3). Suites: núcleo 1048 + 3 omitidas; app con el motor real 533 + 1 omitida (el
  banco opcional); nativas 80/80. App real a 15 s: demo 0:15, 57/57; demo con common line 0:15 con aviso de que se
  pasó (su apertura tarda unos 15 s), 13 cortes compartidos. Pendiente: un trabajo de varias láminas sigue pasando
  de 15 s (E 80 s, casi todo en el planificador de corte común); bloques soldados antes del motor para el corte
  compartido entre dibujos distintos.
- 2026-11-01, misma rama: **15 s sin menú de tiempo.** Se quitaron la flecha de ANIDAR y la fila de tiempo de
  Configuración avanzada; la app siempre anida con `NestRounds.DefaultSeconds`. Una sesión con `NestTimeSeconds` o
  `NestEffort` abre igual. Banco de 30 trabajos reales (DXF de prueba, una mesa de soldar y un rack en mm; fuera del
  repositorio) con `LargeCommonLineJobBenchFixture` y sus opciones nuevas `SHEETNEST_BENCH_SHEET=WxH`, `_SHEETS=n` y
  `_UNITS=mm`: de 17 a 23 entregados en 15 s; ninguno con más láminas, menos colocadas ni más sueltas. Mismo resultado:
  la lectura del DXF busca la siguiente pieza del contorno con una rejilla (un panel con cientos de agujeros, de 318 s a
  0.14 s); `FillHoles` descarta antes los huecos chicos y crece cada contorno una vez por ángulo; `Overlaps` solo pasa a
  Clipper los contornos que tocan; el planificador prueba primero la pieza que lo frenó la última vez. Con otro
  resultado: sparrow acepta `--max-evals`, un tope de trabajo repetible (parche 2/2 en
  `SparrowEngine/sheetnest-sparrow.patch`, `sparrow.exe` nuevo; sin el tope da lo mismo que el viejo). La apertura da a
  cada lámina 3 M evaluaciones entre las láminas estimadas, de 0.6 M a 1.5 M (`OpeningEvals`, `EstimatedSheets`). En la
  lámina que se lleva el resto, la pieza que se devuelve prueba tras el final del acomodo (`TryReseatAtTail`) y, si no
  cabe, al final de una lámina anterior (`FoldLastSheetBack`): cinco mesas y F daban una lámina de más. Inventados a
  15 s: C de 36 a 20 s, F de 44 a 11 s con 3 láminas, E de 80 a 33 s pero de 8 a 30 sueltas (a decidir por Manuel);
  A, B y D igual. Fast A-F, test2 x16-27, mixto, Hodgson Fast/Normal y el patrón: mismo hash. Siguen por encima de 15 s
  cinco con common line (el planificador prueba miles de candidatos que fallan por holgura: 53 000 en cinco mesas) y
  los dos más grandes sin common line (unos 30 s). Pruebas nuevas, rojas contra un sabotaje o el código viejo:
  `DxfParserChainingFixture`, `OpeningWorkFixture`, `SparrowWorkBudgetFixture`, `TailReseatFixture` y
  `NestingTimeSessionFixture`. Suites: núcleo 1049 + 3 omitidas; nativas 93/93; app con el motor real 538/539 (1 omitida).
- 2026-11-01 (tarde), misma rama: **objetivo de aprovechamiento.** Configuración avanzada > Anidado tiene la fila
  "Objetivo de aprovechamiento (%)": 90 por defecto, 0 = apagado (sesión `MaterialTargetPercent`, -1 = de fábrica).
  Al terminar la apertura y cada ronda, si el mejor anidado hasta ahora tiene todas las piezas colocadas y llega al
  objetivo, `NestRounds.Run` para con `TargetReached` y la barra dice "objetivo de 90 % alcanzado (92 %)", con el %
  truncado. La medida (`NestRounds.UsedUtilization`) es el área neta de las piezas (sin agujeros) entre el espacio
  usado: Σ ancho × alto de las láminas con piezas, menos el retazo que SheetNest corta de la última. Es el mismo
  rectángulo o L que dibujan el visor y la exportación (`OffcutGeometry.CutPositions` y `RemnantRects`, con los ajustes
  de retazo de la sesión: dirección, separación, mínimo y máximo). Las demás láminas cuentan enteras, una tira más
  angosta que el mínimo no es retazo y con el retazo apagado cuenta la lámina entera. En el motor el valor por defecto
  es apagado (`NestForTime(materialTarget: 0, offcut: null)`), así que pruebas y hash no cambian; la app pasa el
  objetivo de la sesión y `CurrentOffcutOptions()`. Bancos: `SHEETNEST_BENCH_TARGET` y `SHEETNEST_MIXED_BENCH_TARGET`;
  para el retazo, `SHEETNEST_BENCH_OFFCUT` y `SHEETNEST_MIXED_BENCH_OFFCUT` (`end`, `side`, `both` o `auto`, con las
  medidas de `OffcutOptions.Defaults`; sin la variable, apagado). Las líneas ROUND, TIMED y MIXED muestran `usage=`.
  Medido al 90 % con el retazo en automático: los 30 trabajos reales y A-F dan el mismo hash que sin objetivo y solo
  uno para antes (un soporte con common line, de 15.1 a 5.1 s, mismo anidado, 92.9 %). Los demás usan entre 23 y 89 %
  (los dos más cerca, de una sola pieza, en 89.3 y 89.5 %); A-F, entre 61 y 73 %. App real: la demo al 90 % sigue en
  0:15; con 65 % entrega el mismo anidado en 0:03 y dice 67 %, que es lo que dan sus piezas (54.8 % de la lámina)
  contra la lámina menos el retazo dibujado de 23.25 × 60 in. Pruebas, rojas contra 14 sabotajes (7 del objetivo y 7
  de la medida con retazo): `NestRoundsFixture` (solo se resta el retazo dibujado, las láminas anteriores cuentan
  enteras, una tira bajo el mínimo no es retazo, la L se resta entera, apagado cuenta la lámina entera) y
  `MaterialTargetSessionFixture`. Suites: núcleo 1049 + 3 omitidas; nativas 105/105; app con el motor real 555 + 1
  omitida.
- 2026-11-01 (noche), misma rama, sin commit: **.NET 10.** Se migró y midió primero en una copia aparte (rama `net10`,
  git worktree en `C:/Users/rosam/source/repos/DeepNestSharp-net10`) y Manuel decidió adoptarlo; los mismos 15
  archivos se pasaron aquí, más BUILDING.md y la ruta del motor en COMMON_LINE_IMPLEMENTATION.md. Los 10 proyectos pasan de
  `net6.0` a `net10.0` (con `-windows` donde ya lo tenían); `GpuNestLab` sigue en `net8.0`. Paquetes: se quita
  `System.Text.Json` (viene con .NET 10); `System.Configuration.ConfigurationManager` y
  `Microsoft.Extensions.DependencyInjection`/`Hosting` pasan a 10.0.14; `System.Drawing.Common` 10.0.14 queda explícito
  porque `GeneticSharp.Extensions` pide un 4.5 preview con aviso crítico que antes subía ConfigurationManager 6.
  `dotnet list package --vulnerable --include-transitive` sale limpio en la app y NestBench; los proyectos de pruebas
  aún arrastran `Newtonsoft.Json` 9.0.1 por el `Microsoft.NET.Test.Sdk` viejo (no se distribuye). `package.ps1`,
  `Localization/README.md`, este archivo y los workflows (`dotnet-version: 10.0.x`) usan la ruta nueva; CI sin probar.
  SDK 10.0.403 instalado junto al 8, sin global.json. Mismo resultado: núcleo 1049 + 3 omitidas, nativas 105/105, app
  con el motor real 555 + 1 omitida, Minkowski 5/5, mismos hashes en la regresión de dos bancos y en el set nativo.
  Velocidad con el mismo sparrow.exe, en orden 6, 10, 6, 10 y media de dos corridas por lado (30 trabajos reales a 15 s
  con objetivo 90 y retazo automático, A-F y trabajo fijo): las cuatro corridas dan el mismo hash en todos. Trabajo
  fijo de 150 piezas con common line: Fast -14 % y Normal -18 %; regresión test2 -5 %. Mixtos: B -51 %, E -42 % (34 a
  20 s), C y F -10 %. Reales: el más grande con patrón -25 % (11.4 a 8.5 s), un mixto con common line -19 % (54 a 44 s),
  el rack de cinco juegos sin common line -14 %, la mesa de cinco juegos con common line -7 %. Los que llegan a 15 s
  siguen igual; unos pocos van entre 4 y 5 % más lentos (la mesa de un juego con common line, de 91 a 95 s). Un mixto
  con common line para ahora a los 6 s en vez de 15 s con el mismo anidado: su apertura tarda 5.7 s en vez de 4.7 y la
  regla de tiempo ya no ve sitio para otra ronda. App: ventana en 1 s como antes, demo en 0:15 con 54.8 %. Publish
  autocontenido win-x64: 153 MB (la 1.1.12 instalada, sin la carpeta 3D, ocupa 169 MB), arranca y anida.
- 2026-11-01 (noche), misma rama, sin commit: **un dibujo abierto en el programa de CAD ya comparte cortes.** Un dibujo
  de prueba daba 0 cortes compartidos y todas las piezas sueltas con el aviso "topology invalid". La causa: el programa
  de CAD lo tenía abierto y las tres lecturas de la geometría exacta (`DxfContourCatalog.LoadCore` y
  `ReadSnapshotCore`, `CommonLineGeometryResolver.GetSourceOuter`) usaban `File.ReadAllBytes`, que no comparte el
  archivo; importar sí funcionaba. Ahora usan `DxfPartSplitter.ReadAllBytesShared`. Pruebas, rojas contra el código
  viejo y contra cada arreglo deshecho: `LoadAndSnapshotReadADrawingStillOpenInTheCadProgram`,
  `TheSheetStillSharesEveryCutWhileTheDrawingIsOpenInTheCadProgram` y
  `SourceOuterIsReadWhileTheDrawingIsOpenInTheCadProgram`. El aviso sigue diciendo "topology" para estos fallos.
  Suites: núcleo 1050 + 3 omitidas (falla 1, el `ClipperFixture` de tiempo de siempre), nativas 106/106, app con el
  motor real 556 + 1 omitida; regresión de dos bancos y hash de Hodgson iguales; 26 de los 30 trabajos reales con el
  mismo hash (el banco se canceló antes de los 4 últimos).
- 2026-11-01 (noche), misma rama, sin commit: **sin tope de tiempo.** La app anida con `NestRounds.NoLimit`: para al
  llegar al objetivo de aprovechamiento, tras dos rondas seguidas sin mejora o con el botón Detener. Hasta que termina
  el primer anidado el botón es Cancelar (tira todo); luego pasa a Detener, que tira la ronda en curso y entrega el
  mejor (`NestRoundsStop.Stopped`, la barra dice "detenido"). Mientras anida, la barra dice "objetivo 90 %, mejor hasta
  ahora 71 %", con la medida del objetivo (sin el retazo). Ya no hay "de 0:15" ni "se pasó del límite".
  `DefaultSeconds` queda para bancos y pruebas, que no cambian. `NestRounds.Run` toma `stop` y `newBest`. Pruebas
  nuevas en `NestRoundsFixture`, rojas contra 6 sabotajes. App real: el dibujo de 19 piezas termina solo en 0:59
  (4 rondas, gana la 2, 19 en una lámina, todas comparten); la demo, Detener a los 30 s entrega en menos de 1 s; Cancelar
  en la apertura no deja nada. Suites: app sin motor 544 + 13 omitidas, traducciones 5/5. Pendiente: un trabajo grande
  sin patrón puede tardar mucho (Hodgson ×150 con common line sin patrón, minutos).
- 2026-11-01 (noche), misma rama, sin commit: **tiempo por lámina.** Configuración avanzada > Anidado tiene la fila
  "Tiempo por lámina (s)", 0 a 600, de fábrica 0 = sin límite (sesión `NestSecondsPerSheet`, -1 = de fábrica). Con un
  valor, el trabajo tiene ese tiempo por cada lámina que se espera que use (`SparrowNestService.TimeLimit` con
  `EstimatedSheets`, la misma estimación que reparte la apertura) y corre el `NestRounds.Run` con tiempo de siempre: la
  ronda cortada se tira, la primera siempre termina, no se empieza una ronda que no cabe. La barra dice "Anidando… 0:07
  de 0:30 · ronda 2" y, si la primera ronda se pasa, "la primera ronda pasó del límite de 0:30". Bancos y pruebas no lo
  pasan: mismos hash. Pruebas: `OpeningWorkFixture` (tiempo × láminas, al menos una, 0 = sin límite) y
  `NestSecondsPerSheetSessionFixture`, rojas contra 5 sabotajes. App real: la fila sale en la ventana; demo a 10 s
  entrega la apertura a los 3 s (Fast no cabía); a 30 s corta Fast a las 0:30 y entrega la apertura; el dibujo de 19
  piezas a 2 s entrega a las 0:02, 19 en una lámina compartiendo. Visto: tras un corte por tiempo, los procesos del
  motor de la ronda tirada tardan unos 20 s en cerrarse. Suites: app sin motor 557 + 13 omitidas, traducciones 5/5.
- 2026-11-02, rama `cpp-compact` (desde `nest-time`), sin push: **piloto del núcleo nativo en C++.** `native/`
  (CMake, C++17) compila `sheetnest_core.dll` con MSVC (C++ estático, ucrt del sistema, `/fp:precise`) y `.so` con
  gcc en Linux. Lleva Clipper 6.4.2 C++ sin cambios (`native/third_party/clipper`, Boost) y `snc_compact`, traducción
  línea a línea de `RasterCompact.Compact` con API en C plana. `RasterCompact.Compact` llama al nativo
  (`NativeCompact.cs`) y, si la DLL falta, no carga o rechaza la entrada, corre el C# de siempre (`CompactManaged`);
  `SHEETNEST_NATIVE=off` fuerza el C#. La DLL va precompilada en `NativeDlls/`, como las de Minkowski; CI no compila
  nativo. Paridad: `RasterCompactParityFixture`, 10 casos inventados, posiciones idénticas al último bit; 6 de 7
  sabotajes del C++ en rojo (un sort inestable no se nota: MSVC ordena estable los rangos cortos). Suites de la app
  con y sin `SHEETNEST_NATIVE=off`: 579 + 14 omitidas. Medido en 4 trabajos reales a Fast: mismo hash, compactación
  de 1.6× a 7× más rápida; el total casi no cambia (manda sparrow o el planificador).
  Segundo paso, misma rama: `snc_too_close`, traducción de `PlacementCollision.TooClose` (`collision.cpp`; los
  ayudantes compartidos en `clipper_util.hpp`), con `NativeCollision.cs` y `TooCloseManaged` de respaldo; el interruptor
  y la DLL faltante viven en `NativeCore.cs`. Paridad: `PlacementCollisionParityFixture`, 9000 pares inventados casi
  todos al borde de la holgura, mismo veredicto; 5 de 7 sabotajes en rojo (los otros dos no pueden cambiar el veredicto
  o solo pesan en una mordida cóncava al límite). En tri100-cl a Fast: mismo hash, compactación 4.0 → 0.8 s, pero la
  holgura (`unfit`) solo 2.1 → 2.0 s y el planificador igual (~30 s): su costo está fuera de `TooClose` (copias de
  polígono y revalidaciones). Suites de la app con el núcleo: 587 + 14 omitidas; con `SHEETNEST_NATIVE=off` igual,
  salvo la carrera conocida de `Strings.Culture` que a veces tumba `CommonLineFallbackMessageFixture`.
- La tabla de abajo no coincide con `main` de hoy. Medido con Fast: mixto 30 colocadas y 36 aristas;
  test2 x16/x17/x24/x25 con 26/27/41/42 aristas; x26 con 29 aristas, `isolated=1`, cadena 19; x27 con
  26 colocadas y 28 aristas. Comparar contra una línea base medida, no contra la tabla.

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
