using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Buildalyzer.Construction;
using Buildalyzer.IO;
using Microsoft.Extensions.Logging;

namespace Buildalyzer.Workspaces;

public static class AnalyzerResultExtensions
{
    /// <summary>
    /// Gets a Roslyn workspace for the analyzed results.
    /// </summary>
    /// <param name="analyzerResult">The results from building a Buildalyzer project analyzer.</param>
    /// <param name="addProjectReferences">
    /// <c>true</c> to add projects to the workspace for project references that exist in the same <see cref="AnalyzerManager"/>.
    /// If <c>true</c> this will trigger (re)building all referenced projects. Directly add <see cref="AnalyzerResult"/> instances instead if you already have them available.
    /// </param>
    /// <returns>A Roslyn workspace.</returns>
    public static AdhocWorkspace GetWorkspace(this IAnalyzerResult analyzerResult, bool addProjectReferences = false)
    {
        Guard.NotNull(analyzerResult);
        AdhocWorkspace workspace = analyzerResult.Manager.CreateWorkspace();
        analyzerResult.AddToWorkspace(workspace, addProjectReferences);
        return workspace;
    }

    /// <summary>
    /// Adds a result to an existing Roslyn workspace.
    /// </summary>
    /// <param name="analyzerResult">The results from building a Buildalyzer project analyzer.</param>
    /// <param name="workspace">A Roslyn workspace.</param>
    /// <param name="addProjectReferences">
    /// <c>true</c> to add projects to the workspace for project references that exist in the same <see cref="AnalyzerManager"/>.
    /// If <c>true</c> this will trigger (re)building all referenced projects. Directly add <see cref="AnalyzerResult"/> instances instead if you already have them available.
    /// </param>
    /// <returns>
    /// The newly added Roslyn project, or <c>null</c> if the project couldn't be added to the workspace
    /// (most commonly because its language is not one Roslyn workspaces support, such as F#).
    /// </returns>
    public static Project? AddToWorkspace(this IAnalyzerResult analyzerResult, Workspace workspace, bool addProjectReferences = false)
    {
        Guard.NotNull(analyzerResult);
        Guard.NotNull(workspace);

        // Add the referenced projects first (post-order) so this result can wire to their outputs.
        // Seed the visited set with this project so a reference cycle can't re-add it.
        HashSet<string> visited = new(IOPath.Comparer) { NormalizePath(analyzerResult.ProjectFilePath) };
        if (addProjectReferences)
        {
            // Build the referenced-project closure up front in parallel (this project's own result is
            // already in hand), then populate the workspace sequentially from that cache below.
            IReadOnlyList<IProjectAnalyzer> referencedRoots = ResolveReferencedRoots(analyzerResult.Manager, analyzerResult.ProjectReferences);
            IReadOnlyDictionary<string, IAnalyzerResult[]> prebuilt = PrebuildReferenceClosure(analyzerResult.Manager, referencedRoots);
            AddReferencedAnalyzers(analyzerResult.Manager, analyzerResult.ProjectReferences, workspace, visited, prebuilt);
        }

        // Match MSBuildWorkspace's naming: a framework flavour of a multi-targeted project is
        // "<Project>(<tfm>)" even when it is the only flavour being added; a single-targeted project
        // keeps its bare name.
        ProjectId? projectId = AddResult(analyzerResult, workspace, addDiscriminator: IsMultiTargeted(analyzerResult));
        return projectId is null ? null : workspace.CurrentSolution.GetProject(projectId);
    }

