# Common line: LINE and circular ARC expansion

## Hodgson performance correction (2026-09-04)

The slowdown was in managed common-line processing after Sparrow finished. Hodgson's
two semicircles produce about 508 validation segments at the existing tolerance;
the previous outside-cap loops consulted seam relationships before rejecting distant
segments. Repeated validation of alternative plans amplified that cost. The old
25-copy diagnostic was interrupted after nearly three minutes without a result.

The correction preserves candidate counts, orientations, beam widths, native seeds,
normal spacing and geometry tolerance:

- `CommonEdgeClearance` tests distance before relationship membership, uses an
  X-sorted/Y-filtered segment index retaining original contour positions, and builds
  incident-edge, pair and connectivity indexes once per acceptance pass.
- A bounded, search-local pair-verdict cache reuses identical validation work. Its
  exact keys include immutable geometry identity, absolute transforms, settings,
  consumed intervals, fingerprints, incident relationships and connectivity. It
  never quantizes poses or bypasses source revalidation. Geometry is not cached
  across nesting operations.
- Source catalogs are captured once per seed race and shared immutably. Each seed
  owns its lookup dictionary. Record factories accept these catalogs instead of
  reopening the DXF four times per record. Final acceptance and export still
  validate the source again.
- The cluster resolver rejects disjoint segment bounding boxes before expensive
  ring intersection predicates; the exact predicates remain unchanged.
- Cancellation now reaches the common-line search, clearance and cluster/network
  validation. The planner restores original references, positions, rotations and
  IDs before propagating cancellation or source-change failure.
- Per-seed diagnostics include stage durations, captured catalog counts and
  pair-cache hits/misses. Timings summed across concurrent seeds are not wall time.

Release benchmark on this workstation, using the original Desktop DXF, Fast,
42 x 31 inch sheet, 0.25 inch normal spacing/margin, 90/270 degree rotations,
0.010 inch common gap and 0.0001 inch geometry tolerance:

| Copies | End-to-end wall time | Accepted CommonEdge records |
| --- | ---: | ---: |
| 15 | 1.9 s | 9 |
| 25 | 4.7 s | 22 |
| 50 | 25.7 s | 0 |

The 50-copy case is a stress test of rejected alternatives, not evidence that all
50 parts common-cut successfully. The existing material/safety rules still reject
its proposed common plans. Sheet dimensions affect Sparrow's own runtime; these
numbers are not a promise for other stock sizes or jobs.

`CommonLinePerformanceSafetyFixture` adds 11 cases: exhaustive/indexed distance
equivalence in inches and mm, substantial candidate pruning, cancellation/rollback
at six stages, 100 source-backed record creations while the source is exclusively
locked, changed-source rejection and cache invalidation as geometry/settings/spans
change. The Hodgson native fixture covers 15, 25 and 50 copies and now reports a
real skip when the engine is absent (or fails when the engine is required).

Dedicated native/performance verification in PowerShell:

```powershell
$env:SPARROW_EXE = (Resolve-Path 'DeepNestSharp/bin/Release/net6.0-windows/sparrow.exe').Path
$env:SHEETNEST_REQUIRE_SPARROW = '1'
$env:SHEETNEST_HODGSON_DXF = 'C:\Users\rosam\Desktop\dxf\Hodgson.dxf'
$env:SHEETNEST_ASSERT_COMMON_LINE_PERF = '1'
dotnet test DeepNestSharp.CiTests/DeepNestSharp.CiTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~RealSparrowPlacesRequested|FullyQualifiedName~CommonLinePerformanceSafetyFixture' --logger 'console;verbosity=detailed'
```

The optional performance assertions enforce common-line processing below 10 seconds
per seed for 15/25 copies and below 30 seconds for 50. They never truncate the search.

Full default suites after the correction: application 471 passed / 1 skipped native
theory; core 967 passed / 3 skipped. The three native Hodgson cases were also run
explicitly with the required engine. Existing unrelated native fixtures still have
their original environment-gated behavior; the suite totals alone do not demonstrate
that every native-engine scenario ran.

## Scope

The common-line planner considers straight sides beyond the longest side, including
triangles, oblique sides, concave parts and partial straight spans. It also proposes
exact circular-arc matches: compatible opposite-facing arcs become concentric with
their radii separated by the configured gap. Two arbitrary arcs are not necessarily
compatible. Allowed part rotations still constrain the search.

