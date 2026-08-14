using System.IO;
using Buildalyzer;
using Buildalyzer.TestTools;

namespace AnalyzerManager_specs;

public class Tracks_one_analyzer_per_project
{
    [Test]
    public void when_the_same_project_is_requested_by_differently_written_paths()
    {
        using var ctx = Context.ForProject(@"SdkNetStandardProject\SdkNetStandardProject.csproj");

        // The same file, written with a redundant parent segment. Analyzers are keyed by path, so
        // without normalization this hands back a second analyzer for the project already tracked.
        string detour = Path.Combine(
            ctx.Location.DirectoryName!,
            "..",
            ctx.Location.Directory!.Name,
            ctx.Location.Name);

        var analyzer = ctx.Manager.GetProject(ctx.Location.FullName);

        ctx.Manager.GetProject(detour).Should().BeSameAs(analyzer);
        ctx.Manager.Projects.Should().ContainSingle();
    }
}
