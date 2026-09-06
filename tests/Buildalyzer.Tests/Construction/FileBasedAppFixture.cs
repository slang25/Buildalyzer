using System.IO;
using System.Text;
using Buildalyzer.Construction;
using Buildalyzer.IO;

namespace Buildalyzer.Tests.Construction;

public class FileBasedAppFixture
{
    [TestCase("app.cs")]
    [TestCase("APP.CS")]
    [TestCase("app.without-extension")]
    public void Recognizes_entry_points(string name)
        => FileBasedApp.IsEntryPoint(Write(name, "#!/usr/bin/env dotnet\nConsole.WriteLine();"))
            .Should().BeTrue();

    [TestCase("app.csproj")]
    [TestCase("app.vbproj")]
    [TestCase("app.fsproj")]
    public void Never_treats_a_project_file_as_an_entry_point(string name)
        => FileBasedApp.IsEntryPoint(Write(name, "#!/usr/bin/env dotnet\n<Project />"))
            .Should().BeFalse();

    [Test]
    public void Requires_a_shebang_when_the_extension_is_not_cs()
        => FileBasedApp.IsEntryPoint(Write("app.txt", "Console.WriteLine();"))
            .Should().BeFalse();

    [Test]
    public void Requires_the_file_to_exist()
        => FileBasedApp.IsEntryPoint(IOPath.Parse(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.cs")))
            .Should().BeFalse();

    private static IOPath Write(string name, string content)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"buildalyzer-{Guid.NewGuid()}"));
        var path = Path.Combine(directory.FullName, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return IOPath.Parse(path);
    }
}
