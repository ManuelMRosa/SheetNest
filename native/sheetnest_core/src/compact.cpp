// RasterCompact.Compact translated from DeepNestSharp/RasterNest/RasterCompact.cs, statement for statement,
// with nothing optimised or changed: the same Clipper calls (Clipper 6.4.2, the release Clipper.Core's C#
// was ported from), the same constants, the same order of every loop and every floating-point expression.
// The C# file explains WHY each step is there; the comments here only note what has to stay identical for
// the two to agree bit for bit.

#include "sheetnest_core.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>
#include <map>
#include <numeric>
#include <stdexcept>
#include <vector>

#include "clipper.hpp"
#include "clipper_util.hpp"

namespace {

using namespace snc;

constexpr double Scale = SNC_COMPACT_SCALE;          // integer units per drawing unit
constexpr double EpsArea = 1e-6 * Scale * Scale;     // overlaps below 1e-6 square units are touching
constexpr double Backoff = 0.001;                    // slack floor left after a slide, drawing units
constexpr double BackoffPerClearance = 0.003;        // slack per unit of the clearance being enforced
constexpr double WeldMax = 0.05;                     // furthest the weld closes a seam, drawing units

// What the C# code would have thrown on. The caller runs the C# code instead, so the exception is its own.
struct WouldThrow : std::runtime_error
{
  WouldThrow() : std::runtime_error("the C# compaction throws on this input") {}
};

struct Cancelled
{
};

struct Contour
{
  const double* pts; // x, y pairs
  int count;
};

struct Item
{
  std::vector<Contour> contours; // outline first, then holes
  double x;
  double y;
  double spacing;
  bool hasPeers;
  // INfp.MinX/MaxX/MinY/MaxY: over the outline's points only, 0 when it has none. LINQ Min/Max keep the
  // first of equal values, as < and > do here.
  double minX;
  double maxX;
  double minY;
  double maxY;
};

Path ContourToPath(const Contour& contour, double dx, double dy)
{
  Path path;
  path.reserve(static_cast<size_t>(contour.count));
  for (int k = 0; k < contour.count; k++)
  {
    double px = contour.pts[2 * k];
    double py = contour.pts[(2 * k) + 1];
    path.push_back(IntPoint(NetRound((px + dx) * Scale), NetRound((py + dy) * Scale)));
  }

  return path;
}

Paths ToPaths(const Item& it)
{
  Paths paths;
  for (const Contour& contour : it.contours)
  {
    paths.push_back(ContourToPath(contour, it.x, it.y));
  }

  return paths;
}

Paths Inflate(const Paths& paths, double inches)
{
  if (inches <= 0 || paths.empty())
  {
    return paths;
  }

  Path outer = paths[0];
  if (!ClipperLib::Orientation(outer))
  {
    std::reverse(outer.begin(), outer.end());
  }

  ClipperLib::ClipperOffset offset(2.0, std::fabs(inches * Scale) * 0.0005);
  offset.AddPath(outer, ClipperLib::jtRound, ClipperLib::etClosedPolygon);
  Paths grown;
  offset.Execute(grown, inches * Scale);

  Paths result(grown);
  for (size_t i = 1; i < paths.size(); i++)
  {
    Path hole = paths[i];
    if (!ClipperLib::Orientation(hole))
    {
      std::reverse(hole.begin(), hole.end());
    }

    ClipperLib::ClipperOffset shrink(2.0, std::fabs(inches * Scale) * 0.0005);
    shrink.AddPath(hole, ClipperLib::jtRound, ClipperLib::etClosedPolygon);
    Paths drawnIn;
    shrink.Execute(drawnIn, -inches * Scale);
    result.insert(result.end(), drawnIn.begin(), drawnIn.end());
  }

  return !result.empty() ? result : paths;
}

Paths Translate(const Paths& paths, cInt dx, cInt dy)
{
  Paths moved;
  moved.reserve(paths.size());
  for (const Path& path : paths)
  {
    Path np;
    np.reserve(path.size());
    for (const IntPoint& p : path)
    {
      np.push_back(IntPoint(p.X + dx, p.Y + dy));
    }

    moved.push_back(np);
  }

  return moved;
}

bool Intersects(const Paths& a, const Paths& b)
{
  ClipperLib::Clipper clipper;
  if (!AddReaching(clipper, a, BoundsOf(b), ClipperLib::ptSubject) || !AddReaching(clipper, b, BoundsOf(a), ClipperLib::ptClip))
  {
    return false;
  }

  Paths solution;
  clipper.Execute(ClipperLib::ctIntersection, solution, ClipperLib::pftEvenOdd, ClipperLib::pftEvenOdd);
  double area = 0;
  for (const Path& path : solution)
  {
    area += std::fabs(ClipperLib::Area(path));
  }

  return area > EpsArea;
}

// A C# path array entry that may be null (pathsCC of a part without common-edge peers).
struct MaybePaths
{
  bool present = false;
  Paths paths;
};

struct Compactor
{
  std::vector<Item>& items;
  const unsigned char* shares;
  int n;
  const volatile int* cancel;
  const volatile int* stop; // the nest's clock: finish early and keep the positions reached
  // memberSets[i] in C#: the same array instance for every member of one rigid group. Here the group's
  // index into `sets`, -1 for a part on its own.
  std::vector<int> memberSet;
  std::vector<std::vector<int>> sets;
  std::vector<Paths> pathsRaw;
  std::vector<Paths> pathsHalf;
  std::vector<MaybePaths> pathsCC;
  std::vector<IntRect> bounds;

