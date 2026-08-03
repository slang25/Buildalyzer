using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Buildalyzer.Environment;

internal sealed class ProcessRunner : IDisposable
{
    private readonly ILogger Logger;
    private readonly ProcessDataCollector Collector;

    public int ExitCode => Process.ExitCode;

    public ProcessData Data => Collector.Data;

    private Process Process { get; }

    public Action Exited { get; set; }

    public ProcessRunner(
        string fileName,
        string arguments,
        string workingDirectory,
        Dictionary<string, string?> environmentVariables,
        ILoggerFactory? loggerFactory)
    {
        Logger = loggerFactory?.CreateLogger<ProcessRunner>() ?? NullLogger<ProcessRunner>.Instance;
        Process = new Process
        {
            StartInfo =
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },

            // Raises Process.Exited immediately instead of when checked via .WaitForExit() or .HasExited
            EnableRaisingEvents = true,
        };

        // Copy over environment variables
        if (environmentVariables != null)
        {
            foreach (KeyValuePair<string, string> variable in environmentVariables)
            {
                Process.StartInfo.Environment[variable.Key] = variable.Value;
                Process.StartInfo.EnvironmentVariables[variable.Key] = variable.Value;
            }
        }

        Process.OutputDataReceived += OutputDataReceived;
        Process.ErrorDataReceived += ErrorDataReceived;
        Process.Exited += OnExit;

        Collector = new(Process);
    }

    public ProcessRunner Start()
    {
        Process.Start();
        Process.BeginOutputReadLine();
        Process.BeginErrorReadLine();
        Logger.LogDebug(
            "Started process {ProcessId}: \"{FileName}\" {Arguments}{NewLine}",
            Process.Id,
            Process.StartInfo.FileName,
            Process.StartInfo.Arguments,
            System.Environment.NewLine);
        return this;
    }

    /// <summary>Waits for the process to exit.</summary>
    /// <remarks>
    /// Deliberately not <see cref="Process.WaitForExit()"/>: with output redirected that overload also waits
    /// for the standard output and error streams to reach end of file, and those streams outlive the build.
    /// MSBuild leaves task host nodes running after it exits and they inherited the write end, so end of file
    /// can arrive minutes later - a build that finished in seconds would block here until it does. The
    /// timeout overload waits on the process alone (which is why the documentation tells you to follow it
    /// with the parameterless one to flush the handlers), so loop on that instead. The output collected here
    /// only feeds debug logging, so losing whatever tail is still in flight costs nothing.
    /// </remarks>
    public void WaitForExit()
    {
        while (!Process.WaitForExit(WaitInterval))
        {
        }
    }

    /// <summary>Waits for the process to exit, and for its output to be collected, up to a timeout.</summary>
    public bool WaitForExit(int timeout)
    {
        bool exited = Process.WaitForExit(timeout);
        if (exited)
        {
            // The process has gone, but its asynchronous output handlers may not have run yet and callers of
            // this overload read Data. Wait on the collector rather than calling the parameterless
            // WaitForExit() the documentation suggests, whose wait for end of file is unbounded (see above).
            Collector.WaitForCompletion(timeout);
        }
        return exited;
    }

    private void OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            Logger.LogDebug("{Data}{NewLine}", e.Data, NewLine);
        }
    }

    private void ErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            Logger.LogError("{Data}{NewLine}", e.Data, NewLine);
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        Exited?.Invoke();
        Logger.LogDebug(
            "Process {Id} exited with code {ExitCode}{NewLine}",
            Process.Id,
            Process.ExitCode,
            NewLine);
    }

    public void Dispose()
    {
        Process.OutputDataReceived -= OutputDataReceived;
        Process.ErrorDataReceived -= ErrorDataReceived;
        Process.Exited -= OnExit;
        Process.Close();
        Collector.Dispose();
    }

    private static string NewLine => System.Environment.NewLine;

    /// <summary>How long a single wait on the process handle lasts before it is repeated.</summary>
    private const int WaitInterval = 60_000;
}
