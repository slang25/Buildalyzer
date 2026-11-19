using Buildalyzer.Environment;
using Buildalyzer.TestTools;

namespace Unrestored_projects;

// The test context deletes bin/ and obj/, so building with Restore = false analyzes a project that has
// never been restored. MSBuild reports that design-time build as a success (the SDK skips ResolvePackageAssets
// rather than raising NETSDK1004), but its compiler inputs are missing every package reference, so callers
// that retry with a restore only on failure need it reported as a failure.
public class Analyzer_Build
{
    [TestCase("SdkNetStandardProjectWithPackageReference/SdkNetStandardProjectWithPackageReference.csproj")]
    [TestCase("SdkMultiTargetingProject/SdkMultiTargetingProject.csproj")]
    public void Fails_when_the_project_has_not_been_restored(string projectFile)
    {
        using var ctx = Context.ForProject(projectFile);

        var results = ctx.Analyzer.Build(new EnvironmentOptions { Restore = false });

        results.Should().NotBeEmpty(ctx.Log.ToString());
        results.OverallSuccess.Should().BeFalse(ctx.Log.ToString());
        results.Should().AllSatisfy(r => r.Succeeded.Should().BeFalse());
        ctx.Log.ToString().Should().Contain("has not been restored");
    }

    [TestCase("SdkNetStandardProjectWithPackageReference/SdkNetStandardProjectWithPackageReference.csproj")]
    [TestCase("SdkMultiTargetingProject/SdkMultiTargetingProject.csproj")]
    public void Succeeds_once_restored(string projectFile)
    {
        using var ctx = Context.ForProject(projectFile);

        var results = ctx.Analyzer.Build(new EnvironmentOptions { Restore = true });

        results.OverallSuccess.Should().BeTrue(ctx.Log.ToString());
        results.Should().AllSatisfy(r => r.Succeeded.Should().BeTrue());
        ctx.Log.ToString().Should().NotContain("has not been restored");
    }
}
