namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Linq;
  using System.Reflection;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The planner's candidate struct got its own equality so its caches stop comparing by reflection, and that
  /// equality has to be the one the runtime gave it before: ValueType.Equals, which for a struct holding
  /// references compares every field with object.Equals on the boxed value (a double by double.Equals, so +0
  /// equals -0 and NaN equals NaN; a span by reference). Equal candidates must also hash alike.
  /// </summary>
  public class DirectCandidateEqualityFixture
  {
    private static readonly Type Candidate = typeof(SparrowNestService).GetNestedType("DirectCandidate", BindingFlags.NonPublic)
      ?? throw new InvalidOperationException("SparrowNestService.DirectCandidate is gone");

    private static readonly CommonBoundarySpan SpanOne = new CommonBoundarySpan(0, 1, 0.25, 0.75);
    private static readonly CommonBoundarySpan SpanOneAgain = new CommonBoundarySpan(0, 1, 0.25, 0.75);

    [Fact]
    public void ItsEqualityIsTheRuntimesFieldByFieldOne()
    {
      var random = new Random(7);
      for (int k = 0; k < 500; k++)
      {
        object[] args = RandomArgs(random);
        object a = Make(args);
        object b = Make(Mutate(args, random));

        a.Equals(b).Should().Be(ReferenceEquals(a, b) || FieldByField(a, b), $"case {k}");
        if (a.Equals(b))
        {
          a.GetHashCode().Should().Be(b.GetHashCode(), $"case {k}: equal candidates must hash alike");
        }
      }
    }

    [Theory]
    [InlineData(0.0, -0.0, true)] // double.Equals: the two zeros are one value
    [InlineData(double.NaN, double.NaN, true)] // and NaN equals NaN
    [InlineData(1.0, 1.0000000000000002, false)]
    public void DoublesCompareAsDoubleEquals(double first, double second, bool equal)
    {
      for (int field = 6; field < 14; field++)
      {
        object[] args = BaseArgs();
        object[] other = (object[])args.Clone();
        args[field] = first;
        other[field] = second;

        object a = Make(args);
        object b = Make(other);
        a.Equals(b).Should().Be(equal, $"double field {field}");
        FieldByField(a, b).Should().Be(equal, "the runtime's answer");
        if (equal)
        {
          a.GetHashCode().Should().Be(b.GetHashCode());
        }
      }
    }

    [Fact]
    public void SpansCompareByReference()
    {
      object[] args = BaseArgs();
      object[] same = (object[])args.Clone();
      object[] twin = (object[])args.Clone();
      args[14] = SpanOne;
      same[14] = SpanOne;
      twin[14] = SpanOneAgain;

      Make(args).Equals(Make(same)).Should().BeTrue();
      Make(args).Equals(Make(twin)).Should().BeFalse("an equal-looking span is still another span");
    }

    /// <summary>What ValueType.Equals does for a struct with reference fields.</summary>
    private static bool FieldByField(object a, object b)
      => Candidate.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .All(f => object.Equals(f.GetValue(a), f.GetValue(b)));

    private static object Make(object[] args)
      => Candidate.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single().Invoke(args);

    private static object[] BaseArgs()
      => new object[] { 1, 2, 0, 1, 3, 4, 0.0, 1.0, 0.0, 1.0, 0.5, -0.25, 7.5, 0.1, null, null };

    private static object[] RandomArgs(Random random)
    {
      object[] args = BaseArgs();
      for (int i = 0; i < 6; i++)
      {
        args[i] = random.Next(3);
      }

      for (int i = 6; i < 14; i++)
      {
        args[i] = Pick(random);
      }

      args[14] = random.Next(3) switch { 0 => null, 1 => SpanOne, _ => SpanOneAgain };
      args[15] = random.Next(2) == 0 ? null : SpanOne;
      return args;
    }

    /// <summary>The same arguments, often unchanged, otherwise with one field changed.</summary>
    private static object[] Mutate(object[] args, Random random)
    {
      object[] other = (object[])args.Clone();
      if (random.Next(3) == 0)
      {
        return other;
      }

      int field = random.Next(16);
      other[field] = field switch
      {
        < 6 => (int)other[field] + 1,
        < 14 => Pick(random),
        _ => random.Next(3) switch { 0 => null, 1 => SpanOne, _ => SpanOneAgain },
      };
      return other;
    }

    private static double Pick(Random random) => random.Next(6) switch
    {
      0 => 0.0,
      1 => -0.0,
      2 => double.NaN,
      3 => 0.5,
      4 => 1.0,
      _ => random.NextDouble(),
    };
  }
}
