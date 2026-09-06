using System.IO;
using Buildalyzer.IO;

namespace Buildalyzer.Construction;

/// <summary>
/// Recognizes file-based apps: a C# program written as a single source file that the .NET SDK builds
/// against a project it generates rather than one that exists on disk.
/// </summary>
/// <remarks>
/// The rules are Roslyn's (<c>ProjectFileExtensionRegistry.TryGetLanguageNameFromProjectPath</c>, which
/// defers to the SDK's <c>VirtualProjectBuilder.IsValidEntryPointPath</c>), so a path
/// <c>MSBuildWorkspace</c> opens as a file-based app is one Buildalyzer opens as a file-based app: a file
/// that exists, does not carry a project file extension, and either ends in <c>.cs</c> or starts with a
/// <c>#!</c> shebang.
/// </remarks>
internal static class FileBasedApp
{
    /// <summary>Extensions that are loaded as a project file, never as an entry point.</summary>
    private static readonly string[] ProjectFileExtensions = [".csproj", ".vbproj", ".fsproj"];

    /// <summary>
    /// A property only the SDK's generated project carries, used to tell a virtual project this
    /// analyzer wrote from a real project that happens to occupy the same path.
    /// </summary>
    public const string GeneratedProjectMarker = "<FileBasedProgram>true</FileBasedProgram>";

    /// <summary>Returns whether the path is the entry point file of a file-based app.</summary>
    [Pure]
    public static bool IsEntryPoint(IOPath path)
        => path.File() is { Exists: true } file
        && !Array.Exists(ProjectFileExtensions, extension => file.Extension.IsMatch(extension))
        && (file.Extension.IsMatch(".cs") || HasShebang(file));

    /// <summary>Returns whether the file starts with the two bytes <c>#!</c>.</summary>
    [Pure]
    private static bool HasShebang(FileInfo file)
    {
        try
        {
            using var stream = file.OpenRead();
            int first = stream.ReadByte();
            int second = stream.ReadByte();
            return first == '#' && second == '!';
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