  void CheckCancel() const
  {
    if (cancel != nullptr && *cancel != 0)
    {
      throw Cancelled();
    }
  }

  bool StopRequested() const
  {
    return stop != nullptr && *stop != 0;
  }

  bool SharesCommonEdge(int i, int j) const
  {
    return shares != nullptr && shares[(static_cast<size_t>(i) * static_cast<size_t>(n)) + static_cast<size_t>(j)] != 0;
  }

  bool SameSet(int a, int b) const
  {
    return memberSet[a] >= 0 && memberSet[a] == memberSet[b];
  }

  // The members a move of part m carries: its whole group, or itself.
  std::vector<int> MembersOf(int m) const
  {
    return memberSet[m] >= 0 ? sets[memberSet[m]] : std::vector<int>{m};
  }

  bool RawClearAll(int m, double ox, double oy)
  {
    cInt tx = NetRound(ox * Scale);
    cInt ty = NetRound(oy * Scale);
    int set = memberSet[m];
    for (int mm : MembersOf(m))
    {
      Paths moved = tx == 0 && ty == 0 ? pathsRaw[mm] : Translate(pathsRaw[mm], tx, ty);
      IntRect mb = BoundsOf(moved);
      for (int k = 0; k < n; k++)
      {
        if (k == mm || (set >= 0 && memberSet[k] == set) || !BoxesTouch(mb, bounds[k]))
        {
          continue;
        }

        if (Intersects(moved, pathsRaw[k]))
        {
          return false;
        }
      }
    }

    return true;
  }

  bool ValidOffset(int i, double ox, double oy)
  {
    cInt tx = NetRound(ox * Scale);
    cInt ty = NetRound(oy * Scale);
    int set = memberSet[i];
    for (int mm : MembersOf(i))
    {
      Paths movedHalf = tx == 0 && ty == 0 ? pathsHalf[mm] : Translate(pathsHalf[mm], tx, ty);
      MaybePaths movedCC;
      if (pathsCC[mm].present)
      {
        movedCC.present = true;
        movedCC.paths = tx == 0 && ty == 0 ? pathsCC[mm].paths : Translate(pathsCC[mm].paths, tx, ty);
      }

      IntRect mb = BoundsOf(movedHalf);
      for (int j = 0; j < n; j++)
      {
        if (j == mm || (set >= 0 && memberSet[j] == set) || !BoxesTouch(mb, bounds[j]))
        {
          continue;
        }

        bool hit = SharesCommonEdge(mm, j)
          ? Intersects(movedCC.paths, pathsCC[j].paths)
          : Intersects(movedHalf, pathsHalf[j]);
        if (hit)
        {
          return false;
        }
      }
    }

    return true;
  }