    /// <summary>
    /// Adds every succeeded target-framework result of an analyzer - and, post-order, the analyzers it
    /// references - to the workspace, modelling each (project, framework) as its own Roslyn project just
    /// like MSBuildWorkspace. Project references are wired by resolved output-assembly path, so a consumer
    /// binds the exact framework flavour of a multi-targeted dependency that MSBuild resolved. Returns the
    /// ProjectIds of this analyzer's own per-framework projects, in framework order.
    /// </summary>
    internal static IReadOnlyList<ProjectId> AddAnalyzer(IProjectAnalyzer analyzer, Workspace workspace, bool addProjectReferences, HashSet<string> visited, IReadOnlyDictionary<string, IAnalyzerResult[]>? prebuilt = null)
    {
        string projectPath = NormalizePath(analyzer.ProjectFile.Path);
        if (!visited.Add(projectPath))
        {
            return [];
        }

        // One Roslyn project per target framework. Results are reused from the pre-built cache when the
        // caller built projects in parallel up front.
        IAnalyzerResult[] results = prebuilt is not null && prebuilt.TryGetValue(projectPath, out IAnalyzerResult[] cached)
            ? cached
            : WorkspaceResults(analyzer.Build());

        // Post-order: add the projects this one references before it, so their outputs are present to
        // wire against and every project reference resolves in a single forward pass.
        if (addProjectReferences)
        {
            AddReferencedAnalyzers(analyzer.Manager, results.SelectMany(r => r.ProjectReferences), workspace, visited, prebuilt);
        }

        // Match MSBuildWorkspace: append a "(tfm)" discriminator when the project is multi-targeted (it
        // declares TargetFrameworks, or produced more than one framework); a single-targeted project keeps
        // its bare name.
        bool addDiscriminator = results.Any(IsMultiTargeted)
            || results
                .Select(r => r.TargetFramework)
                .Where(tfm => !string.IsNullOrEmpty(tfm))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1;

        List<ProjectId> ids = [];
        foreach (IAnalyzerResult result in results)
        {
            if (AddResult(result, workspace, addDiscriminator) is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static void AddReferencedAnalyzers(IAnalyzerManager manager, IEnumerable<string> projectReferences, Workspace workspace, HashSet<string> visited, IReadOnlyDictionary<string, IAnalyzerResult[]>? prebuilt)
    {
        foreach (string referencePath in projectReferences.Distinct(IOPath.Comparer))
        {
            if (ResolveReferenced(manager, referencePath) is { } referenced)
            {
                AddAnalyzer(referenced, workspace, addProjectReferences: true, visited, prebuilt);
            }
        }
    }

    /// <summary>
    /// Builds every project reachable through project references from <paramref name="roots"/> and returns
    /// their succeeded results keyed by normalized project path, for reuse when populating the workspace.
    /// </summary>
    /// <remarks>
    /// The transitive closure is discovered by parsing project files only (see
    /// <see cref="Buildalyzer.Construction.ProjectFile.ProjectReferences"/>), so no build is needed to find
    /// it. Every project in the closure is then built in a single parallel wave - this keeps even a linear
    /// A→B→C reference chain concurrent, which a level-by-level build would serialize. <c>Build()</c> is safe
    /// to run concurrently across projects; the workspace itself is populated sequentially afterwards. A
    /// reference the static parse misses simply isn't cached here and is built on demand by
    /// <see cref="AddAnalyzer"/>, so correctness never depends on the discovery being complete.
    /// </remarks>
    internal static IReadOnlyDictionary<string, IAnalyzerResult[]> PrebuildReferenceClosure(
        IAnalyzerManager manager, IEnumerable<IProjectAnalyzer> roots)
    {
        // Discover the closure (cheap XML parse per project); dedupe by canonical project path.
        Dictionary<string, IProjectAnalyzer> closure = new(IOPath.Comparer);
        Queue<IProjectAnalyzer> pending = new();
        foreach (IProjectAnalyzer root in roots)
        {
            if (closure.TryAdd(NormalizePath(root.ProjectFile.Path), root))
            {
                pending.Enqueue(root);
            }
        }

        while (pending.Count > 0)
        {
            IProjectAnalyzer analyzer = pending.Dequeue();
            foreach (string reference in ((ProjectFile)analyzer.ProjectFile).ProjectReferences)
            {
                if (ResolveReferenced(manager, reference) is { } referenced
                    && closure.TryAdd(NormalizePath(referenced.ProjectFile.Path), referenced))
                {
                    pending.Enqueue(referenced);
                }
            }
        }

        // Build the whole closure concurrently, then index the results by project path. Each build restores
        // itself (via the -restore switch, folded into the same invocation); a separate up-front graph
        // restore was measured to be slower, since the per-project restores already overlap in this parallel
        // wave while an up-front restore only adds a serial process.
        return closure.Values
            .AsParallel()
            .Select(a => (Path: NormalizePath(a.ProjectFile.Path), Results: WorkspaceResults(a.Build())))
            .ToList()
            .ToDictionary(x => x.Path, x => x.Results, IOPath.Comparer);
    }

    /// <summary>
    /// The per-framework results to model as Roslyn projects.
    /// </summary>
    /// <remarks>
    /// A <em>failed</em> result is deliberately kept: when MSBuild aborts before the compiler task runs the
    /// project still evaluated its <c>Compile</c> items and resolved its references, and the workspace is
    /// reconstructed from those (see <see cref="ShouldFallBackToItems"/> and issue #341). Dropping failed
    /// results here would leave that recovery unreachable from <c>IProjectAnalyzer.GetWorkspace()</c> and
    /// <c>IAnalyzerManager.GetWorkspace()</c>.
    /// <para>
    /// Only the empty-framework outer evaluation of a multi-targeted build is dropped, and only when
    /// per-framework results exist alongside it: it compiles nothing itself and would otherwise duplicate
    /// the project under a bare name. When it is the only result (an evaluation-only or early-failure
    /// build) it is kept, since it is all the caller has.
    /// </para>
    /// </remarks>
    internal static IAnalyzerResult[] WorkspaceResults(IAnalyzerResults results)
    {
        IAnalyzerResult[] all = [.. results];
        return all.Any(r => !string.IsNullOrEmpty(r.TargetFramework))
            ? [.. all.Where(r => !string.IsNullOrEmpty(r.TargetFramework))]
            : all;
    }

    private static IReadOnlyList<IProjectAnalyzer> ResolveReferencedRoots(IAnalyzerManager manager, IEnumerable<string> projectReferences)
    {
        List<IProjectAnalyzer> roots = [];
        foreach (string reference in projectReferences.Distinct(IOPath.Comparer))
        {
            if (ResolveReferenced(manager, reference) is { } referenced)
            {
                roots.Add(referenced);
            }
        }

        return roots;
    }

    private static IProjectAnalyzer? ResolveReferenced(IAnalyzerManager manager, string referencePath) =>
        manager.Projects.TryGetValue(referencePath, out IProjectAnalyzer existing)
            ? existing
            : manager.GetProject(referencePath);

    // Adds a single target-framework result as its own Roslyn project and wires its project references by
    // resolved output-assembly path. Returns null when the language is unsupported, or the id of the
    // existing project when the same (project file, project name) has already been added (idempotent).
    private static ProjectId? AddResult(IAnalyzerResult analyzerResult, Workspace workspace, bool addDiscriminator)
    {
        if (!TryGetSupportedLanguageName(analyzerResult.ProjectFilePath, out string languageName))
        {
            return null;
        }

        // Idempotent: the same (project file, project name) is the same (project, framework).
        string projectName = ProjectName(analyzerResult, addDiscriminator);
        if (FindProject(workspace.CurrentSolution, analyzerResult.ProjectFilePath, projectName) is { } existingId)
        {
            return existingId;
        }

        // Parse the captured compiler command line once and share it across options, documents and
        // references. It is the authoritative record of what the compiler actually saw and is present in
        // virtually every binary log (the csc/vbc command line is a normal-verbosity message), so it also
        // backfills the compiler-derived inputs when the structured task-input events weren't captured.
        string? projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        CommandLineArguments? commandLine = ParseCommandLine(analyzerResult, languageName, projectDirectory);
        if (commandLine is null)
        {
            // Say so out loud: the reconstructed project (see ShouldFallBackToItems) has no build-generated
            // sources and options derived from evaluated properties, and a consumer comparing it against a
            // full design-time build would otherwise have nothing to point at but the missing files.
            analyzerResult.Manager.LoggerFactory?.CreateLogger(typeof(AnalyzerResultExtensions).FullName!).LogWarning(
                "No compiler invocation was captured for {ProjectFile} ({TargetFramework}); the build did not reach CoreCompile, "
                + "so the workspace project is reconstructed from the evaluated items and properties (build-generated sources, "
                + "SDK analyzers and compiler-computed options may be missing). Succeeded: {Succeeded}.",
                analyzerResult.ProjectFilePath,
                string.IsNullOrEmpty(analyzerResult.TargetFramework) ? "no target framework" : analyzerResult.TargetFramework,
                analyzerResult.Succeeded);
        }

        ProjectId projectId = ProjectId.CreateNewId();
        Microsoft.CodeAnalysis.ProjectInfo projectInfo = GetProjectInfo(
            analyzerResult, workspace, projectId, projectName, languageName, projectDirectory, commandLine);
        if (projectInfo is null)
        {
            return null;
        }

        Solution solution = workspace.CurrentSolution.AddProject(projectInfo);
        solution = WireProjectReferences(solution, projectId, analyzerResult, commandLine);

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Could not apply workspace solution changes");
        }

        return projectId;
    }

    // Correlates this project's resolved references against the output-assembly paths (TargetPath and the
    // reference assembly TargetRefPath) of the projects already in the workspace: any match becomes a
    // project reference. The reference is not also a metadata reference because a design-time build never
    // produces the output on disk (GetMetadataReferences filters by File.Exists). This is how
    // MSBuildWorkspace resolves the exact framework flavour of a multi-targeted dependency.
    private static Solution WireProjectReferences(Solution solution, ProjectId projectId, IAnalyzerResult analyzerResult, CommandLineArguments? commandLine)
    {
        Dictionary<string, List<(ProjectId Id, string? TargetFramework)>> outputToProjects = BuildOutputIndex(solution, projectId);
        if (outputToProjects.Count == 0)
        {
            return solution;
        }

        HashSet<ProjectId> referenced = [];
        foreach (var reference in GetReferencePaths(analyzerResult, commandLine))
        {
            if (outputToProjects.TryGetValue(NormalizePath(reference.Reference), out List<(ProjectId Id, string? TargetFramework)> candidates)
                && ChooseReferencedProject(candidates, analyzerResult.TargetFramework) is { } targetId
                && referenced.Add(targetId))
            {
                // Carry the aliases and embed-interop flag over to the project reference. Turning a
                // resolved assembly reference into a project reference must not change what the compiler
                // sees: an `extern alias`ed dependency is not in the global namespace, and an embedded
                // interop reference contributes types rather than a reference. Dropping them here would
                // make the workspace compile differently from the build it came from.
                solution = solution.AddProjectReference(
                    projectId,
                    new ProjectReference(targetId, reference.Aliases, reference.EmbedInteropTypes));
            }
        }

        return solution;
    }

    // Every project claiming an output path is kept as a candidate: a multi-targeted project that sets
    // AppendTargetFrameworkToOutputPath=false has one output path for ALL of its frameworks, so a single
    // last-write-wins slot would silently rewire consumers to an arbitrary flavour.
    private static Dictionary<string, List<(ProjectId Id, string? TargetFramework)>> BuildOutputIndex(Solution solution, ProjectId exclude)
    {
        Dictionary<string, List<(ProjectId Id, string? TargetFramework)>> index = new(IOPath.Comparer);
        foreach (Project project in solution.Projects)
        {
            if (project.Id.Equals(exclude))
            {
                continue;
            }

            string? targetFramework = ExtractTargetFramework(project.Name);
            foreach (string? output in new[] { project.OutputFilePath, project.OutputRefFilePath })
            {
                if (!string.IsNullOrEmpty(output))
                {
                    string normalized = NormalizePath(output);
                    if (!index.TryGetValue(normalized, out List<(ProjectId Id, string? TargetFramework)> candidates))
                    {
                        index[normalized] = candidates = [];
                    }

                    if (!candidates.Any(c => c.Id.Equals(project.Id)))
                    {
                        candidates.Add((project.Id, targetFramework));
                    }
                }
            }
        }

        return index;
    }

    // The output path alone cannot say which framework flavour a consumer resolved when several projects
    // claim the same path (AppendTargetFrameworkToOutputPath=false). Prefer the candidate whose framework
    // equals the consuming result's - when the consumer's own framework is among the dependency's, that is
    // also what MSBuild's nearest-framework negotiation picks. Otherwise fall back to the last candidate
    // added, preserving the previous behaviour for references that stay genuinely ambiguous.
    private static ProjectId? ChooseReferencedProject(List<(ProjectId Id, string? TargetFramework)> candidates, string? consumerTargetFramework)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count > 1 && !string.IsNullOrEmpty(consumerTargetFramework))
        {
            foreach ((ProjectId id, string? targetFramework) in candidates)
            {
                if (string.Equals(targetFramework, consumerTargetFramework, StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }
            }
        }

        return candidates[^1].Id;
    }

    // The "(tfm)" discriminator ProjectName appends to a multi-targeted project's name is the only
    // per-framework identity a Roslyn Project carries; a single-framework project keeps its bare name
    // (and its output path can't collide with a sibling flavour's).
    private static string? ExtractTargetFramework(string projectName)
    {
        int open = projectName.LastIndexOf('(');
        return open >= 0 && projectName.EndsWith(")", StringComparison.Ordinal)
            ? projectName[(open + 1)..^1]
            : null;
    }

    // Locates an already-added Roslyn project by its identity: the project file it came from plus its name,
    // which carries the "(tfm)" discriminator for a multi-targeted project. Output paths are NOT an identity
    // - unrelated projects can share a TargetPath, and a multi-targeted project that sets
    // AppendTargetFrameworkToOutputPath=false has one output path for all of its frameworks - so they are
    // used only for wiring project references (see WireProjectReferences).
    private static ProjectId? FindProject(Solution solution, string? projectFilePath, string projectName)
    {
        if (string.IsNullOrEmpty(projectFilePath))
        {
            return null;
        }

        string normalized = NormalizePath(projectFilePath);
        foreach (Project project in solution.Projects)
        {
            if (project.FilePath is { } path
                && NormalizePath(path).Equals(normalized, IOPath.Comparison)
                && string.Equals(project.Name, projectName, StringComparison.Ordinal))
            {
                return project.Id;
            }
        }

        return null;
    }

    // The resolved reference paths used for output-path correlation, each with the aliases and embed-interop
    // flag the compiler was given for it. Unlike GetMetadataReferences these are NOT filtered by File.Exists:
    // a project reference resolves to a dependency's output that a design-time build never writes to disk,
    // and that (nonexistent) path is exactly what we match on.
    private static IEnumerable<(string Reference, ImmutableArray<string> Aliases, bool EmbedInteropTypes)> GetReferencePaths(
        IAnalyzerResult analyzerResult,
        CommandLineArguments? commandLine)
    {
        string[] references = analyzerResult.References ?? [];

        // The command line's /reference: switches list the same resolved assembly paths (project outputs
        // included), so they correlate to project references too when task inputs weren't captured. The
        // switch carries its own alias and embed-interop metadata.
        if (references.Length == 0 && commandLine is not null)
        {
            return commandLine.MetadataReferences
                .Select(r => (r.Reference, r.Properties.Aliases, r.Properties.EmbedInteropTypes));
        }

        if (references.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            references = GetItemPaths(analyzerResult, "ReferencePath");
        }

        return references.Select(reference => (
            Reference: reference,
            Aliases: analyzerResult.ReferenceAliases.GetValueOrDefault(reference),
            EmbedInteropTypes: analyzerResult.ReferencesEmbeddingInteropTypes.Contains(reference)));
    }

    // Whether the project declares itself multi-targeted. Reads the evaluated TargetFrameworks property of
    // the build (present in each framework's inner build too), which is exactly how MSBuildWorkspace decides
    // to load a project once per framework and name each "<Project>(<tfm>)".
    private static bool IsMultiTargeted(IAnalyzerResult analyzerResult) =>
        !string.IsNullOrWhiteSpace(analyzerResult.GetProperty("TargetFrameworks"));

    private static string ProjectName(IAnalyzerResult analyzerResult, bool addDiscriminator)
    {
        string name = Path.GetFileNameWithoutExtension(analyzerResult.ProjectFilePath);
        return addDiscriminator && !string.IsNullOrWhiteSpace(analyzerResult.TargetFramework)
            ? $"{name}({analyzerResult.TargetFramework})"
            : name;
    }

    internal static string NormalizePath(string path) => Path.GetFullPath(path);

    private static Microsoft.CodeAnalysis.ProjectInfo? GetProjectInfo(
        IAnalyzerResult analyzerResult, Workspace workspace, ProjectId projectId, string projectName,
        string languageName, string? projectDirectory, CommandLineArguments? commandLine)
    {
        string assemblyName = analyzerResult.GetProperty("AssemblyName") is { Length: > 0 } name ? name : projectName;
        (CompilationOptions? compilationOptions, ParseOptions? parseOptions) = CreateOptions(analyzerResult, languageName, projectDirectory, commandLine);

        // Project references are wired after the project is added, by output-assembly path, so that a
        // multi-targeted dependency resolves to the exact framework flavour MSBuild chose (see WireProjectReferences).
        return Microsoft.CodeAnalysis.ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            projectName,
            assemblyName,
            languageName,
            filePath: analyzerResult.ProjectFilePath,
            outputFilePath: analyzerResult.GetProperty("TargetPath"),
            outputRefFilePath: analyzerResult.GetProperty("TargetRefPath"),
            compilationOptions: compilationOptions,
            parseOptions: parseOptions,
            documents: GetDocuments(analyzerResult, projectId, commandLine),
            projectReferences: [],
            metadataReferences: GetMetadataReferences(analyzerResult, commandLine),
            analyzerReferences: GetAnalyzerReferences(analyzerResult, workspace, commandLine),
            additionalDocuments: GetAdditionalDocuments(analyzerResult, projectId, commandLine))
            .WithDefaultNamespace(analyzerResult.GetProperty("RootNamespace"))
            .WithAnalyzerConfigDocuments(GetAnalyzerConfigDocuments(analyzerResult, projectId, commandLine));
    }

