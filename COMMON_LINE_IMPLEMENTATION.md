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
$env:SPARROW_EXE = (Resolve-Path 'DeepNestSharp/bin/Release/net10.0-windows/sparrow.exe').Path
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

## Row pattern for one drawing laid out by its box (2026-10-29, 2026-10-30)

A timed nest (`NestForTime`) first tries a row pattern (`RowPatternPacker`) when the
job is a single drawing set to share cuts with itself and its opposite straight sides
meet in full. Copies go in bands, side by side at the common-line gap. Bands of the same
orientation and length close up into a grid; any other bands keep the normal spacing.

Laying a part out by its box must give little or nothing away, so one of three things
must hold:

- No two copies can push into each other's boxes by more than a quarter of the thinner
  box side both ways at once (`CopiesKeepToTheirBoxes`, a Minkowski sum per pair of
  poses). test2's rounded corners pass; a U whose arms fit into a copy's notch does not.
- The part fills at least 80 % of its box in every pose (`FillsItsBoxes`). A copy
  reaching into another's box can only win back the air in it. The Hodgson bar fills
  about 84 %: the fingers at its ends fit into the slots of a copy turned half a turn,
  but there is little air to win. A U with a notch most of its height deep fills 72 %.
- Every copy fits on one sheet of the smallest size in stock. No layout takes less, so a
  search could only win a tighter offcut.

A side may carry several straight edges (the slotted side of a bar has three). Two sides
meet only when every edge has a partner of the same span; an edge left over would sit
at the gap with no shared cut. When a side meets only the same side of a copy turned
half a turn (a flat back against a flat back), the band or the grid alternates the two
orientations. Every pattern sheet
passes the same four checks as a searched candidate (records, outline overlap,
`DirectLayoutIsValid`, topology) and then the final validation. A sheet whose grid
fails falls back to plain bands. If the nest still fails, the pattern gives nothing
and the search runs exactly as before. Fixed-effort `Nest` never uses the pattern.

The pattern is round 0 of the timed rounds. When it gives a nest, round 1 (Fast) is
held to the time like every later round and is kept only if it ranks better. Rounds
are ranked with one rule added after sheet area: fewer parts that may share a cut but
share none.

The final stage now reads each drawing once instead of once per seam end. Results are
unchanged (same hashes); it saves about 2.5 s on the job below.

Measured on this workstation with test2.dxf (7 x 36 inch, R1 corners) x800, 0 and 90
degrees, 0.25 inch spacing and border, unlimited 120 x 60 inch sheets:

| | Fast search | Timed nest, 15 s |
| --- | ---: | ---: |
| Wall time | 53 to 57 s | 5.6 s pattern, delivered at 15.1 s |
| Sheets, parts per sheet | 31, 26 (last 20) | 31, 26 (last 20) |
| Parts without a shared cut | 0 | 0 |
| Shared cuts | 930, 24,283 in | 865, 23,958 in |

Hodgson.dxf x150, four rotations, 0.25 inch spacing and border, one 120 x 60 inch
sheet:

| | Fast search | Normal search | Timed nest, 15 s |
| --- | ---: | ---: | ---: |
| Wall time | 75 s | 170 s | 3.2 s pattern, delivered at 15.1 s |
| Parts without a shared cut | 1 | 0 | 0 |
| Shared cuts | 211, 1,001 in | 306, 1,208.6 in | 389, 1,404.5 in |

The Fast and Normal columns are with the compaction fallback below. Without it Fast gave
0 shared cuts and Normal 299 (1,181 in).

The timed nest is delivered at the limit because Fast is started with the time the
pattern left and is cut there. Tests: `RowPatternPackerFixture` (no engine),
`NestRoundsFixture` (the opening round), `Test2RowPatternJobFixture` and
`HodgsonRowPatternJobFixture` (native).

## A failed compaction falls back to the planned layout (2026-10-30)

