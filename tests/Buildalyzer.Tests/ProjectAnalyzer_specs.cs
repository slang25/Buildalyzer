using Buildalyzer;

namespace ProjectAnalyzer_specs;

public class Suffixed_binary_log_paths
{
    [TestCase("project.binlog", "net8.0", "project.net8.0.binlog")]
    [TestCase(@"C:\logs\project.binlog", "net8.0", @"C:\logs\project.net8.0.binlog")]
    [TestCase("project.binlog", "restore", "project.restore.binlog")]
    [TestCase("my project.binlog", "net8.0", "my project.net8.0.binlog")]
    public void append_suffix_before_the_extension(string path, string suffix, string expected)
        => ProjectAnalyzer.AddSuffixToBinaryLogPath(path, suffix)
            .Should().Be(expected);

    [Test]
    public void leave_empty_paths_untouched()
        => ProjectAnalyzer.AddSuffixToBinaryLogPath(string.Empty, "net8.0")
            .Should().Be(string.Empty);
}

public class Binary_logger_paths
{
    // MSBuild splits /bl parameters on ';' and takes the file name verbatim (no %3B unescaping), so
    // these can't be passed through and are rejected up front rather than failing the build with MSB1029.
    [TestCase("logs/a;b.binlog")]
    [TestCase("logs/a\"b.binlog")]
    public void reject_characters_msbuild_cannot_receive(string path)
    {
        using var ctx = Buildalyzer.TestTools.Context.ForProject(@"SdkNetStandardProject\SdkNetStandardProject.csproj");
        var analyzer = ctx.Manager.GetProject(ctx.Location.FullName);

        analyzer.Invoking(a => a.AddBinaryLogger(path))
            .Should().Throw<ArgumentException>()
            .WithParameterName("binaryLogFilePath");
    }
}