  void ApplyMove(int i, double ox, double oy)
  {
    cInt tx = NetRound(ox * Scale);
    cInt ty = NetRound(oy * Scale);
    for (int mm : MembersOf(i))
    {
      items[mm].x += ox;
      items[mm].y += oy;
      pathsRaw[mm] = Translate(pathsRaw[mm], tx, ty);
      pathsHalf[mm] = Translate(pathsHalf[mm], tx, ty);
      if (pathsCC[mm].present)
      {
        pathsCC[mm].paths = Translate(pathsCC[mm].paths, tx, ty);
      }

      bounds[mm] = BoundsOf(pathsHalf[mm]);
    }
  }

  bool PairClear(int a, int b, double ox, double oy)
  {
    bool cc = SharesCommonEdge(a, b);
    const Paths& pa = cc ? pathsCC[a].paths : pathsHalf[a];
    const Paths& pb = cc ? pathsCC[b].paths : pathsHalf[b];
    Paths moved = ox == 0 && oy == 0 ? pa : Translate(pa, NetRound(ox * Scale), NetRound(oy * Scale));
    return !BoxesTouch(BoundsOf(moved), BoundsOf(pb)) || !Intersects(moved, pb);
  }

  bool Slide(int i, int dirX, int dirY, double margin)
  {
    double max = std::numeric_limits<double>::max();
    double clearance = 0;
    for (int member : MembersOf(i))
    {
      const Item& it = items[member];
      max = NetMin(max, dirX != 0 ? (it.x + it.minX) - margin : (it.y + it.minY) - margin);
      clearance = NetMax(clearance, it.spacing);
    }

    double backoff = NetMax(Backoff, clearance * BackoffPerClearance);

    if (max <= backoff)
    {
      return false;
    }

    if (!ValidOffset(i, dirX * NetMin(max, backoff * 2.0), dirY * NetMin(max, backoff * 2.0)))
    {
      return false;
    }

    double slide;
    if (ValidOffset(i, dirX * max, dirY * max))
    {
      slide = max;
    }
    else
    {
      double lo = 0, hi = max;
      while (hi - lo > backoff / 2.0)
      {
        double mid = (lo + hi) / 2.0;
        if (ValidOffset(i, dirX * mid, dirY * mid))
        {
          lo = mid;
        }
        else
        {
          hi = mid;
        }
      }

      slide = lo;
    }

    slide -= backoff;
    if (slide <= 1e-9)
    {
      return false;
    }

    ApplyMove(i, dirX * slide, dirY * slide);
    return true;
  }

