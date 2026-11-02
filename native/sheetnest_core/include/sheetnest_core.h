/*
 * sheetnest_core: SheetNest's native geometry core, called from C# through P/Invoke.
 *
 * The API is plain C: simple types and pointers only, cdecl, no C++ types across the boundary, no global
 * state (every call may run on its own thread at the same time as others).
 *
 * Units: every length is in DRAWING UNITS, the units the job's DXF files are drawn in (inches or
 * millimetres). Internally polygons are scaled to 64-bit integers at SNC_COMPACT_SCALE units per drawing
 * unit, exactly as the C# code does, so the results match it bit for bit.
 */
#ifndef SHEETNEST_CORE_H
#define SHEETNEST_CORE_H

#if defined(_WIN32)
#  define SNC_CALL __cdecl
#  if defined(SHEETNEST_CORE_BUILD)
#    define SNC_API __declspec(dllexport)
#  else
#    define SNC_API __declspec(dllimport)
#  endif
#else
#  define SNC_CALL
#  define SNC_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Results of snc_compact. */
#define SNC_OK 0        /* xs/ys hold the compacted positions */
#define SNC_CANCELLED 1 /* *cancel became non-zero; xs/ys are unchanged */
#define SNC_ERROR 2     /* the input could not be compacted the way the C# code would (it would throw);
                           xs/ys are unchanged and the caller should run the C# code to get its exception */

/*
 * Integer units per drawing unit, and the explicit tolerances the compaction works to (all as in
 * DeepNestSharp/RasterNest/RasterCompact.cs):
 *   overlap      two shapes collide only when their intersection exceeds 1e-6 square drawing units
 *   backoff      every slide stops max(0.001, 0.003 x the largest spacing) drawing units short of contact
 *   weld reach   the final weld closes common-line seams by at most 0.05 drawing units
 */
#define SNC_COMPACT_SCALE 1e6

/*
 * Compacts one sheet's placements toward the sheet's lower-left corner: a translation of
 * RasterCompact.Compact. Parts only move, never rotate.
 *
 * n               number of placed parts
 * contour_counts  [n] contours per part: the outline first, then its holes (at least 1)
 * point_counts    [sum of contour_counts] points per contour, in the same order
 * points          [2 x sum of point_counts] x, y of every point, relative to the part's position
 * xs, ys          [n] part positions; read, and on SNC_OK overwritten with the compacted positions
 * spacings        [n] each part's nominal spacing to parts it may not share a cut with
 * has_peers       [n] 1 when the part has at least one planned common-edge peer, else 0
 * shares          [n x n] row-major, 1 when parts i and j share a planned common edge
 *                 (RasterCompact.SharesCommonEdge); NULL when no part has peers
 * groups          [groups_length] rigid group id per part (> 0 = member of that group), or NULL
 * sheet_w, sheet_h, margin, common_line_gap   in drawing units
 * cancel          polled where the C# code checks its CancellationToken; non-zero cancels. May be NULL.
 * stop            polled at the same points; non-zero ends the pass early with SNC_OK and the positions
 *                 reached so far (every move was checked before it was made). May be NULL.
 */
SNC_API int SNC_CALL snc_compact(
  int n,
  const int* contour_counts,
  const int* point_counts,
  const double* points,
  double* xs,
  double* ys,
  const double* spacings,
  const unsigned char* has_peers,
  const unsigned char* shares,
  const int* groups,
  int groups_length,
  double sheet_w,
  double sheet_h,
  double margin,
  double common_line_gap,
  const volatile int* cancel,
  const volatile int* stop);

/*
 * PlacementCollision.TooClose: whether two placed parts are closer than a clearance. Each outline is grown by
 * the clearance in turn (round joins, holes kept) and the pair is too close when the other part bites into it
 * deeper than `sliver`, either way round. The depth of a bite is the diameter of the largest circle inside it.
 *
 * Integer units per drawing unit, and the explicit tolerances (as in PlacementCollision.cs):
 *   round shells  arc tolerance 0.0005 x the clearance
 *   depth         a bite counts only past `sliver` drawing units (the caller's kerf or noise floor)
 */
#define SNC_COLLISION_SCALE 1e6

#define SNC_TOO_CLOSE_ERROR (-1) /* the C# code would throw on this input; the caller runs it to get the exception */

/*
 * Each part: its contour count (outline first, then holes), the points per contour, and the x, y of every
 * point, already placed, in drawing units. A contour of fewer than three points is ignored, as in C#.
 * Returns 1 when too close, 0 when not, SNC_TOO_CLOSE_ERROR on failure.
 */
SNC_API int SNC_CALL snc_too_close(
  int a_contours,
  const int* a_point_counts,
  const double* a_points,
  int b_contours,
  const int* b_point_counts,
  const double* b_points,
  double clearance,
  double sliver);

#ifdef __cplusplus
}
#endif

#endif /* SHEETNEST_CORE_H */