    /// <summary>
    /// Parses the captured compiler command line with Roslyn's own parser, or returns <c>null</c> when no
    /// command line was captured (e.g. the build failed before the compiler task ran). The result is the
    /// authoritative record of what the compiler saw - defines, language version, unsafe/checked/nullable,
    /// optimization, platform, warning level, documentation mode and full diagnostic configuration, plus the
    /// resolved source/reference/analyzer/additional/analyzer-config sets - and is used both for the
    /// compilation/parse options and to backfill those inputs when the structured task-input events are absent.
    /// </summary>
    /// <remarks>
    /// Language-specific parsing stays in local functions so the parser assembly for the other language is
    /// never loaded for a project that does not use it.
    /// </remarks>
    private static CommandLineArguments? ParseCommandLine(IAnalyzerResult analyzerResult, string languageName, string? projectDirectory)
    {
        if (analyzerResult.CompilerArguments is not { Length: > 0 } arguments)
        {
            return null;
        }

        if (languageName == LanguageNames.CSharp)
        {
            CommandLineArguments ParseCSharp() => CSharpCommandLineParser.Default.Parse(arguments, projectDirectory, sdkDirectory: null);
            return ParseCSharp();
        }

        if (languageName == LanguageNames.VisualBasic)
        {
            CommandLineArguments ParseVisualBasic() => VisualBasicCommandLineParser.Default.Parse(arguments, projectDirectory, sdkDirectory: null);
            return ParseVisualBasic();
        }

        return null;
    }