The bounded search retains alternatives by occupied area, shared length and
direction. Final acceptance is material-first and cannot enlarge the baseline
material footprint. This is not an exhaustive optimizer or a claim of parity with
Radan, SigmaNest or TruTops.

## Architecture and changed modules

| Responsibility | Files |
| --- | --- |
| Source geometry and immutable DXF snapshots | `DeepNestLib.Core/IO/DxfContourCatalog.cs` |
| Persistent relationships and exact primitives | `DeepNestLib.Core/Placement/CommonLineGeometryModel.cs`, `DxfResolvedBoundarySpan.cs`, `DxfCommonLineSpanResolver.cs` |
| Arc placement proposals | `DeepNestLib.Core/Placement/DxfCommonLineArcCandidateFinder.cs` |
| Cluster boundaries and validation | `DeepNestLib.Core/Placement/CommonLineGeometryResolver.cs` |
| Shared-cut graph | `DeepNestLib.Core/Placement/CommonLineCurveNetwork.cs` |
| Export | `DeepNestLib.Core/IO/DxfExporter.cs` |
| Nesting, candidate selection and clearance | `DeepNestSharp/RasterNest/SparrowNestService.cs`, `CommonEdgeClearance.cs`, `CommonCuttingCompatibility.cs`, `CommonLineFallbackNotice.cs`, `RasterCompact.cs`, `RasterPartInfo.cs` |
| Physical defaults and settings contracts | `DeepNestLib.Core/Placement/CommonLineDefaults.cs`, `ISvgNestConfig.cs`, `SvgNestConfig.cs`, `TestSvgNestConfig.cs` |
| Project persistence | `DeepNestLib.Core/NestProject/IProjectInfo.cs`, `ProjectInfo.cs`, `SvgNestConfigJsonConverter.cs` |
| Observable settings | `DeepNestSharp.Domain/Models/ObservableProjectInfo.cs`, `ObservableSvgNestConfig.cs` |
| Session and UI | `DeepNestSharp/SessionState.cs`, `Ui/Views/AdvancedSettingsWindow.xaml`, `AdvancedSettingsWindow.xaml.cs`, `MainWindow.xaml.cs`, `Ui/UserControls/DxfViewer.xaml.cs` |

The existing polygon/Clipper nesting infrastructure is retained. Exact DXF geometry
is an additional source for common-line placement and export, not a replacement
nesting engine. Common Line OFF retains the ordinary nesting path.

## CommonEdge and intentional placements

Schema-two `CommonEdge` records contain part IDs, source-contour/primitive indices,
parametric intervals, source and contour fingerprints, gap and an exact center
curve. A center curve is a LINE or circular ARC. Legacy full-line records remain
readable and are revalidated rather than blindly trusted.

The planner records accepted relationships when constructing a placement. Mere
proximity is not authorization for reduced clearance. NormalPartGap still applies
to unrelated geometry. Local endpoint clearance checks allow adjoining branches
to diverge back to normal spacing, reject re-entry/parallel continuation, and
account for acute straight corners and tangent arc recovery.

Source catalogs are cached from immutable file contents, including tolerance in
the cache key. Pose enumeration reuses those snapshots. Final acceptance and export
revalidate fingerprints, so a source edit cannot silently reuse an obsolete seam.

## Clusters and closed boundaries

Union-find builds connected components from validated CommonEdges. Each cluster
retains the unshared source spans and reconstructs endpoint connectors in a boundary
graph. Source arcs remain analytic; curved source geometry is not replaced with
straight chords in the exported boundary.

The graph must produce closed cycles. Containment classifies the external envelope
as CUT_OUTER and enclosed cluster voids as CUT_INNER; original part holes are also
preserved. Ambiguous touching cycles, self-intersections, disjoint external roots
or dangling corners invalidate the result. Individual open part outlines are never
used as a fallback export.

## Common network and DXF contract

The shared path lies halfway between nominal source edges. For arcs this is the
concentric midpoint radius. The network splits LINE/LINE, LINE/ARC and ARC/ARC
intersections, deduplicates overlapping spans and canonicalizes nearby nodes within
GeometryTolerance. Junction repair requires supporting geometry; unrelated nearby
endpoints must not be bridged automatically.

SheetCam operation order:

1. `01_CUT_INNER`: closed inner contours.
2. `02_COMMON_LINE`: shared paths, ON PATH / NO OFFSET.
3. `03_CUT_OUTER`: completely closed external cluster boundaries and normal outers.