  // DirectionalGapExact: in a frame where the slide is always -U.
  cInt DirectionalGapExact(int i, bool alongX, cInt capInt, cInt marginInt)
  {
    auto U = [alongX](const IntPoint& p) { return alongX ? p.X : p.Y; };
    auto V = [alongX](const IntPoint& p) { return alongX ? p.Y : p.X; };

    int iSet = memberSet[i];

    // Mine(source): the part's own paths, or every group member's concatenated. C# hands back null for a
    // lone part without a CC shell, and throws (AddRange(null)) for a group with a member without one.
    auto mine = [&](int which, bool& present) -> Paths {
      present = true;
      if (iSet < 0)
      {
        if (which == 0)
        {
          return pathsRaw[i];
        }

        if (which == 1)
        {
          return pathsHalf[i];
        }

        present = pathsCC[i].present;
        return pathsCC[i].paths;
      }

      Paths all;
      for (int mm : sets[iSet])
      {
        const Paths* source = nullptr;
        if (which == 0)
        {
          source = &pathsRaw[mm];
        }
        else if (which == 1)
        {
          source = &pathsHalf[mm];
        }
        else
        {
          if (!pathsCC[mm].present)
          {
            throw WouldThrow();
          }

          source = &pathsCC[mm].paths;
        }

        all.insert(all.end(), source->begin(), source->end());
      }

      return all;
    };

    bool present = true;
    Paths mineRaw = mine(0, present);
    Paths mineHalf = mine(1, present);
    Paths mineCommon = mine(2, present);

    struct FrameBox
    {
      cInt minU;
      cInt maxU;
      cInt minV;
      cInt maxV;
    };

    auto frameBounds = [&](const Paths& paths) {
      FrameBox box{LongMax, LongMin, LongMax, LongMin};
      for (const Path& path : paths)
      {
        for (const IntPoint& p : path)
        {
          cInt u = U(p);
          cInt v = V(p);
          if (u < box.minU) { box.minU = u; }
          if (u > box.maxU) { box.maxU = u; }
          if (v < box.minV) { box.minV = v; }
          if (v > box.maxV) { box.maxV = v; }
        }
      }

      return box;
    };

    FrameBox rawBox = frameBounds(mineRaw);
    FrameBox halfBox = frameBounds(mineHalf);

    cInt best = LongMax;
    best = std::min(best, rawBox.minU - marginInt);

    auto rayGap = [&](const IntPoint& v, const IntPoint& a, const IntPoint& b) -> cInt {
      cInt va = V(a);
      cInt vb = V(b);
      if (va == vb)
      {
        if (V(v) != va)
        {
          return LongMax;
        }

        cInt nearest = std::max(U(a), U(b)) <= U(v) ? std::max(U(a), U(b)) : LongMax;
        return nearest == LongMax ? LongMax : U(v) - nearest;
      }

      if (V(v) < std::min(va, vb) || V(v) > std::max(va, vb))
      {
        return LongMax;
      }

      double t = static_cast<double>(V(v) - va) / static_cast<double>(vb - va);
      cInt uEdge = NetRound(static_cast<double>(U(a)) + (t * static_cast<double>(U(b) - U(a))));
      return uEdge <= U(v) ? U(v) - uEdge : LongMax;
    };

    auto rayGapReverse = [&](const IntPoint& w, const IntPoint& a, const IntPoint& b) -> cInt {
      cInt va = V(a);
      cInt vb = V(b);
      if (va == vb)
      {
        if (V(w) != va)
        {
          return LongMax;
        }

        cInt nearest = std::min(U(a), U(b)) >= U(w) ? std::min(U(a), U(b)) : LongMax;
        return nearest == LongMax ? LongMax : nearest - U(w);
      }

      if (V(w) < std::min(va, vb) || V(w) > std::max(va, vb))
      {
        return LongMax;
      }

      double t = static_cast<double>(V(w) - va) / static_cast<double>(vb - va);
      cInt uEdge = NetRound(static_cast<double>(U(a)) + (t * static_cast<double>(U(b) - U(a))));
      return uEdge >= U(w) ? uEdge - U(w) : LongMax;
    };

    for (int j = 0; j < n; j++)
    {
      if (j == i || (iSet >= 0 && memberSet[j] == iSet))
      {
        continue;
      }

      bool share = SharesCommonEdge(i, j);
      const Paths& theirs = share ? pathsCC[j].paths : pathsHalf[j];
      const Paths& mineHere = share ? mineCommon : mineHalf;
      FrameBox commonBox = share ? frameBounds(mineCommon) : FrameBox{0, 0, 0, 0};
      FrameBox myBox = share ? commonBox : halfBox;
      cInt myMinU = myBox.minU, myMaxU = myBox.maxU, myMinV = myBox.minV, myMaxV = myBox.maxV;

      cInt thMinU = LongMax, thMaxU = LongMin, thMinV = LongMax, thMaxV = LongMin;
      for (const Path& path : theirs)
      {
        for (const IntPoint& p : path)
        {
          cInt u = U(p);
          cInt v = V(p);
          if (u < thMinU) { thMinU = u; }
          if (u > thMaxU) { thMaxU = u; }
          if (v < thMinV) { thMinV = v; }
          if (v > thMaxV) { thMaxV = v; }
        }
      }

      if (thMinV >= myMaxV || thMaxV <= myMinV || thMinU >= myMaxU || thMaxU < myMinU - capInt)
      {
        continue;
      }

      for (const Path& pathA : mineHere)
      {
        for (const IntPoint& v : pathA)
        {
          if (V(v) < thMinV || V(v) > thMaxV || U(v) < thMinU || U(v) - thMaxU > capInt)
          {
            continue;
          }

          for (const Path& pathB : theirs)
          {
            for (size_t k = 0; k < pathB.size(); k++)
            {
              cInt g1 = rayGap(v, pathB[k], pathB[(k + 1) % pathB.size()]);
              if (g1 < best)
              {
                best = g1;
              }
            }
          }
        }
      }

      for (const Path& pathB : theirs)
      {
        for (const IntPoint& w : pathB)
        {
          if (V(w) < myMinV || V(w) > myMaxV || U(w) > myMaxU || myMinU - U(w) > capInt)
          {
            continue;
          }

          for (const Path& pathA : mineHere)
          {
            for (size_t m = 0; m < pathA.size(); m++)
            {
              cInt g2 = rayGapReverse(w, pathA[m], pathA[(m + 1) % pathA.size()]);
              if (g2 < best)
              {
                best = g2;
              }
            }
          }
        }
      }

      if (best == 0)
      {
        return 0;
      }
    }

    return best;
  }