Once the planner has a plan, each cluster is compacted as one rigid body and the four
checks run again. When they failed, the sheet went straight back to the baseline with no
shared cut. On Hodgson x150 every Fast seed found a plan and lost it this way: the
compaction left two clusters 0.00010 to 0.00015 in inside their 0.25 in spacing, and the
exact clearance check rejected the sheet. A failed compaction now returns first to the
planned layout. That layout has to pass the same four checks again and keep at least as
many parts on the sheet as the baseline; otherwise the sheet goes to the baseline as
before. When the compaction passes nothing changes: test2 x16 to x27 and the mixed bench
are identical.

Hodgson x600 on unlimited 120 x 60 inch sheets, nested for 15 s, while the row pattern
still left jobs of several sheets to the search:

| | Before | After |
| --- | ---: | ---: |
| Wall time | 15 min 10 s | 15 min 3 s |
| Sheets | 2 | 2 |
| Parts without a shared cut | 600 | 23 |
| Shared cuts | 0 | 636, 3,245.7 in |

The first round still ran far past the time on this job; the next section takes it to
the row pattern. Test: `HodgsonFastCommonLineJobFixture` (native). The unsafe-pair count
in the Hodgson tests accepts two parts that come close only where their shared cuts with
one common neighbour end, a cross or a T, as the common-line checks do.

## The row pattern on several sheets (2026-10-30)

Until now the Hodgson bar reached the row pattern only when the whole job fit on one
sheet, because its copies reach into each other's boxes. A job of several sheets went to
the search, and the first round always finishes, so 15 s took 15 minutes. The pattern now
also takes a part that fills at least 80 % of its box (see the list above), on any number
of sheets. Same job as the table above:

| | Fast search (first round) | Timed nest, 15 s |
| --- | ---: | ---: |
| Wall time | 15 min 3 s | 11.3 s, pattern only |
| Sheets, parts per sheet | 2, 419 + 181 | 2, 485 + 115 |
| Parts without a shared cut | 23 | 0 |
| Shared cuts | 636, 3,245.7 in | 1,286, 5,236.3 in |

Round 1 is not started: the time left after the pattern is shorter than the pattern
took. Tests: `RowPatternPackerFixture` (a U with a shallow notch, 200 copies on four
sheets, no engine) and `HodgsonRowPatternJobFixture` (native, x600).

## The length along the sheet comes before shared cut (2026-10-30)

The operator asked for the nest to save sheet length: the parts packed to one end and the
rest of the sheet left in one piece. Three places choose between layouts that place the
same parts, and all three put more shared cut ahead of a shorter pack. Each now ranks the
shorter pack first, after the parts placed and the parts left on their own:

- `RowPatternPacker.Plan`, the pattern's splits: parts placed, parts on their own, length
  along the sheet's long side, shared cut, length along the short side.
- `SparrowNestService.PickBest`, a sheet's searched candidates: parts placed, parts on
  their own, length, shared cut, density, seed. Parts on their own are new here, counted
  as the rounds count them: parts of a drawing set to share cuts with no shared cut.
  Without that rule the Normal search left two Hodgson bars on their own to save 1.57 in.
- `NestRounds.IsBetter`, the rounds: parts placed, sheet area, parts on their own, length,
  shared cut.

Hodgson as in the tables above:

| | Shared cut first | Length first |
| --- | ---: | ---: |
| x150 timed nest, length used | 50.04 in | 37.39 in |
| x150 timed nest, shared cuts | 389, 1,404.5 in | 375, 1,367.6 in |
| x600 timed nest, length of the last sheet | 50.04 in | 29.08 in |
| x600 timed nest, shared cuts | 1,286 | 1,277 |
| x150 Fast search | 43.40 in, 1 part on its own | 43.64 in, none on its own |
| x150 Normal search | 42.32 in, 306 shared cuts | 41.78 in, 283 shared cuts |