    /// <summary>
    /// Produces the compilation and parse options. The primary source is the parsed compiler command line,
    /// which yields exactly the options the compiler used (and that MSBuildWorkspace reports). When no command
    /// line was captured (the build stopped before the compiler ran) the command line the compiler task would
    /// have produced is reconstructed from the evaluated properties and parsed the same way
    /// (<see cref="EvaluatedCommandLine"/>), so NoWarn/WarningsAsErrors, defines, language version, nullable,
    /// signing, the module name and the rest follow the compiler's own semantics on that path too.
    /// </summary>
    private static (CompilationOptions? CompilationOptions, ParseOptions? ParseOptions) CreateOptions(
        IAnalyzerResult analyzerResult, string languageName, string? projectDirectory, CommandLineArguments? commandLine)
    {
        (CompilationOptions? compilationOptions, ParseOptions? parseOptions) = CreateRawOptions(analyzerResult, languageName, commandLine);

        if (compilationOptions is not null)
        {
            compilationOptions = WithWorkspaceServices(compilationOptions, projectDirectory);
        }

        // The compiler only parses doc comments when asked to emit them (/doc), so a project without
        // GenerateDocumentationFile parses with DocumentationMode.None. MSBuildWorkspace always raises that
        // to Parse ("ensure that doc-comments are parsed") so IDE features see the comments; do the same.
        if (parseOptions is { DocumentationMode: DocumentationMode.None })
        {
            parseOptions = parseOptions.WithDocumentationMode(DocumentationMode.Parse);
        }

        return (compilationOptions, parseOptions);
    }