Common arcs use open bulged LWPOLYLINE entities so their endpoints use exactly the
same coordinates as adjacent network entities. No original shared segment remains
under the common cut. SheetNest generates no common-path lead-in or lead-out and
does not depend on SheetCam duplicate removal or contour joining.

## Settings and units

`CommonLineGapInches` is the canonical persisted physical value in project/session
settings. `CommonLineDefaults.DefaultGapInches` is the single default, 0.010 inches
(0.254 mm). Advanced Settings displays/converts the active inch/mm value and offers
Reset to Default. Algorithms consume the active converted setting.

GeometryTolerance is independent: its default is 0.0001 inches, converted to active
units. It is not a user-facing kerf or gap setting. Cluster sizing supports Auto
(bounded to at most 100 parts) or a manual 2..500-part limit.

The existing export format may declare unitless DXF coordinates. Select matching
project/import units in SheetCam; the gap must never be used to infer file units.

## Safety validation

Validation checks finite geometry, active gap, facing direction, valid parametric
spans, current source fingerprints, placement collisions, relationship conflicts,
closed/simple boundary cycles, nonzero primitives, duplicate seams, seam/outer
overlap and exact junction connectivity. Common-cluster clearance checks use the
exact source boundary with a conservative bounded approximation for distance tests.

Curve sampling has explicit per-curve and aggregate limits. Exceeding a validation
budget fails closed; it does not silently switch to a less accurate export. An
invalid common placement is rejected or the export is blocked, never exported as
two normal cuts while retaining common-line spacing. Recorded cluster members are
locked against individual manual movement; re-nest to change their arrangement.

## Automated coverage and verification

New fixtures: `DxfContourCatalogFixture`, `CommonLineCurveNetworkFixture`,
`CommonLineGeometryModelV2Fixture`, `CommonLineOuterGuardFixture`,
`CommonLineValidationBudgetFixture`, `DxfCommonLineSpanResolverFixture`,
`ExactArcCommonLineGeometryFixture`, `ExactArcCommonLinePlannerFixture`.

Expanded fixtures: `CommonLineDxfExportFixture`, `ProjectInfoSerializationFixture`,
`CommonLineGeometryResolverFixture`, `CommonCuttingClearanceFixture`,
`CommonLineGapSettingsFixture`, `NestingEffortSessionFixture`,
`ProductionPlanSheetsFixture`, `DirectCommonLineClusterFixture`,
`DirectCommonLineOrientationSafetyFixture`, `HodgsonDirectCommonLineFixture`,
`ObliqueCommonCutFixture`, `Test2DirectCommonLineSnapFixture`,
`SessionStateCompatibilityFixture`.

Coverage includes rectangle chains/grids, triangles, short/oblique sides, rotations,
partial spans, exact arcs, T/+ intersections, holes/cluster voids, gap values,
inch/mm equivalence, persistence, changed DXF contents, unsafe external neighbors,
acute-corner clearance recovery and rejected malformed exports.

Verified on 2026-09-04:

- `dotnet test DeepNestSharp.CiTests/DeepNestSharp.CiTests.csproj --no-restore --verbosity quiet -p:WarningLevel=0`: 461 passed.
- `dotnet test DeepNestLib.CiTests/DeepNestLib.CiTests.csproj --no-restore --verbosity quiet -p:WarningLevel=0`: 967 passed, 3 skipped.
- `dotnet build DeepNestSharp/DeepNestSharp.csproj -c Release --no-restore --verbosity quiet -p:WarningLevel=0`: succeeded, zero errors.

Existing build warnings remain for unsupported .NET 6, System.Text.Json 6.0.5
(NU1903) and analyzer version mismatch. Dependency migration is separate work.

## Remaining limitations

- ELLIPSE and SPLINE are not eligible common seams; unsupported curved cluster
  boundaries are rejected instead of approximated into cutting geometry.
- Hole-to-hole and hole-to-outside common cutting are not supported.
- Search is bounded and may miss valid arrangements, particularly complex curved
  endpoint transitions. Conservative rejection is preferable to ambiguous cuts.
- Thermal optimization, material-specific kerf tables, chain cutting, pierce
  minimization and advanced leads are not implemented.
- Automated geometry/export tests do not replace a SheetCam import and physical
  cutting trial. No such production trial is claimed here.
