using System.Collections.Generic;
using Basic.CompilerLog.Util;

namespace Buildalyzer.CompilerLogs;

/// <summary>
/// The result of writing Buildalyzer build results to a compiler log.
/// </summary>
public sealed class CompilerLogCreateResult
{
    internal CompilerLogCreateResult(bool succeeded, IReadOnlyList<CompilerCall> compilerCalls, IReadOnlyList<string> diagnostics)
    {
        Succeeded = succeeded;
        CompilerCalls = compilerCalls;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// <c>true</c> when every build result with a supported compiler command was written to the log.
    /// The log may still be usable when <c>false</c>; <see cref="CompilerCalls"/> lists what was written.
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    /// The compiler calls written to the log, one per built target framework.
    /// </summary>
    public IReadOnlyList<CompilerCall> CompilerCalls { get; }

    /// <summary>
    /// Human-readable notes produced while writing the log, one per line, for display or logging; they are
    /// not meant to be parsed. They name each project that could not be written and why (no compiler
    /// command line was captured because its build stopped before the compiler ran, or adding it failed),
    /// each project skipped because it is not C# or Visual Basic, and anything raised by the underlying
    /// compiler log writer (missing files, unreadable content and the like).
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; }
}
