using System.IO;
using Buildalyzer;
using Buildalyzer.Environment;
using Buildalyzer.TestTools;

namespace Evaluation_data_on_live_builds;

/// <summary>
/// A project's properties and items normally arrive on ProjectStarted. As soon as any logger in the build
/// opts in to IncludeEvaluationPropertiesAndItems, MSBuild moves them onto ProjectEvaluationFinished for every
/// logger instead - and a binary log opts in. These are the live results of such builds, which used to come
/// back with no properties, no target framework and the evaluation-time items missing, every inner build of a
/// multi-targeted project collapsed into one result. Only replaying the binary log gave the full picture.
/// </summary>
[NonParallelizable]
public class With_a_binary_log_attached
{
    [TestCase("SdkNetStandardProject/SdkNetStandardProject.csproj", false)]
    [TestCase("ParallelMultiTargetProject/ParallelMultiTargetProject.csproj", true)]
    public void match_the_replayed_log(string projectFile, bool multiTargeted)
    {
        using ProjectFileTestContext context = Context.ForProject(projectFile);
        string directory = Directory.CreateTempSubdirectory("buildalyzer-live-binlog-").FullName;
        string binaryLogPath = Path.Combine(directory, "build.binlog");

        try
        {
            context.Analyzer.AddBinaryLogger(binaryLogPath);

            IAnalyzerResults live = context.Analyzer.Build();

            live.OverallSuccess.Should().BeTrue(context.Log.ToString());
            live.Should().HaveCount(multiTargeted ? 2 : 1);
            foreach (IAnalyzerResult result in live)
            {
                result.TargetFramework.Should().NotBeNullOrEmpty();

                // A multi-targeted project's binary log is written per framework.
                string log = multiTargeted ? Path.Combine(directory, $"build.{result.TargetFramework}.binlog") : binaryLogPath;
                IAnalyzerResult replayed = context.Manager.Analyze(log).Should().ContainSingle().Subject;

                replayed.TargetFramework.Should().Be(result.TargetFramework);
                result.Properties.Should().BeEquivalentTo(replayed.Properties);
                ItemSpecs(result).Should().BeEquivalentTo(ItemSpecs(replayed));
                result.SourceFiles.Should().BeEquivalentTo(replayed.SourceFiles);
                result.References.Should().BeEquivalentTo(replayed.References);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // The restore Buildalyzer runs ahead of a multi-targeted project's per-framework builds only evaluates the
    // outer, cross-targeting project - it compiles nothing, so there is no result in its log.
    [Test]
    public void write_a_restore_log_that_replays_to_no_results()
    {
        using ProjectFileTestContext context = Context.ForProject("ParallelMultiTargetProject/ParallelMultiTargetProject.csproj");
        string directory = Directory.CreateTempSubdirectory("buildalyzer-live-binlog-").FullName;

        try
        {
            context.Analyzer.AddBinaryLogger(Path.Combine(directory, "build.binlog"));

            context.Analyzer.Build().OverallSuccess.Should().BeTrue(context.Log.ToString());

            context.Manager.Analyze(Path.Combine(directory, "build.restore.binlog")).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Dictionary<string, string[]> ItemSpecs(IAnalyzerResult result)
        => result.Items.ToDictionary(group => group.Key, group => group.Value.Select(item => item.ItemSpec).ToArray());
}

/// <summary>
/// The same move, forced by MSBuild's escape hatch rather than by a binary log: with nothing to replay, a live
/// build that drops the evaluation data has no results at all.
/// </summary>
[NonParallelizable]
public class With_evaluation_data_moved_off_ProjectStarted
{
    [TestCase("SdkNetStandardProject/SdkNetStandardProject.csproj")]
    [TestCase("ParallelMultiTargetProject/ParallelMultiTargetProject.csproj")]
    public void match_a_build_without(string projectFile)
    {
        const string EscapeHatch = "MSBUILDLOGPROPERTIESANDITEMSAFTEREVALUATION";
        using ProjectFileTestContext context = Context.ForProject(projectFile);
        EnvironmentOptions moved = new();
        moved.EnvironmentVariables[EscapeHatch] = "1";

        IAnalyzerResults expected = context.Analyzer.Build();
        IAnalyzerResults actual = context.Analyzer.Build(moved);

        actual.OverallSuccess.Should().BeTrue(context.Log.ToString());
        actual.TargetFrameworks.Should().BeEquivalentTo(expected.TargetFrameworks).And.NotContain(string.Empty);
        foreach (IAnalyzerResult result in actual)
        {
            IAnalyzerResult baseline = expected[result.TargetFramework];
            // Environment variables surface as properties, the escape hatch included.
            result.Properties.Keys.Except([EscapeHatch]).Should().BeEquivalentTo(baseline.Properties.Keys);
            result.Items.Keys.Should().BeEquivalentTo(baseline.Items.Keys);
            result.SourceFiles.Should().BeEquivalentTo(baseline.SourceFiles);
            result.References.Should().BeEquivalentTo(baseline.References);
        }
    }
}
