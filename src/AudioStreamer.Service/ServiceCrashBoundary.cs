namespace AudioStreamer.Service;

/// <summary>Records process-level failures before the host's bounded log providers are disposed.</summary>
public sealed class ServiceCrashBoundary : IDisposable
{
    private readonly ILogger<ServiceCrashBoundary> _logger;
    private int _disposed;

    public ServiceCrashBoundary(ILogger<ServiceCrashBoundary> logger)
    {
        _logger = logger;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        var exceptionType = (args.ExceptionObject as Exception)?.GetType().FullName ?? "UnknownException";
        _logger.LogCritical("Process encountered an unhandled exception. ExceptionType={ExceptionType} IsTerminating={IsTerminating}",
            exceptionType, args.IsTerminating);
        if (args.IsTerminating) Environment.ExitCode = 1;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        _logger.LogError("A background task exception was not observed. ExceptionType={ExceptionType}",
            args.Exception.GetType().FullName ?? "UnknownException");
        args.SetObserved();
    }
}
