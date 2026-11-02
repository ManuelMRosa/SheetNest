// Helpers the C# geometry classes each keep their own copy of (RasterCompact, PlacementCollision), written once
// here with the same behaviour: .NET's rounding and Math.Min/Max on doubles, integer bounds, and the box test
// that decides which contours go to Clipper.
#ifndef SHEETNEST_CLIPPER_UTIL_HPP
#define SHEETNEST_CLIPPER_UTIL_HPP

#include <algorithm>
#include <cmath>
#include <limits>

#include "clipper.hpp"

namespace snc {

using ClipperLib::cInt;
using ClipperLib::IntPoint;
using ClipperLib::Path;
using ClipperLib::Paths;

constexpr cInt LongMax = std::numeric_limits<cInt>::max();
constexpr cInt LongMin = std::numeric_limits<cInt>::min();

struct IntRect
{
  cInt left;
  cInt top;
  cInt right;
  cInt bottom;
};

// (long)Math.Round(v): .NET rounds half to even. nearbyint does too in the default rounding mode, which
// is the mode .NET runs in. (Clipper's own Round, half away from zero, is a different rule and stays.)
inline cInt NetRound(double v)
{
  return static_cast<cInt>(std::nearbyint(v));
}

// Math.Min / Math.Max on doubles as .NET defines them (IEEE 754:2019 minimum/maximum: NaN propagates,
// -0 is below +0), so even a signed zero comes out the same.
inline double NetMin(double a, double b)
{
  if (a != b && !std::isnan(a))
  {
    return a < b ? a : b;
  }

  return std::signbit(a) ? a : b;
}

inline double NetMax(double a, double b)
{
  if (a != b)
  {
    if (!std::isnan(a))
    {
      return b < a ? a : b;
    }

    return a;
  }

  return std::signbit(b) ? a : b;
}

inline IntRect BoundsOf(const Path& path)
{
  cInt minX = LongMax, minY = LongMax, maxX = LongMin, maxY = LongMin;
  for (const IntPoint& p : path)
  {
    minX = std::min(minX, p.X);
    maxX = std::max(maxX, p.X);
    minY = std::min(minY, p.Y);
    maxY = std::max(maxY, p.Y);
  }

  return IntRect{minX, minY, maxX, maxY};
}

inline IntRect BoundsOf(const Paths& paths)
{
  cInt minX = LongMax, minY = LongMax, maxX = LongMin, maxY = LongMin;
  for (const Path& path : paths)
  {
    for (const IntPoint& p : path)
    {
      minX = std::min(minX, p.X);
      maxX = std::max(maxX, p.X);
      minY = std::min(minY, p.Y);
      maxY = std::max(maxY, p.Y);
    }
  }

  return IntRect{minX, minY, maxX, maxY};
}

inline bool BoxesTouch(const IntRect& a, const IntRect& b)
{
  return a.left <= b.right && b.left <= a.right && a.top <= b.bottom && b.top <= a.bottom;
}

// Adds the contours whose box touches `other`; false when there are none.
inline bool AddReaching(ClipperLib::Clipper& clipper, const Paths& paths, const IntRect& other, ClipperLib::PolyType type)
{
  bool any = false;
  for (const Path& path : paths)
  {
    if (BoxesTouch(BoundsOf(path), other))
    {
      clipper.AddPath(path, type, true);
      any = true;
    }
  }

  return any;
}

} // namespace snc

#endif // SHEETNEST_CLIPPER_UTIL_HPP