Every part shares a cut in the timed nests on both sides. Fast gives up 0.24 in so that
no bar is left on its own. test2 x16 to x27 and the mixed bench are identical. Tests:
`RowPatternPackerFixture` (the layouts worked out by hand), `NestRoundsFixture`,
`SparrowNestServiceFixture` (`PickBest`) and `HodgsonRowPatternJobFixture` (native, the
length used).

## Jobs of many different parts (2026-10-31)

Everything above tunes jobs of one repeated drawing. A job of several drawings always goes
to the search, and four things held it back: the first round always finished, so 15 s
took 20 s on the demo and minutes on several sheets; the engine never sees a hole, so no
part went inside a frame; each sheet's batch was taken drawing by drawing in import
order; and the compaction clipped every contour of a part on every slide. Measured with
`MixedJobBenchFixture` (native, opt-in) on invented parts, unlimited 120 x 60 in sheets,
0.25 in spacing and border, four-way rotation:

- A: the demo, 57 parts of six drawings, one sheet's worth;
- B: 47 parts of six drawings, common line on the panels and plates;
- C: four times A;
- D: 12 frames with a 20 x 12 in window and 96 squares of 4 in;
- E: three times B;
- F: 190 parts of seven drawings, about three sheets.

What changed:

1. **Compaction** (`RasterCompact.Intersects`) hands Clipper only the contours whose box
   reaches the other part's box. A part with many holes was clipped whole on every
   slide. The layouts are the same to the bit (A to D on Fast keep their hashes); C at 30
   iterations went from 22.0 s to 13.8 s.
2. **A quick first round.** When the row pattern does not take the job, the timed nest
   opens with the search on 150 iterations (Fast runs 300), then Fast, Normal and Best as
   before. The opening always finishes; Fast is now cut by the time like the rounds after
   it, and starts only when twice the opening's time is left (on the bench Fast took 2.2
   to 5.4 times as long). The rounds stay repeatable: fixed iterations, never seconds.
   The measurements behind 150 are with `QuickSearchIterations`.
3. **Holes.**
   - Every sheet fills its frames' holes with the parts still to be placed, before the
     parts that do not fit are deferred. Until now only the parts left over at the end of
     the job went into holes, and on unlimited sheets nothing is left over.
   - Before the engine sees a sheet's batch, the parts the frames' holes will take are
     held back (`HoldBackForHoles`), or the engine lays them beside the frames and the
     sheet comes out longer. Each drawing with holes is filled once on its own at every
     angle it may take, and the batch holds back what its worst angle takes, times its
     copies. A part held back whose frame does not make the sheet is still to be placed.
   - Drawings set to share cuts never go into a hole, where they would be cut on their
     own.
   - The full spacing inside a hole. The hole fill fitted a part to exactly the spacing,
     on the line the sheet is judged by, and two of sixteen squares came back; each side
     now keeps 0.3 % of the spacing on top (at least 0.001 in). The compaction grew the
     outline of every part but not its holes, so a slide could leave a part inside a
     window at half the spacing: measured, squares came to rest 0.126 in from the frame.
     `RasterCompact.Inflate` now draws each hole in by the same amount.
4. **Batches in turn.** `SelectBatch` takes one of each drawing in turn, within the same
   priority and the same sheet sizes, instead of every copy of one drawing before the
   next. Drawing by drawing, the last sheets got only the last drawings, and the small
   parts that fill the gaps between big ones were never on the same sheet as them. A
   drawing set to share cuts still goes in whole at its turn, because its copies share
   with each other: spread among the rest, E on Fast left 58 parts on their own, against
   43 whole.

At 15 s, the app's default:

| Job | Before | After |
| --- | --- | --- |
| A | 20.9 s, 91.81 in | 15.1 s, 96.31 in |
| B | 35.8 s, 77.70 in, 4 parts on their own | 14.7 s, 81.47 in, 3 on their own |
| C | 153 s, 4 sheets, last 48.06 in | 36.1 s, 4 sheets, last 56.82 in |
| D | 64.5 s, 2 sheets, 114.26 + 12.50 in | 8.3 s, 1 sheet, 88.75 in |
| E | 233 s, 3 sheets, last 13.00 in, 18 on their own | 80.1 s, 3 sheets, last 19.89 in, 8 on their own |
| F | 301 s, 4 sheets, last 19.89 in | 44.3 s, 3 sheets |

On Fast, where the layout and not the time is compared:

| Job | Before | After |
| --- | --- | --- |
| A, B | | same layouts |
| C | 153 s, last sheet 48.06 in | 128 s, last sheet 41.93 in |
| D | 2 sheets, 114.26 + 12.50 in | 1 sheet, 88.75 in |
| E | 235 s, 3 sheets, 18 on their own | 142 s, 3 sheets, 43 on their own |
| F | 304 s, 4 sheets | 215 s, 3 sheets |

A and C at 15 s are longer than Fast because they now deliver the quick round in time,
which is what the time is for; a longer time still reaches Fast and beyond. E moves both
ways with the batches in turn: its quick round went from 55 parts on their own to 8, and
Fast from 18 to 43. By the bench's count no layout after has a pair closer than the
spacing; E before had one. test2 x16 to x27, Hodgson x150 on Fast and Normal and the row
pattern jobs keep their hashes. The mixed common-line regression ends the same (one
sheet, 30 parts, 36 shared cuts), and its log no longer shows the clearance conflict the
baseline layout had.

Still open: a job of several sheets runs past 15 s, because the opening always finishes
(C 36 s, F 44 s, E 80 s). Shared cut between different drawings still depends on the
engine leaving them side by side; ranking the planner's plans by parts welded was tried,
changed no bench and was dropped.

Tests: `NestRoundsFixture` (`AfterTheQuickSearchRoundOneNeedsTwiceItsTimeLeft`),
`TimedNestRoundsFixture` (native, the opening is round 0), `SparrowNestServiceFixture`
(native: `PartsGoInsideTheFramesOnTheirSheetAtTheFullSpacing`, two invented frames and
16 squares on one sheet, unlimited and not, the squares sharing cuts or not;
`PartsThatFitInTheFramesDoNotLengthenTheSheet`; `TheFirstSheetOfAMixedJobGetsSomeOfEachDrawing`,
30 big squares and 40 small ones, three sheets where drawing by drawing took four, the
last with only the small ones). Each was seen red against the old code or a sabotage: no
hole fill per sheet, a hole not drawn in by the compaction, the bare hole in the fill, no
hold-back, Fast not cut after the opening, and batches drawing by drawing.

## Fifteen seconds, with no time to choose (2026-10-31, 2026-11-01)

The operator asked for most real jobs to nest in 15 s or less, with no time to pick. The
menu beside NEST and the row in Advanced settings are gone, and every nest runs the rounds
against `NestRounds.DefaultSeconds`. A session saved with a time or an effort still opens;
those keys are ignored.

Measured on a bench of 30 jobs made from the operator's test drawings, which stay out of the
repository: single drawings from 20 to 800 copies, three mixes, and two jobs in millimetres
on 3048 x 1524 mm sheets (a welding table of 39 parts and a rack enclosure of 7 panels, one
set and five), with common line and without, on unlimited sheets or on one. They run
through `LargeCommonLineJobBenchFixture`, which now takes `SHEETNEST_BENCH_SHEET=WxH`,
`SHEETNEST_BENCH_SHEETS=n` and `SHEETNEST_BENCH_UNITS=mm`, and prints the time spent
reading the drawings. Times are for the whole nest, reading included.

The first four changes give the same layouts to the bit:

1. **Reading the drawings.** `DxfParser.ConnectElements` looked for the next piece of a
   contour among every piece left, so a plate with hundreds of holes took minutes: one
   panel of the rack took 318 s, all seven 413 s. A grid with cells two closing distances
   wide now finds the nearest end, earliest piece first on a tie as before. 98 drawings
   read to the same contours; that panel now reads in 0.14 s.