    private static (CompilationOptions? CompilationOptions, ParseOptions? ParseOptions) CreateRawOptions(
        IAnalyzerResult analyzerResult, string languageName, CommandLineArguments? commandLine)
    {
        commandLine ??= EvaluatedCommandLine.Parse(analyzerResult, languageName, Path.GetDirectoryName(analyzerResult.ProjectFilePath));
        return commandLine is null ? (null, null) : (commandLine.CompilationOptions, commandLine.ParseOptions);
    }

    /// <summary>
    /// Attaches the same compilation "services" MSBuildWorkspace puts on every project, so features
    /// relying on them behave the same: assembly identity/version unification (<see cref="DesktopAssemblyIdentityComparer"/>),
    /// XML documentation <c>&lt;include&gt;</c> resolution, <c>#load</c>/<c>#line</c> source resolution, and
    /// strong-name signing during <c>Emit</c>. The command-line parser leaves these null. The metadata
    /// reference resolver MSBuildWorkspace uses is internal to Roslyn and only affects <c>#r</c>, so it is
    /// left at its default.
    /// </summary>
    private static CompilationOptions WithWorkspaceServices(CompilationOptions options, string? projectDirectory)
    {
        ImmutableArray<string> keyFileSearchPaths = projectDirectory is null ? [] : [projectDirectory];
        return options
            .WithXmlReferenceResolver(new XmlFileResolver(projectDirectory))
            .WithSourceReferenceResolver(new SourceFileResolver([], projectDirectory))
            .WithStrongNameProvider(new DesktopStrongNameProvider(keyFileSearchPaths, Path.GetTempPath()))
            .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);
    }

    private static IEnumerable<DocumentInfo> GetAnalyzerConfigDocuments(IAnalyzerResult analyzerResult, ProjectId projectId, CommandLineArguments? commandLine)
    {
        // The compiler receives these as absolute paths via /analyzerconfig:, including the
        // SDK-generated <Project>.GeneratedMSBuildEditorConfig.editorconfig that surfaces
        // build_property.* values many source generators depend on.
        string[] analyzerConfigFiles = analyzerResult.AnalyzerConfigFiles ?? [];

        // Backfill from the command line's /analyzerconfig: switches when task inputs weren't captured.
        if (analyzerConfigFiles.Length == 0 && commandLine is not null)
        {
            analyzerConfigFiles = [.. commandLine.AnalyzerConfigPaths];
        }

        return GetDocuments(analyzerConfigFiles, projectId, Path.GetDirectoryName(analyzerResult.ProjectFilePath), GetChecksumAlgorithm(analyzerResult));
    }

    private static IEnumerable<DocumentInfo> GetDocuments(IAnalyzerResult analyzerResult, ProjectId projectId, CommandLineArguments? commandLine)
    {
        string[] sourceFiles = analyzerResult.SourceFiles ?? [];

        // When the compiler task's inputs weren't captured (e.g. replaying a binary log that lacks task
        // parameters) SourceFiles is empty, but the csc/vbc command line - a normal-verbosity message present
        // in virtually every binary log - still lists the exact, final source set (generated files included).
        if (sourceFiles.Length == 0 && commandLine is not null)
        {
            sourceFiles = [.. commandLine.SourceFiles.Select(f => f.Path)];
        }

        // Last resort: when neither the task inputs nor a command line were captured (the build failed before
        // the compiler ran), the evaluation-time Compile items - declaration order, no build-generated
        // sources - at least give the workspace documents (issue #341).
        if (sourceFiles.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            sourceFiles = GetItemPaths(analyzerResult, "Compile");
        }

        return GetDocuments(sourceFiles, projectId, Path.GetDirectoryName(analyzerResult.ProjectFilePath), GetChecksumAlgorithm(analyzerResult));
    }

    // Every input path becomes a document, whether or not it exists on disk, matching MSBuildWorkspace.
    // The text is loaded lazily on first read; a missing file then produces an empty document and a
    // standard Roslyn document-load failure instead of being silently dropped here (issue #345).
    private static IEnumerable<DocumentInfo> GetDocuments(IEnumerable<string> files, ProjectId projectId, string? projectDirectory, SourceHashAlgorithm checksumAlgorithm) =>
       files.Select(x => DocumentInfo.Create(
               DocumentId.CreateNewId(projectId),
               Path.GetFileName(x),
               folders: GetDocumentFolders(x, projectDirectory),
               loader: new LazyFileTextLoader(x, checksumAlgorithm),
               filePath: x));

    /// <summary>
    /// Reads a document's text from disk on first access, like MSBuildWorkspace's file loader, instead of
    /// eagerly at workspace construction. Roslyn turns an <see cref="IOException"/> thrown here into an
    /// empty document plus a document-load diagnostic, which is exactly how MSBuildWorkspace surfaces a
    /// source path that does not exist on disk.
    /// </summary>
    private sealed class LazyFileTextLoader(string path, SourceHashAlgorithm checksumAlgorithm) : TextLoader
    {
        public override Task<TextAndVersion> LoadTextAndVersionAsync(LoadTextOptions options, CancellationToken cancellationToken)
            => Task.FromResult(TextAndVersion.Create(
                ReadSourceText(path, checksumAlgorithm),
                VersionStamp.Create(File.GetLastWriteTimeUtc(path)),
                path));
    }

    /// <summary>
    /// When MSBuild aborts before the compiler task runs, <c>CompilerCommand</c> is never captured, so the
    /// compiler-backed accessors (<c>SourceFiles</c>, <c>References</c>, ...) are empty even though the project
    /// evaluated its <c>Compile</c> items. In that case the workspace is reconstructed from evaluation-time
    /// items and the resolved <c>ReferencePath</c> captured from ResolveAssemblyReference. See issue #341.
    /// <para>
    /// Only safe for languages whose compilation is order-insensitive. Evaluated <c>Compile</c> items are
    /// in declaration order, which for F# is not compile order (<c>FSharpSourceCodeCompileOrder</c> re-sorts
    /// them by <c>CompileOrder</c> metadata before <c>CoreCompile</c>), so this fallback would silently
    /// produce a differently-meaning compilation. F# never reaches here today because
    /// <see cref="TryGetSupportedLanguageName"/> rejects <c>.fsproj</c>; guard this if that ever changes.
    /// </para>
    /// </summary>
    private static bool ShouldFallBackToItems(IAnalyzerResult analyzerResult) =>
        (analyzerResult.SourceFiles is null || analyzerResult.SourceFiles.Length == 0)
        && analyzerResult.Items.TryGetValue("Compile", out IProjectItem[] compileItems)
        && compileItems.Length > 0;

    /// <summary>Resolves the <c>ItemSpec</c> of each item of the given type to a full path.</summary>
    private static string[] GetItemPaths(IAnalyzerResult analyzerResult, string itemType)
    {
        if (!analyzerResult.Items.TryGetValue(itemType, out IProjectItem[] items) || items.Length == 0)
        {
            return [];
        }

        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        return [.. items
            .Select(x => Path.GetFullPath(x.ItemSpec, projectDirectory!))
            .Distinct(IOPath.Comparer)];
    }

    // Preserve the file's own encoding (detecting a BOM, defaulting to UTF-8) rather than forcing a
    // fixed encoding, so Document text encoding matches MSBuildWorkspace and the compiler.
    private static SourceText ReadSourceText(string path, SourceHashAlgorithm checksumAlgorithm)
    {
        using FileStream stream = File.OpenRead(path);
        return SourceText.From(stream, Encoding.UTF8, checksumAlgorithm);
    }

    // The SDK hashes source with SHA256 by default (older projects used SHA1); mirror MSBuildWorkspace,
    // which takes the algorithm from the ChecksumAlgorithm property, so document checksums match.
    private static SourceHashAlgorithm GetChecksumAlgorithm(IAnalyzerResult analyzerResult) =>
        analyzerResult.GetProperty("ChecksumAlgorithm")?.ToUpperInvariant() switch
        {
            "SHA1" => SourceHashAlgorithm.Sha1,
            _ => SourceHashAlgorithm.Sha256,
        };

    // Mirrors MSBuildWorkspace: a document's logical folders are the directory of its path relative
    // to the project directory. Files outside the project cone are auto-linked by the SDK and keep no
    // folders, so anything whose relative path escapes the project directory yields none.
    private static IEnumerable<string> GetDocumentFolders(string filePath, string? projectDirectory)
    {
        if (string.IsNullOrEmpty(projectDirectory))
        {
            return [];
        }

        string relativePath = Path.GetRelativePath(projectDirectory!, filePath);

        // GetRelativePath returns a rooted path when the file sits on a different drive/UNC root, and a
        // ..-prefixed path when it escapes the project directory on the same root. Either way the file is
        // outside the project cone, so it keeps no folders.
        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith("..", StringComparison.Ordinal))
        {
            return [];
        }

        string relativeDirectory = Path.GetDirectoryName(relativePath) ?? string.Empty;
        return relativeDirectory.Length == 0
            ? []
            : relativeDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<DocumentInfo> GetAdditionalDocuments(IAnalyzerResult analyzerResult, ProjectId projectId, CommandLineArguments? commandLine)
    {
        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        string[] additionalFiles = analyzerResult.AdditionalFiles ?? [];

        // Backfill from the command line's /additionalfile: switches when task inputs weren't captured.
        if (additionalFiles.Length == 0 && commandLine is not null)
        {
            return GetDocuments(commandLine.AdditionalFiles.Select(f => f.Path), projectId, projectDirectory, GetChecksumAlgorithm(analyzerResult));
        }

        // Fall back to the evaluation-time AdditionalFiles items when the compiler never ran (issue #341).
        if (additionalFiles.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            return GetDocuments(GetItemPaths(analyzerResult, "AdditionalFiles"), projectId, projectDirectory, GetChecksumAlgorithm(analyzerResult));
        }

        return GetDocuments(additionalFiles.Select(x => Path.GetFullPath(x, projectDirectory!)), projectId, projectDirectory, GetChecksumAlgorithm(analyzerResult));
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences(IAnalyzerResult analyzerResult, CommandLineArguments? commandLine)
    {
        string[] references = analyzerResult.References ?? [];

        // Backfill from the command line's /reference: switches - which also carry each reference's alias
        // and embed-interop metadata - when task inputs weren't captured.
        if (references.Length == 0 && commandLine is not null)
        {
            return commandLine.MetadataReferences
                .Where(r => File.Exists(r.Reference))
                .Select(r => MetadataReference.CreateFromFile(r.Reference, r.Properties));
        }

        // Fall back to the resolved ReferencePath items (captured from ResolveAssemblyReference) when neither
        // the compiler task inputs nor a command line were captured, so the recovered workspace can still
        // bind types (issue #341).
        if (references.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            references = GetItemPaths(analyzerResult, "ReferencePath");
        }

        return references
            .Where(File.Exists)
            .Select(x => MetadataReference.CreateFromFile(x, new MetadataReferenceProperties(
                aliases: analyzerResult.ReferenceAliases.GetValueOrDefault(x),
                embedInteropTypes: analyzerResult.ReferencesEmbeddingInteropTypes.Contains(x))));
    }

    private static IEnumerable<AnalyzerReference> GetAnalyzerReferences(IAnalyzerResult analyzerResult, Workspace workspace, CommandLineArguments? commandLine)
    {
        IAnalyzerAssemblyLoader loader = workspace.Services.GetRequiredService<IAnalyzerService>().GetLoader();

        string projectDirectory = Path.GetDirectoryName(analyzerResult.ProjectFilePath);
        string[] analyzerReferences = analyzerResult.AnalyzerReferences ?? [];

        // Backfill from the command line's /analyzer: switches when task inputs weren't captured.
        if (analyzerReferences.Length == 0 && commandLine is not null)
        {
            analyzerReferences = [.. commandLine.AnalyzerReferences.Select(a => a.FilePath)];
        }

        // Fall back to the evaluation-time Analyzer items when the compiler never ran (issue #341).
        if (analyzerReferences.Length == 0 && ShouldFallBackToItems(analyzerResult))
        {
            analyzerReferences = GetItemPaths(analyzerResult, "Analyzer");
        }

        // A path that is missing on disk (an unbuilt project-private analyzer, say) becomes an
        // UnresolvedAnalyzerReference rather than being silently dropped, exactly as MSBuildWorkspace
        // surfaces it via CommandLineArguments.ResolveAnalyzerReferences (issue #345).
        return analyzerReferences
            .Select(x => Path.GetFullPath(x, projectDirectory!))
            .Select(x => File.Exists(x)
                ? new AnalyzerFileReference(x, loader)
                : (AnalyzerReference)new UnresolvedAnalyzerReference(x));
    }

    private static bool TryGetSupportedLanguageName(string projectPath, out string languageName)
    {
        switch (Path.GetExtension(projectPath))
        {
            case ".csproj":
                languageName = LanguageNames.CSharp;
                return true;
            case ".vbproj":
                languageName = LanguageNames.VisualBasic;
                return true;
            default:
                languageName = null;
                return false;
        }
    }
}
