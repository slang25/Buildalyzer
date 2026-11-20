using System.IO;
using Buildalyzer;
using Buildalyzer.Logging;

namespace AnalyzerResults_specs;

public class Lookup_by_target_framework
{
    // A result with no target framework is filed under "", so handing its own (null) TargetFramework back to
    // the lookups has to find it rather than throw from the dictionary's null-key check.
    [Test]
    public void finds_a_result_without_a_target_framework_by_its_null_framework()
    {
        AnalyzerResult result = Results.Create();
        AnalyzerResults results = new() { { [result], true } };

        result.TargetFramework.Should().BeNull();
        results.TryGetTargetFramework(result.TargetFramework, out IAnalyzerResult found).Should().BeTrue();
        found.Should().BeSameAs(result);
        results.ContainsTargetFramework(result.TargetFramework).Should().BeTrue();
        results[result.TargetFramework].Should().BeSameAs(result);
    }
}

public class Cross_targeting_outer_builds
{
    [Test]
    public void are_removed_leaving_the_inner_builds()
    {
        AnalyzerResult outer = Results.Create(("TargetFrameworks", "net8.0;netstandard2.0"));
        AnalyzerResult inner = Results.Create(("TargetFrameworks", "net8.0;netstandard2.0"), ("TargetFramework", "net8.0"));
        AnalyzerResults results = new() { { [outer, inner], true } };

        results.RemoveCrossTargetingOuterBuilds();

        results.Should().ContainSingle().Which.Should().BeSameAs(inner);
        results.OverallSuccess.Should().BeTrue();
    }

    [Test]
    public void leave_a_project_that_is_not_multi_targeted_alone()
    {
        AnalyzerResult legacy = Results.Create(("TargetFrameworkIdentifier", ".NETFramework"), ("TargetFrameworkVersion", "v4.8"));
        AnalyzerResults results = new() { { [legacy], true } };

        results.RemoveCrossTargetingOuterBuilds();

        results.Should().ContainSingle().Which.Should().BeSameAs(legacy);
    }
}

internal static class Results
{
    public static AnalyzerResult Create(params (string Name, string Value)[] properties)
    {
        AnalyzerResult result = new(Path.Combine(Path.GetTempPath(), "project.csproj"), new AnalyzerManager(), null);
        result.ProcessProject(new PropertiesAndItems
        {
            Properties = new CompilerProperties(properties.Select(p => new KeyValuePair<string, object>(p.Name, p.Value))),
            Items = CompilerItemsCollection.Empty,
        });
        return result;
    }
}
