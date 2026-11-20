using System.Collections.Concurrent;
using XenoAtom.MsBuildPipeLogger;

namespace Buildalyzer;

public class AnalyzerResults : IAnalyzerResults
{
    private readonly ConcurrentDictionary<string, IAnalyzerResult> _results = new();

    private bool? _overallSuccess = null;

    public bool OverallSuccess => _overallSuccess == true;

    internal void Add(IEnumerable<IAnalyzerResult> results, bool overallSuccess)
    {
        foreach (IAnalyzerResult result in results)
        {
            _results[result.TargetFramework ?? string.Empty] = result;
        }
        _overallSuccess = _overallSuccess.HasValue ? _overallSuccess.Value && overallSuccess : overallSuccess;
    }

    // A multi-targeted project's outer (cross-targeting) build evaluates the project with TargetFrameworks set
    // and no TargetFramework, and never compiles anything: it only dispatches to the per-framework inner builds.
    // Its evaluation is read internally - target-framework discovery takes TargetFrameworks from it - but it is
    // not a result of its own, so it is dropped before results are handed out. Otherwise it surfaces as a
    // result with no target framework, no sources and no references (keyed ""), as it did when replaying a
    // multi-targeted build's or restore's binary log.
    internal void RemoveCrossTargetingOuterBuilds()
    {
        foreach (string key in _results.Where(entry => IsCrossTargetingOuterBuild(entry.Value)).Select(entry => entry.Key).ToList())
        {
            _results.TryRemove(key, out _);
        }
    }

    internal static bool IsCrossTargetingOuterBuild(IAnalyzerResult result)
        => !(result.Properties.TryGetValue("TargetFramework", out string? targetFramework) && !string.IsNullOrWhiteSpace(targetFramework))
        && result.Properties.TryGetValue("TargetFrameworks", out string? targetFrameworks)
        && !string.IsNullOrWhiteSpace(targetFrameworks);

    /// <summary>
    /// The raw build events collected during the analysis. This is an internal implementation
    /// detail: the events are XenoAtom.MsBuildPipeLogger types and are not part of the public API.
    /// </summary>
    internal ImmutableArray<PipeBuildEventArgs> BuildEventArguments { get; set; } = [];

    // A result with no target framework (a legacy project without a moniker, say) is filed under "" by Add, so
    // a null lookup - result.TargetFramework passed straight back in - finds it rather than throwing.
    public IAnalyzerResult this[string targetFramework] => _results[targetFramework ?? string.Empty];

    public IEnumerable<string> TargetFrameworks => _results.Keys.OrderBy(e => e, TargetFrameworkComparer.Instance);

    public IEnumerable<IAnalyzerResult> Results => TargetFrameworks.Select(e => _results[e]);

    public int Count => _results.Count;

    public bool ContainsTargetFramework(string targetFramework) => _results.ContainsKey(targetFramework ?? string.Empty);

    public bool TryGetTargetFramework(string targetFramework, out IAnalyzerResult result) => _results.TryGetValue(targetFramework ?? string.Empty, out result);

    public IEnumerator<IAnalyzerResult> GetEnumerator() => Results.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class TargetFrameworkComparer : IComparer<string>
    {
        public static TargetFrameworkComparer Instance { get; } = new TargetFrameworkComparer();

        public int Compare(string x, string y)
        {
            return (string.IsNullOrEmpty(x), string.IsNullOrEmpty(y)) switch
            {
                (true, true) => 0,
                (true, false) => +1,
                (false, true) => -1,
                _ => StringComparer.OrdinalIgnoreCase.Compare(x, y)
            };
        }
    }
}
