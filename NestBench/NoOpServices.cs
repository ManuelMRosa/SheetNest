namespace NestBench
{
  using System;
  using System.Collections.Generic;
  using System.Threading;
  using System.Threading.Tasks;
  using DeepNestLib;
  using DeepNestLib.Placement;

  /// <summary>Swallows all UI messages; only surfaces exceptions to stderr.</summary>
  internal sealed class NoOpMessageService : IMessageService
  {
    public void DisplayMessage(string message) { }

    public void DisplayMessage(Exception ex) => Console.Error.WriteLine("  [engine] " + ex.Message);

    public void DisplayMessageBox(string text, string caption, MessageBoxIcon icon) { }

    public MessageBoxResult DisplayOkCancel(string text, string caption, MessageBoxIcon icon) => default;

    public MessageBoxResult DisplayYesNoCancel(string text, string caption, MessageBoxIcon icon) => default;
  }

  /// <summary>Headless progress sink. Sets a signal on each progress callback so the
  /// driver loop can throttle without a UI message pump (mirrors the test harness).</summary>
  internal sealed class SignalProgressDisplayer : IProgressDisplayer
  {
    public readonly AutoResetEvent Signal = new AutoResetEvent(false);

    public bool IsVisibleSecondaryProgressBar { get; set; }

    public void DisplayProgress(ProgressBar progressBar, double percentageComplete) => Signal.Set();

    public void DisplayProgress(int currentPopulation, INestResult topNest) => Signal.Set();

    public void ClearTransientMessage() { }

    public void DisplayTransientMessage(string message) { }

    public void DisplayMessageBox(string text, string caption, MessageBoxIcon icon) { }

    public void UpdateNestsList() => Signal.Set();

    public void InitialiseUiForStartNest() { }

    public Task IncrementLoopProgress(ProgressBar progressBar) => Task.CompletedTask;

    public void InitialiseLoopProgress(ProgressBar progressBar, int loopMax) { }

    public void InitialiseLoopProgress(ProgressBar progressBar, string transientMessage, int loopMax) { }
  }

  /// <summary>Minimal IPlacementWorker for the post-nest re-insertion polish (re-insertion never
  /// commits via AddPlacement, so only VerboseLog is exercised).</summary>
  internal sealed class StubPlacementWorker : IPlacementWorker
  {
    public SheetPlacement AddPlacement(INfp processingPart, List<IPartPlacement> placements, INfp part, PartPlacement position, PlacementTypeEnum placementType, ISheet sheet, double mergedLength) => null;

    public void VerboseLog(string message) { }
  }

  /// <summary>Runs callbacks inline; there is no UI thread to marshal to.</summary>
  internal sealed class InlineDispatcherService : IDispatcherService
  {
    public bool InvokeRequired => false;

    public void Invoke(Action callback) => callback();

    public Task InvokeAsync(Action callback)
    {
      callback();
      return Task.CompletedTask;
    }
  }
}
