using System;
using System.Collections.Generic;
using System.IO;
using Basic.CompilerLog.Util;

namespace Buildalyzer.CompilerLogs;

/// <summary>
/// Creates compiler log (.complog) files from Buildalyzer build results.
/// </summary>
/// <remarks>
/// The compiler log is built from the raw csc/vbc command line Buildalyzer captured from the
/// build, so it carries everything the compiler was actually given — including the emit-time
/// inputs (embedded resources, Win32 manifest/icon/resource, source link, app.config, strong
/// name key, rulesets) that a Roslyn workspace cannot surface. The referenced file contents are
/// read from disk at the time of this call, so create the log while the build outputs are still
/// in place.
/// <para>
/// A compiler log records compiler <em>invocations</em>, not their outcome, so a compilation that
/// fails with errors is logged like any other: its command line was captured, and replaying the log
/// reproduces the same errors. What cannot be logged is a project whose build stopped before
/// <c>CoreCompile</c> invoked the compiler (a failed restore, say), because there is no command line
/// to record. Such a project is reported in the diagnostics rather than silently left out.
/// </para>
/// <para>
/// The shape of this API follows <c>Basic.CompilerLog.Util</c>'s own <c>CompilerLogUtil.ConvertBinaryLog</c>
/// and <c>TryConvertBinaryLog</c>: a throwing variant that returns the diagnostics, and a <c>Try</c>
/// variant that returns them alongside the compiler calls that were written.
/// </para>
/// </remarks>
public static class AnalyzerResultsExtensions
{
    /// <summary>
    /// Creates a compiler log file from the build results and returns the diagnostics.
    /// </summary>
    /// <returns>
    /// Human-readable notes about the log, one per line, for display or logging; they are not meant to be
    /// parsed. When this method returns, every C# and Visual Basic result was written, so these are only
    /// informational: a project skipped because it is not C# or Visual Basic, or a warning from the underlying
    /// writer.
    /// </returns>
    /// <exception cref="CompilerLogException">
    /// The log could not be fully created: a result has no captured compiler command line (its build stopped
    /// before the compiler was invoked), adding a compiler call failed, or nothing was written at all. The
    /// exception carries the same diagnostics, including one naming each project that could not be added.
    /// Use <see cref="TryCreateCompilerLog(IEnumerable{IAnalyzerResult}, string)"/> to keep a partial log
    /// instead of throwing.
    /// </exception>
    public static IReadOnlyList<string> CreateCompilerLog(this IEnumerable<IAnalyzerResult> results, string compilerLogFilePath)
    {
        CompilerLogCreateResult result = TryCreateCompilerLog(results, compilerLogFilePath);
        return result.Succeeded
            ? result.Diagnostics
            : throw new CompilerLogException("Could not create compiler log", [.. result.Diagnostics]);
    }

    /// <summary>
    /// Creates a compiler log file from the build results.
    /// </summary>
    /// <returns>
    /// Whether every result was written, the compiler calls that were, and the diagnostics explaining any
    /// that were not. See <see cref="CompilerLogCreateResult"/>.
    /// </returns>
    public static CompilerLogCreateResult TryCreateCompilerLog(this IEnumerable<IAnalyzerResult> results, string compilerLogFilePath)
    {
        using FileStream stream = new(compilerLogFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        return TryCreateCompilerLog(results, stream);
    }

    /// <summary>
    /// Writes a compiler log for the build results to the given stream.
    /// </summary>
    /// <inheritdoc cref="TryCreateCompilerLog(IEnumerable{IAnalyzerResult}, string)" path="/returns"/>
    public static CompilerLogCreateResult TryCreateCompilerLog(this IEnumerable<IAnalyzerResult> results, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(results);

        List<string> diagnostics = [];
        List<CompilerCall> compilerCalls = [];
        bool succeeded = true;

        // Internal to Basic.CompilerLog.Util, reached via publicizing until an upstream producer API exists.
        using var builder = new CompilerLogBuilder(stream, diagnostics);

        foreach (IAnalyzerResult analyzerResult in results)
        {
            string name = $"{Path.GetFileName(analyzerResult.ProjectFilePath)} ({analyzerResult.TargetFramework})";

            CompilerCommand? command = (analyzerResult as AnalyzerResult)?.CompilerCommand;
            if (command is null)
            {
                diagnostics.Add($"{name}: no compiler command line was captured; the build may have failed before the compiler was invoked.");
                succeeded = false;
                continue;
            }

            if (command.Language is not (CompilerLanguage.CSharp or CompilerLanguage.VisualBasic))
            {
                diagnostics.Add($"{name}: compiler logs only support C# and Visual Basic; skipping.");
                continue;
            }

            CompilerCall compilerCall = new(
                analyzerResult.ProjectFilePath,
                CompilerCallKind.Regular,
                analyzerResult.TargetFramework,
                isCSharp: command.Language == CompilerLanguage.CSharp,
                compilerFilePath: command.CompilerLocation?.FullName);

            try
            {
                builder.AddFromDisk(compilerCall, command.Arguments);
                compilerCalls.Add(compilerCall);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"Error adding {compilerCall.GetDiagnosticName()}: {ex.Message}");
                succeeded = false;
            }
        }

        if (compilerCalls.Count == 0)
        {
            diagnostics.Add("No compiler calls were written to the log.");
            succeeded = false;
        }

        builder.Close();
        return new CompilerLogCreateResult(succeeded, compilerCalls, diagnostics);
    }
}