2. **Holes.** `FillHoles` skips a hole too small for any part before growing it, and
   grows each part's outline once per angle instead of once per hole.
3. **Overlap checks.** `PlacementCollision.Overlaps` hands Clipper only the contours whose
   box reaches the other part, as the compaction already did.
4. **The planner.** The neighbour walk (`IsClearWhereItStands`) tries first the part that
   stopped it last, and a null check no longer builds the placed outline.
5. **A work cap on the opening.** The engine's iteration budget counts its checks of the
   clock, and between two checks one iteration can do a hundred times the work of another,
   so one hard sheet could take a minute at 150 iterations. The patched engine now takes
   `--max-evals`, a cap on its sample evaluations, split between its two phases the way
   the iterations are. A search that stays inside the cap comes out exactly as without it,
   and the cap counts work, not time, so it lands the same on every machine. The opening
   gives each sheet 3,000,000 evaluations divided by the sheets the job is expected to
   take, kept between 600,000 and 1,500,000 (`OpeningEvals`). The sheets expected are the
   parts' area over the usable area of the biggest size in stock, rounded up and never more
   than the stock (`EstimatedSheets`). The opening's last-chance search on the last sheet
   in stock gets twice that work, not a Normal search.
6. **The last sheet.** On the sheet that takes the rest, a part that cannot be walked clear
   is tried past the end of the pack before it is given back (`TryReseatAtTail`). And when
   everything on the last sheet fits past the end of the packs on the sheets before it,
   it goes there and the last sheet is dropped (`FoldLastSheetBack`). Before these, five
   welding tables came out on four sheets, the fourth holding one strip, and F at 15 s on
   four, the fourth holding one clip.

Real bench at 15 s, from 17 of 30 jobs delivered in time to 23. No job takes another sheet,
places fewer parts or leaves more parts on their own:

| Job | Before | After |
| --- | ---: | ---: |
| One drawing, 150 copies, no common line | 30.8 s | 5.9 s |
| 300 brackets on one sheet | 21.8 s | 5.7 s |
| Mix of four drawings, 196 parts, no common line | 45.8 s | 11.0 s |
| Common-line regression mix, 30 parts | 35.7 s | 15.1 s, same layout |
| Welding table, one set on one sheet | 94.3 s | 7.8 s, 20 mm longer |
| Rack, one set | 434 s | 6.1 s |
| Rack, five sets | 487 s | 27.5 s, same layout |
| Welding table, five sets | 453 s | 30.7 s, 3 sheets, 62 mm longer |
| Welding table, five sets, common line | 1,143 s, 25 shared cuts | 337 s, 41 shared cuts |
| Rack, five sets, common line | 909 s | 320 s, 400 mm shorter |
| Welding table, one set, common line | 270 s | 92 s |
| Mix of four drawings, common line | 110 s | 55 s |

The invented jobs of the section above, at 15 s:

| Job | Before | After |
| --- | --- | --- |
| A, B, D | 15.1, 14.7, 8.3 s | 15.1, 13.8, 8.5 s, same layouts |
| C | 36.1 s, 4 sheets, last 56.82 in | 19.7 s, 4 sheets, last 64.56 in |
| E | 80.1 s, last sheet 19.89 in, 8 parts on their own | 33.1 s, last sheet 12.35 in, 30 on their own |
| F | 44.3 s, 3 sheets | 11.2 s, 3 sheets |

On Fast all six keep their layouts, and so do test2 x16 to x27, the mixed common-line
regression, Hodgson x150 on Fast and Normal and the row-pattern jobs. Without
`--max-evals` the new engine gives the same solutions as the old one, byte for byte, on
eight kept jobs. E is the one that gets worse: its shared cut between different drawings
depends on where the engine leaves them, and the opening now searches less.

