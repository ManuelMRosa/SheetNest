namespace DeepNestLib.CiTests
{
  using System;
  using System.IO;
  using DeepNestLib.IO;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Exercises the real STEP -> FreeCAD unfold -> DXF -> IRawDetail pipeline. Self-skips when
  /// FreeCAD or the sample file are not present (e.g. CI runners without FreeCAD installed).
  /// </summary>
  public class StepUnfoldServiceFixture
  {
    private const string FreeCadCmd = @"C:\Users\rosam\AppData\Local\Programs\FreeCAD 1.1\bin\freecadcmd.exe";
    private const string SampleStep = @"C:\Users\rosam\AppData\Local\Temp\claude\C--Users-rosam\3a4d8e47-655d-44b2-b6ff-61ae4b2c37f6\scratchpad\steps\picogus_bracket.step";
    private const string MacroSrc = @"C:\Users\rosam\source\repos\DeepNestSharp\assets\freecad\unfold_macro.py";

    [Fact]
    public void IsStepFile_RecognisesExtensions()
    {
      StepUnfoldService.IsStepFile("a.step").Should().BeTrue();
      StepUnfoldService.IsStepFile("a.STP").Should().BeTrue();
      StepUnfoldService.IsStepFile("a.iges").Should().BeTrue();
      StepUnfoldService.IsStepFile("a.dxf").Should().BeFalse();
      StepUnfoldService.IsStepFile(null).Should().BeFalse();
    }

    [Fact]
    public void UnfoldsRealStepToNestablePart()
    {
      if (!File.Exists(FreeCadCmd) || !File.Exists(SampleStep) || !File.Exists(MacroSrc))
      {
        return; // FreeCAD / sample not available (CI) — skip.
      }

      File.Copy(MacroSrc, Path.Combine(AppContext.BaseDirectory, "unfold_macro.py"), true);
      StepUnfoldService.FreeCadCmdPathOverride = FreeCadCmd;

      var raw = StepUnfoldService.LoadAsRawDetail(SampleStep);

      raw.Should().NotBeNull();
      var nfp = raw.ToNfp();
      nfp.Should().NotBeNull();
      nfp.Points.Length.Should().BeGreaterThan(2, "the unfolded flat pattern must have a real outline");
      nfp.Area.Should().BeGreaterThan(0);
    }
  }
}
