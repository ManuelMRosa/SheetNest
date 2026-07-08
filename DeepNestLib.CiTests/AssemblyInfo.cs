// The suite mutates process-wide static state (notably SvgNest.Config) from many fixtures, so
// running xunit collections in parallel made ~70-80 tests fail non-deterministically (all pass in
// isolation). Serializing the collections makes the suite deterministic; the ~10s runtime is fine.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
