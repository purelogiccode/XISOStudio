namespace XISOStudio.Services;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes its callback synchronously on the reporting
/// thread. Unlike <see cref="Progress{T}"/>, which posts callbacks to the captured
/// synchronization context (the thread pool when none is present), this preserves the
/// order in which values were reported and prevents concurrent callbacks from racing on
/// adapter state such as the last reported percentage.
/// </summary>
/// <typeparam name="T">Type of the reported progress value.</typeparam>
internal sealed class SynchronousProgress<T> : IProgress<T>
{
    private readonly Action<T> _callback;

    /// <summary>
    /// Initializes a new instance of the <see cref="SynchronousProgress{T}"/> class.
    /// </summary>
    /// <param name="callback">Callback invoked for every reported value.</param>
    internal SynchronousProgress(Action<T> callback)
    {
        _callback = callback;
    }

    /// <inheritdoc />
    public void Report(T value)
    {
        _callback(value);
    }
}