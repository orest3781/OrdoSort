using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Runs scheduled work inline on the calling thread, so every shell
/// flow completes synchronously by the time the call returns — tests assert
/// immediately after driving the view model, no pumping or sleeping.</summary>
public sealed class InlineWorkScheduler : IWorkScheduler
{
    public Task<T> Run<T>(Func<T> work) => Task.FromResult(work());

    public Task Run(Action work)
    {
        work();
        return Task.CompletedTask;
    }

    /// <summary>Surfaces the outcome of work started on this scheduler,
    /// which has already finished by the time the call returns. Fails the
    /// test if it has not, rather than blocking on it.</summary>
    public static void Finished(Task task)
    {
        Assert.True(task.IsCompleted, "work on the inline scheduler should have finished before the call returned");
        task.GetAwaiter().GetResult();
    }
}