  void Run(const int* groups, int groupsLength, double sheetW, double sheetH, double margin, double commonLineGap)
  {
    // Rigid groups: members in ascending index order, one shared set per group id with more than one member.
    memberSet.assign(static_cast<size_t>(n), -1);
    if (groups != nullptr)
    {
      std::map<int, std::vector<int>> byGroup;
      for (int i = 0; i < n && i < groupsLength; i++)
      {
        if (groups[i] > 0)
        {
          byGroup[groups[i]].push_back(i);
        }
      }

      for (auto& entry : byGroup)
      {
        if (entry.second.size() > 1)
        {
          int index = static_cast<int>(sets.size());
          sets.push_back(entry.second);
          for (int i : entry.second)
          {
            memberSet[i] = index;
          }
        }
      }
    }

    for (int i = 0; i < n; i++)
    {
      pathsRaw.push_back(ToPaths(items[i]));
    }

    for (int i = 0; i < n; i++)
    {
      pathsHalf.push_back(items[i].spacing > 0 ? Inflate(ToPaths(items[i]), items[i].spacing / 2.0) : ToPaths(items[i]));
    }

    commonLineGap = NetMax(0, commonLineGap);
    for (int i = 0; i < n; i++)
    {
      MaybePaths cc;
      if (items[i].hasPeers)
      {
        cc.present = true;
        cc.paths = Inflate(ToPaths(items[i]), commonLineGap / 2.0);
      }

      pathsCC.push_back(cc);
    }

    for (int i = 0; i < n; i++)
    {
      bounds.push_back(BoundsOf(pathsHalf[i]));
    }

    bool growX = sheetW > sheetH;

    // OrderBy(key1).ThenBy(key2): a stable sort, keys computed once from the starting positions.
    std::vector<int> order(static_cast<size_t>(n));
    std::iota(order.begin(), order.end(), 0);
    {
      std::vector<double> key1(static_cast<size_t>(n)), key2(static_cast<size_t>(n));
      for (int i = 0; i < n; i++)
      {
        key1[i] = growX ? items[i].y + items[i].minY : items[i].x + items[i].minX;
        key2[i] = growX ? items[i].x + items[i].minX : items[i].y + items[i].minY;
      }

      std::stable_sort(order.begin(), order.end(), [&](int a, int b) {
        if (key1[a] < key1[b]) { return true; }
        if (key1[b] < key1[a]) { return false; }
        return key2[a] < key2[b];
      });
    }

    int maxIterations = std::min(128, n + 8);
    const double probes[] = {0.01, 0.05, 0.1, 0.25};
    const bool axes[4][2] = {{true, true}, {false, true}, {true, false}, {false, false}};
    bool stopped = false;
    for (int iter = 0; iter < maxIterations; iter++)
    {
      CheckCancel();
      if (StopRequested())
      {
        stopped = true;
        break;
      }

      bool anyViolation = false;
      bool movedAny = false;
      for (int a = 0; a < n; a++)
      {
        for (int b = a + 1; b < n; b++)
        {
          if ((memberSet[a] >= 0 && memberSet[a] == memberSet[b])
              || !BoxesTouch(bounds[a], bounds[b]) || PairClear(a, b, 0, 0))
          {
            continue;
          }

          anyViolation = true;

          double cxa = items[a].x + ((items[a].minX + items[a].maxX) / 2.0);
          double cya = items[a].y + ((items[a].minY + items[a].maxY) / 2.0);
          double cxb = items[b].x + ((items[b].minX + items[b].maxX) / 2.0);
          double cyb = items[b].y + ((items[b].minY + items[b].maxY) / 2.0);

          int pushed = -1;
          double dx = 0, dy = 0, hi = std::numeric_limits<double>::max();

          for (const auto& axis : axes)
          {
            bool axisX = axis[0];
            bool positive = axis[1];
            int mover = axisX
              ? (positive ? (cxb >= cxa ? b : a) : (cxb >= cxa ? a : b))
              : (positive ? (cyb >= cya ? b : a) : (cyb >= cya ? a : b));
            int anchor2 = mover == a ? b : a;
            double sign = positive ? 1 : -1;
            double ddx = axisX ? sign : 0;
            double ddy = axisX ? 0 : sign;
            const Item& mi = items[mover];
            double roof = axisX
              ? (positive ? sheetW - margin - (mi.x + mi.maxX) : (mi.x + mi.minX) - margin)
              : (positive ? sheetH - margin - (mi.y + mi.maxY) : (mi.y + mi.minY) - margin);

            double axisHi = -1;
            for (double probe : probes)
            {
              if (probe > roof)
              {
                break;
              }

              if (PairClear(mover, anchor2, ddx * probe, ddy * probe))
              {
                axisHi = probe;
                break;
              }
            }

            if (axisHi < 0)
            {
              continue;
            }

            double lo2 = 0;
            for (int k = 0; k < 18; k++)
            {
              double mid = (lo2 + axisHi) / 2.0;
              if (PairClear(mover, anchor2, ddx * mid, ddy * mid))
              {
                axisHi = mid;
              }
              else
              {
                lo2 = mid;
              }
            }

            if (axisHi < hi)
            {
              hi = axisHi;
              pushed = mover;
              dx = ddx;
              dy = ddy;
            }
          }

          if (pushed < 0)
          {
            continue;
          }

          if (!RawClearAll(pushed, dx * hi, dy * hi))
          {
            double lo2 = 0, hi2 = hi;
            for (int k = 0; k < 18; k++)
            {
              double mid = (lo2 + hi2) / 2.0;
              if (RawClearAll(pushed, dx * mid, dy * mid))
              {
                lo2 = mid;
              }
              else
              {
                hi2 = mid;
              }
            }

            hi = lo2 - 0.0005;
          }

          if (hi <= 1e-6)
          {
            continue;
          }

          ApplyMove(pushed, dx * hi, dy * hi);
          movedAny = true;
        }
      }

      if (!anyViolation || !movedAny)
      {
        break;
      }
    }

    std::vector<bool> processed(static_cast<size_t>(n));
    for (int round = 0; round < 8 && !stopped; round++)
    {
      bool moved = false;
      std::fill(processed.begin(), processed.end(), false);
      for (int i : order)
      {
        CheckCancel();
        if (StopRequested())
        {
          stopped = true;
          break;
        }

        if (processed[i])
        {
          continue;
        }

        if (memberSet[i] >= 0)
        {
          for (int mm : sets[memberSet[i]])
          {
            processed[mm] = true;
          }
        }

        if (growX)
        {
          moved |= Slide(i, 0, -1, margin);
          moved |= Slide(i, -1, 0, margin);
        }
        else
        {
          moved |= Slide(i, -1, 0, margin);
          moved |= Slide(i, 0, -1, margin);
        }
      }

      if (!moved)
      {
        break;
      }
    }

    cInt weldMaxInt = NetRound(WeldMax * Scale);
    cInt marginInt = NetRound(margin * Scale);

    // order.OrderBy(...).ThenBy(...): stable over `order`, keys from the settled positions.
    std::vector<int> weldOrder(order);
    {
      std::vector<double> key1(static_cast<size_t>(n)), key2(static_cast<size_t>(n));
      for (int i = 0; i < n; i++)
      {
        key1[i] = growX ? items[i].x + items[i].minX : items[i].y + items[i].minY;
        key2[i] = growX ? items[i].y + items[i].minY : items[i].x + items[i].minX;
      }

      std::stable_sort(weldOrder.begin(), weldOrder.end(), [&](int a, int b) {
        if (key1[a] < key1[b]) { return true; }
        if (key1[b] < key1[a]) { return false; }
        return key2[a] < key2[b];
      });
    }

    for (int sweep = 0; sweep < 8 && !stopped; sweep++)
    {
      bool welded = false;
      std::fill(processed.begin(), processed.end(), false);
      for (int i : weldOrder)
      {
        CheckCancel();
        if (StopRequested())
        {
          stopped = true;
          break;
        }

        if (!items[i].hasPeers || processed[i])
        {
          continue;
        }

        if (memberSet[i] >= 0)
        {
          for (int mm : sets[memberSet[i]])
          {
            processed[mm] = true;
          }
        }

        for (int pass = 0; pass < 2; pass++)
        {
          bool alongX = growX == (pass == 0);
          cInt gap = DirectionalGapExact(i, alongX, weldMaxInt, marginInt);
          if (gap > 0 && gap <= weldMaxInt)
          {
            double g = static_cast<double>(gap) / Scale;
            double ox = alongX ? -g : 0;
            double oy = alongX ? 0 : -g;
            if (RawClearAll(i, ox, oy) && ValidOffset(i, ox, oy))
            {
              ApplyMove(i, ox, oy);
              welded = true;
            }
          }
        }
      }

      if (!welded)
      {
        break;
      }
    }
  }
};

} // namespace

