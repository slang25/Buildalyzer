using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Build.Framework;
using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer.Logger;

public class BuildalyzerLogger : PipeLogger
{
    private string _pipeHandleAsString = string.Empty;
    private bool _logEverything;

    public override void Initialize(IEventSource eventSource)
    {
        // Parse the parameters
        string[] parameters = [.. (Parameters?.Split(';').Select(x => x.Trim())).OfType<string>()];
        if (parameters.Length != 2)
        {
            throw new LoggerException("Unexpected number of parameters");
        }
        _pipeHandleAsString = parameters[0];
        if (!bool.TryParse(parameters[1], out _logEverything))
        {
            throw new LoggerException("Second parameter (log everything) should be a bool");
        }

        base.Initialize(eventSource);
    }

    protected override void InitializeEnvironmentVariables()
    {
        // Only register the extra logging environment variables if logging everything
        if (_logEverything)
        {
            base.InitializeEnvironmentVariables();
        }
    }

    protected override IPipeWriter InitializePipeWriter() => new AnonymousPipeWriter(_pipeHandleAsString);

    // The base class only exposes its writer as a nullable property, because it is created during Initialize.
    // Every handler below runs after Initialize has returned, so the writer is always there by then.
    private IPipeWriter Writer => Pipe ?? throw new InvalidOperationException("The pipe writer has not been initialized.");

    protected override void InitializeEvents(IEventSource eventSource)
    {
        if (eventSource is null)
        {
            throw new ArgumentNullException(nameof(eventSource));
        }

        if (_logEverything)
        {
            base.InitializeEvents(eventSource);
            return;
        }

        // Only log what we need for Buildalyzer
        eventSource.ProjectStarted += (_, e) => Writer.Write(e);
        eventSource.ProjectFinished += (_, e) => Writer.Write(e);
        eventSource.BuildFinished += (_, e) => Writer.Write(e);
        eventSource.ErrorRaised += (_, e) => Writer.Write(e);
        eventSource.TargetStarted += TargetStarted;
        eventSource.TargetFinished += TargetFinished;
        eventSource.MessageRaised += MessageRaised;

        // Ask MSBuild to log task input parameters, so the compiler task's resolved Sources/References/etc.
        // are available as structured TaskParameter events. This is the same opt-in the binary logger uses;
        // it does not require diagnostic verbosity, and we only forward the compiler task's parameters below.
        // IEventSource4 is deliberately a version sentinel, not the minimal interface: IncludeTaskInputs is
        // declared on IEventSource3 (MSBuild 15.3), but on 15.3-16.3 it only logs task inputs as plain text
        // messages - the structured TaskParameterEventArgs consumed below shipped in 16.4, the same release
        // as IEventSource4. Gating on IEventSource4 both avoids a useless opt-in on 15.3-16.3 and keeps the
        // AnyEventRaised handler (whose body would fail to JIT where TaskParameterEventArgs is missing) off
        // older MSBuilds. Without it Buildalyzer falls back to the evaluation-time items.
        TryIncludeTaskInputs(eventSource);
    }

    // The JIT resolves the types a method names when it compiles that method, not when the line naming them
    // runs. On an MSBuild whose Framework assembly predates IEventSource4, compiling IncludeTaskInputs therefore
    // throws a TypeLoadException. That compilation happens at the call below, inside the try, and the exception
    // is caught there: this method names none of the newer types, so the logger still initializes and
    // Buildalyzer falls back to the evaluation-time items.
    private void TryIncludeTaskInputs(IEventSource eventSource)
    {
        try
        {
            IncludeTaskInputs(eventSource);
        }
        catch (TypeLoadException)
        {
            // IEventSource4 (MSBuild 16.4+) is not available.
        }
    }

    // NoInlining is what makes the catch in TryIncludeTaskInputs work. If the JIT inlined this method into its
    // caller, IEventSource4 and TaskParameterEventArgs would be resolved while compiling TryIncludeTaskInputs,
    // before its try block is entered, and the TypeLoadException would escape Initialize and fail the build.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void IncludeTaskInputs(IEventSource eventSource)
    {
        if (eventSource is IEventSource4 eventSource4)
        {
            eventSource4.IncludeTaskInputs();

            // TaskParameterEventArgs are dispatched via AnyEventRaised (TaskStarted isn't delivered at normal
            // verbosity). Scope forwarding to the CoreCompile target - where the compiler task runs - so we
            // get the compiler's resolved inputs and not, say, the References fed to other tasks. Keeps the
            // pipe lean.
            eventSource.AnyEventRaised += OnAnyEventRaised;
        }
    }

