using OrdoSort.Wpf.Services;

namespace OrdoSort.Smoke.E2E.Scenarios;

/// <summary>Runs scheduled work inline so a scenario's assertions follow the
/// call rather than a sleep. Mirrors OrdoSort.Wpf.Tests.InlineWorkScheduler,
/// duplicated here because the test project's types are not visible to the
/// Smoke tool — the project dependency runs the other way.
///
/// Its own file, not the bottom of a surface's scenario file: every surface
/// constructs its view models with this, so a type nine files depend on should
/// not be reachable only by opening the Zip scenarios.
///
/// Note what this is and is not. Swapping the SCHEDULER for an inline one is
/// legitimate — it removes thread-pool timing from the run without removing any
/// of the app's own logic. Swapping a view model's WORK seam
/// (zipper/extractor/merger/counter/unlocker/plan) would be the opposite: it
/// would replace the thing under test. Those stay at their defaults in every
/// scenario.</summary>
internal sealed class InlineScheduler : IWorkScheduler
{
    public Task<T> Run<T>(Func<T> work) => Task.FromResult(work());
    public Task Run(Action work) { work(); return Task.CompletedTask; }
}