extern "C" SNC_API int SNC_CALL snc_compact(
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
  const volatile int* stop)
{
  if (n < 1)
  {
    return SNC_OK;
  }

  try
  {
    std::vector<Item> items(static_cast<size_t>(n));
    size_t contour = 0;
    size_t point = 0;
    for (int i = 0; i < n; i++)
    {
      Item& it = items[i];
      it.x = xs[i];
      it.y = ys[i];
      it.spacing = spacings[i];
      it.hasPeers = has_peers[i] != 0;
      for (int c = 0; c < contour_counts[i]; c++)
      {
        int count = point_counts[contour++];
        it.contours.push_back(Contour{points + (2 * point), count});
        point += static_cast<size_t>(count);
      }

      it.minX = it.maxX = it.minY = it.maxY = 0;
      if (!it.contours.empty() && it.contours[0].count > 0)
      {
        const double* p = it.contours[0].pts;
        it.minX = it.maxX = p[0];
        it.minY = it.maxY = p[1];
        for (int k = 1; k < it.contours[0].count; k++)
        {
          double px = p[2 * k];
          double py = p[(2 * k) + 1];
          if (px < it.minX) { it.minX = px; }
          if (px > it.maxX) { it.maxX = px; }
          if (py < it.minY) { it.minY = py; }
          if (py > it.maxY) { it.maxY = py; }
        }
      }
    }

    Compactor compactor{items, shares, n, cancel, stop, {}, {}, {}, {}, {}, {}};
    compactor.Run(groups, groups_length, sheet_w, sheet_h, margin, common_line_gap);
    for (int i = 0; i < n; i++)
    {
      xs[i] = items[i].x;
      ys[i] = items[i].y;
    }

    return SNC_OK;
  }
  catch (const Cancelled&)
  {
    return SNC_CANCELLED;
  }
  catch (...)
  {
    return SNC_ERROR;
  }
}
