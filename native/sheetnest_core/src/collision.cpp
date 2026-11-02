// PlacementCollision.TooClose translated from DeepNestSharp/RasterNest/PlacementCollision.cs, statement for
// statement, with nothing optimised or changed: the same Clipper calls, constants and order of every loop and
// floating-point expression. The C# file explains why each step is there.

#include "sheetnest_core.h"

#include <cmath>
#include <vector>

#include "clipper.hpp"
#include "clipper_util.hpp"

namespace {

using namespace snc;

constexpr double Scale = SNC_COLLISION_SCALE; // PlacementCollision.Scale: integer units per drawing unit

struct Polygon
{
  int contours;            // outline first, then holes
  const int* pointCounts;  // [contours]
  const double* points;    // x, y pairs, already placed
};

// INfp.MinX/MinY/MaxX/MaxY: over the outline's points only, 0 when it has none.
struct Box
{
  double minX = 0;
  double minY = 0;
  double maxX = 0;
  double maxY = 0;
};

Box BoxOf(const Polygon& polygon)
{
  Box box;
  int count = polygon.contours > 0 ? polygon.pointCounts[0] : 0;
  if (count > 0)
  {
    const double* p = polygon.points;
    box.minX = box.maxX = p[0];
    box.minY = box.maxY = p[1];
    for (int k = 1; k < count; k++)
    {
      double px = p[2 * k];
      double py = p[(2 * k) + 1];
      if (px < box.minX) { box.minX = px; }
      if (px > box.maxX) { box.maxX = px; }
      if (py < box.minY) { box.minY = py; }
      if (py > box.maxY) { box.maxY = py; }
    }
  }

  return box;
}

bool BoxesApart(const Box& a, const Box& b, double clearance)
{
  return a.maxX + clearance <= b.minX || b.maxX <= a.minX - clearance
    || a.maxY + clearance <= b.minY || b.maxY <= a.minY - clearance;
}

// ToPaths: every contour with at least three points, outline first.
Paths ToPaths(const Polygon& polygon)
{
  Paths paths;
  const double* p = polygon.points;
  for (int c = 0; c < polygon.contours; c++)
  {
    int count = polygon.pointCounts[c];
    if (count >= 3)
    {
      Path path;
      path.reserve(static_cast<size_t>(count));
      for (int k = 0; k < count; k++)
      {
        path.push_back(IntPoint(NetRound(p[2 * k] * Scale), NetRound(p[(2 * k) + 1] * Scale)));
      }

      paths.push_back(path);
    }

    p += 2 * count;
  }

  return paths;
}

// InflateOuter: grows the first contour; holes are kept as they are.
Paths InflateOuter(const Paths& paths, double amount)
{
  if (paths.empty() || amount <= 0)
  {
    return paths;
  }

  Path outer = paths[0];
  if (!ClipperLib::Orientation(outer))
  {
    std::reverse(outer.begin(), outer.end());
  }

  ClipperLib::ClipperOffset offset(2.0, std::fabs(amount * Scale) * 0.0005);
  offset.AddPath(outer, ClipperLib::jtRound, ClipperLib::etClosedPolygon);
  Paths grown;
  offset.Execute(grown, amount * Scale);

  Paths result(grown);
  for (size_t i = 1; i < paths.size(); i++)
  {
    result.push_back(paths[i]);
  }

  return !result.empty() ? result : paths;
}

double InscribedDiameter(const Path& path, double limit)
{
  double lo = 0, hi = limit;
  for (int i = 0; i < 8; i++)
  {
    double mid = (lo + hi) / 2;
    ClipperLib::ClipperOffset offset(2.0, NetMax(1, mid * 0.0005));
    offset.AddPath(path, ClipperLib::jtMiter, ClipperLib::etClosedPolygon);
    Paths shrunk;
    offset.Execute(shrunk, -mid);
    if (!shrunk.empty())
    {
      lo = mid;
    }
    else
    {
      hi = mid;
    }
  }

  return 2 * lo;
}

bool BitesDeeperThan(const Path& path, double limit)
{
  double area = std::fabs(ClipperLib::Area(path));
  if (area <= 0 || path.size() < 3)
  {
    return false;
  }

  double perimeter = 0;
  for (size_t i = 0; i < path.size(); i++)
  {
    const IntPoint& p = path[i];
    const IntPoint& q = path[(i + 1) % path.size()];
    double dx = static_cast<double>(q.X - p.X);
    double dy = static_cast<double>(q.Y - p.Y);
    perimeter += std::sqrt((dx * dx) + (dy * dy));
  }

  if (perimeter <= 0)
  {
    return false;
  }

  double ratio = 2 * area / perimeter;
  if (ratio > limit)
  {
    return true;
  }

  if (2 * ratio <= limit)
  {
    return false;
  }

  return InscribedDiameter(path, limit) > limit;
}

bool Overlaps(const Paths& a, const Paths& b, double sliver)
{
  ClipperLib::Clipper clipper;
  if (!AddReaching(clipper, a, BoundsOf(b), ClipperLib::ptSubject) || !AddReaching(clipper, b, BoundsOf(a), ClipperLib::ptClip))
  {
    return false;
  }

  Paths solution;
  clipper.Execute(ClipperLib::ctIntersection, solution, ClipperLib::pftEvenOdd, ClipperLib::pftEvenOdd);

  double limit = NetMax(0, sliver) * Scale;
  for (const Path& path : solution)
  {
    if (BitesDeeperThan(path, limit))
    {
      return true;
    }
  }

  return false;
}

bool TooCloseOneWay(const Polygon& grown, const Polygon& other, double clearance, double sliver)
{
  Paths paths = ToPaths(grown);
  if (clearance > 0)
  {
    paths = InflateOuter(paths, clearance);
  }

  return Overlaps(paths, ToPaths(other), sliver);
}

} // namespace

extern "C" SNC_API int SNC_CALL snc_too_close(
  int a_contours,
  const int* a_point_counts,
  const double* a_points,
  int b_contours,
  const int* b_point_counts,
  const double* b_points,
  double clearance,
  double sliver)
{
  try
  {
    Polygon a{a_contours, a_point_counts, a_points};
    Polygon b{b_contours, b_point_counts, b_points};
    if (BoxesApart(BoxOf(a), BoxOf(b), clearance))
    {
      return 0;
    }

    return TooCloseOneWay(a, b, clearance, sliver) || TooCloseOneWay(b, a, clearance, sliver) ? 1 : 0;
  }
  catch (...)
  {
    return SNC_TOO_CLOSE_ERROR;
  }
}
