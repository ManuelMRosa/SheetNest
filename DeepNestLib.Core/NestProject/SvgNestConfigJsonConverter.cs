namespace DeepNestLib.NestProject
{
  using DeepNestLib.Placement;
  using System;
  using System.Text.Json;
  using System.Text.Json.Serialization;

  public class SvgNestConfigJsonConverter : JsonConverterFactory
  {
    public override bool CanConvert(Type typeToConvert)
    {
      return typeToConvert.IsAssignableFrom(typeof(ISvgNestConfig)) ||
             typeToConvert.IsAssignableFrom(typeof(IPlacementConfig)) ||
             typeToConvert.IsAssignableFrom(typeof(ITopNestResultsConfig));
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
      if (CanConvert(typeToConvert))
      {
        return new SvgNestConfigJsonConverterInner();
      }

      throw new ArgumentException($"Cannot convert {nameof(typeToConvert)}.", nameof(typeToConvert));
    }

    public class SvgNestConfigJsonConverterInner : JsonConverter<ISvgNestConfig>
    {
      public override ISvgNestConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
      {
#if NCRUNCH
        return JsonSerializer.Deserialize<TestSvgNestConfig>(ref reader, options);
#else
        return JsonSerializer.Deserialize<SvgNestConfig>(ref reader, options);
#endif
      }

      public override void Write(Utf8JsonWriter writer, ISvgNestConfig value, JsonSerializerOptions options)
      {
#if NCRUNCH
        JsonSerializer.Serialize<TestSvgNestConfig>(writer, (TestSvgNestConfig)value, options);
#else
        if (value is IExportableConfig obs)
        {
          JsonSerializer.Serialize<SvgNestConfig>(writer, (SvgNestConfig)obs.ExportableInstance, options);
        }
        else
        {
          JsonSerializer.Serialize<SvgNestConfig>(writer, (SvgNestConfig)value, options);
        }
#endif
      }
    }

    internal static ISvgNestConfig FromJson(string json)
    {
      var options = new JsonSerializerOptions();
      options.Converters.Add(new SvgNestConfigJsonConverter());
#if NCRUNCH
      return JsonSerializer.Deserialize<TestSvgNestConfig>(json, options);
#else
      return JsonSerializer.Deserialize<SvgNestConfig>(json, options);
#endif
    }

    internal static string ToJson(ISvgNestConfig config)
    {
      var options = new JsonSerializerOptions();
      options.Converters.Add(new SvgNestConfigJsonConverter());
#if NCRUNCH
      return JsonSerializer.Serialize<TestSvgNestConfig>((TestSvgNestConfig)config, options);
#else
      return JsonSerializer.Serialize<SvgNestConfig>((SvgNestConfig)config, options);
#endif
    }

    /// <summary>
    /// Long hand copy of every serializable property from source to target config instances.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="target"></param>
    /// <summary>
    /// Take from a saved project only the settings that describe HOW THAT JOB WAS NESTED. Everything else
    /// in the file belongs to whoever saved it and is none of this operator's business.
    /// <para>The line is drawn at the result: a value that changes where the parts land travels with the
    /// job, because otherwise reopening it would nest it differently from the way it was cut. A value that
    /// changes how hard this machine searches, where it writes files, what it draws on screen, or what the
    /// exporter does afterwards is a preference, and opening somebody's project must not overwrite it.
    /// </para>
    /// <para>Copying the lot is what put a user on issue #2 through an afternoon of testing a checkbox
    /// that came back on after every project open. It was also quietly rewriting his last-used file paths
    /// and whether his machine uses the native library, from a file written on another machine.</para>
    /// </summary>
    internal static void CopyJobSettings(ISvgNestConfig source, ISvgNestConfig target)
    {
      // Clearances and rotation: the geometry the parts were placed to.
      target.Spacing = source.Spacing;
      target.SheetSpacing = source.SheetSpacing;
      target.Rotations = source.Rotations;
      target.StrictAngles = source.StrictAngles;

      // What the placer was allowed to do.
      target.PlacementType = source.PlacementType;
      target.UseHoles = source.UseHoles;
      target.UsePriority = source.UsePriority;

      // How the outlines themselves were approximated. Change these and the same DXF is a different shape.
      target.CurveTolerance = source.CurveTolerance;
      target.Simplify = source.Simplify;
      target.ClipByHull = source.ClipByHull;
      target.ExploreConcave = source.ExploreConcave;
      target.Tolerance = source.Tolerance;
      target.ToleranceSvg = source.ToleranceSvg;
      target.Scale = source.Scale;
      target.ClipperScale = source.ClipperScale;
    }
  }
}