    private void OnAnyEventRaised(object sender, BuildEventArgs e)
    {
        // The evaluation's properties and items normally ride on ProjectStarted. Once any logger opts in to
        // IncludeEvaluationPropertiesAndItems - /bl does, so does the dotnet CLI's telemetry logger, and
        // MSBUILDLOGPROPERTIESANDITEMSAFTEREVALUATION=1 forces it - MSBuild moves them onto
        // ProjectEvaluationFinished instead, for every logger: central loggers share one event sink, so this
        // one's ProjectStarted arrives empty too. Forward the evaluation whenever it carries the data. This
        // handler is only attached where IEventSource4 exists, which is exactly where the data can move: the
        // opt-in that moves it is that interface's only member. On a live build these collections hold
        // MSBuild's own property and item instances rather than the DictionaryEntry pairs a replayed log
        // carries; XenoAtom.MsBuildPipeLogger serializes both from 2.0.3 on.
        if (e is ProjectEvaluationFinishedEventArgs evaluation)
        {
            if (evaluation.Properties is not null || evaluation.Items is not null)
            {
                Writer.Write(evaluation);
            }

            return;
        }

        if (e is not TaskParameterEventArgs parameter)
        {
            return;
        }

        // Forward the compiler task's resolved inputs (captured inside CoreCompile) and the item groups the
        // SDK resolves before it - ResolveAssemblyReference's ReferencePath, and the analyzers that
        // ResolveTargetingPackAssets (into Analyzer) and ResolvePackageAssets (into ResolvedAnalyzers) produce -
        // so the workspace can still be reconstructed when the build fails before CoreCompile runs (issue #341).
        bool compilerInput = ProjectContextId(parameter) is { } contextId
            && _coreCompileContexts.Contains(contextId)
            && parameter.Kind == TaskParameterMessageKind.TaskInput
            && IsCompilerInput(parameter.ItemType);
        bool resolvedBeforeCompile = parameter.Kind == TaskParameterMessageKind.TaskOutput
            && IsResolvedBeforeCompile(parameter.ItemType);

        if (compilerInput || resolvedBeforeCompile)
        {
            Writer.Write(e);
        }
    }

    // The project contexts currently inside CoreCompile. This logger is a central logger, so under /m it sees
    // every project's events interleaved on one thread: a single "in CoreCompile" flag would be cleared by
    // whichever project's CoreCompile finished first, silently dropping the task inputs of every compiler
    // still running. Targets don't nest within a project context, so a set of context ids is enough.
    private readonly HashSet<int> _coreCompileContexts = [];

    // MSBuild stamps every target and task event with a build context, so a missing one is not expected in
    // practice. Should one arrive, it can't be attributed to a project: it never enters or leaves CoreCompile
    // tracking, and its task inputs are not forwarded as compiler inputs.
    //
    // The id alone is a safe key even under /m: a project-context id is unique across the whole build, not
    // just its own node. MSBuild's LoggingService seeds each node's counter with the node id and advances it
    // by MaxCPUCount + 2, so nodes allocate from disjoint ranges and never collide.
    private static int? ProjectContextId(BuildEventArgs e) => e.BuildEventContext?.ProjectContextId;

    // MSBuild only forwards ITaskItem[] task inputs at normal verbosity (not scalar string parameters such
    // as DefineConstants), so only the compiler's item-group inputs are listed here. Preprocessor symbols are
    // recovered from the compiler command line instead.
    private static bool IsCompilerInput(string? itemType) => itemType is
        "Sources" or "References" or "Analyzers" or "AdditionalFiles" or "AnalyzerConfigFiles" or "EmbeddedFiles";

    // The item groups the SDK's resolution tasks output ahead of CoreCompile, kept so a build that fails before
    // the compiler runs still yields its resolved references and analyzers. MSBuild item names are
    // case-insensitive, unlike the task parameter names above.
    private static bool IsResolvedBeforeCompile(string? itemType) =>
        itemType.IsMatch("ReferencePath") || itemType.IsMatch("Analyzer") || itemType.IsMatch("ResolvedAnalyzers");

    private void TargetStarted(object sender, TargetStartedEventArgs e)
    {
        // Only send the CoreCompile target
        if (e.TargetName == "CoreCompile")
        {
            if (ProjectContextId(e) is { } contextId)
            {
                _coreCompileContexts.Add(contextId);
            }

            Writer.Write(e);
        }
    }

    private void TargetFinished(object sender, TargetFinishedEventArgs e)
    {
        // Only send the CoreCompile target
        if (e.TargetName == "CoreCompile")
        {
            if (ProjectContextId(e) is { } contextId)
            {
                _coreCompileContexts.Remove(contextId);
            }

            Writer.Write(e);
        }
    }

    private void MessageRaised(object sender, BuildMessageEventArgs e)
    {
        // Only send if in the Csc Vbc, or the Fsc task
        if ((e is TaskCommandLineEventArgs cmd && cmd.TaskName.IsMatch("Csc"))
            || e.SenderName.IsMatch("Fsc")
            || e.SenderName.IsMatch("Vbc"))
        {
            Writer.Write(e);
        }
    }
}