Still over 15 s: five jobs with common line (mixes of four drawings at 18 and 55 s, the
welding table at 92 and 337 s, five racks at 320 s) and the two biggest without it (five
racks, five welding tables, about 30 s). With common line the planner is the cost: on five
welding tables it tries 53,000 candidates, nearly all rejected for clearance, and rebuilds
the plan after each. Skipping a candidate before applying it is not provably the same
answer, because whether two parts are clear can depend on cuts they share with a third, at
a T.

Tests: `DxfParserChainingFixture` (a plate with 540 holes reads in under 5 s; 52 s before),
`OpeningWorkFixture` (the sheets expected, and the work per sheet with its floor),
`SparrowWorkBudgetFixture` (native: a cap the search stays inside changes nothing, a tighter
one stops it within an iteration and lands the same twice), `TailReseatFixture` (past the
end of the pack, on the same sheet and on an earlier one, and nothing moved when one part
fits nowhere) and `NestingTimeSessionFixture` (a session no longer stores a time; an old one
still opens). Each was seen red against the old code or a sabotage: the work per sheet held
constant, the sheets expected rounded down, no reseat, no fold back, no undo of a fold that
fails, the old engine, and the old session.

## No time limit, and a Stop that keeps the nest (2026-11-01)

The operator asked for no time limit. The app now nests with `NestRounds.NoLimit`: the
rounds stop on the material usage target, on two rounds in a row that do not improve, or on
Stop. The 15 s of the section above is now `NestRounds.DefaultSeconds` for the benches and
the timed tests only, which do not change.

Until the first nest of the run has finished the button is Cancel and throws everything
away. After that it is Stop: the round still running is thrown away whole, as the deadline
did, and the best nest so far is handed over (`NestRoundsStop.Stopped`). While it runs the
status line shows the target and how close the best nest is, on the target's measure.

Tests in `NestRoundsFixture`: long rounds with no limit, the target with no limit, Stop
during a round (thrown or quiet), Stop between rounds, Stop before any nest, Cancel after a
best, and the best-so-far callback. Each was seen red against a sabotage.

A time limit is back as an option, per sheet: Advanced settings > Nesting > "Time per
sheet (s)", 0 (no limit) by default. With a value the job gets that much for every sheet it
is expected to take (`SparrowNestService.TimeLimit`, from `EstimatedSheets`), and the timed
rounds run as before. Tests: `OpeningWorkFixture` and `NestSecondsPerSheetSessionFixture`.

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

Layer order, which is the order the operations run in:

1. `01_CUT_INNER`: closed inner contours.
2. `02_COMMON_LINE`: shared paths, ON PATH / NO OFFSET.
3. `03_CUT_OUTER`: completely closed external cluster boundaries and normal outers.

Common arcs use open bulged LWPOLYLINE entities so their endpoints use exactly the
same coordinates as adjacent network entities. No original shared segment remains
under the common cut. SheetNest generates no common-path lead-in or lead-out and
does not depend on the reader removing duplicates or joining contours.

## Settings and units

`CommonLineGapInches` is the canonical persisted physical value in project/session
settings. `CommonLineDefaults.DefaultGapInches` is the single default, 0.010 inches
(0.254 mm). Advanced Settings displays/converts the active inch/mm value and offers
Reset to Default. Algorithms consume the active converted setting.

GeometryTolerance is independent: its default is 0.0001 inches, converted to active
units. It is not a user-facing kerf or gap setting. Cluster sizing supports Auto
(bounded to at most 100 parts) or a manual 2..500-part limit.

The existing export format may declare unitless DXF coordinates. Select matching
project and import units in your CAM; the gap must never be used to infer file units.

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
`CommonLineGapSettingsFixture`, `NestingTimeSessionFixture`,
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
- Automated geometry/export tests do not replace importing the DXF into your CAM
  and a physical cutting trial. No such production trial is claimed here.
