﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Buildalyzer;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Shouldly;
using System.Linq;
using System.Xml.Linq;

namespace NetCoreTests
{
    [TestFixture]
    public class NetCoreTestFixture
    {
        private static string[] _projectFiles =
        {
#if Is_Windows
            @"LegacyFrameworkProject\LegacyFrameworkProject.csproj",
            @"LegacyFrameworkProjectWithReference\LegacyFrameworkProjectWithReference.csproj",
            @"SdkFrameworkProject\SdkFrameworkProject.csproj",
#endif
            @"SdkNetCoreProject\SdkNetCoreProject.csproj",
            @"SdkNetCoreProjectImport\SdkNetCoreProjectImport.csproj",
            @"SdkNetStandardProject\SdkNetStandardProject.csproj",
            @"SdkNetStandardProjectImport\SdkNetStandardProjectImport.csproj",
            @"SdkMultiTargetingProject\SdkMultiTargetingProject.csproj"
        };

        [TestCaseSource(nameof(_projectFiles))]
        public void LoadsProject(string projectFile)
        {
            // Given
            StringWriter log = new StringWriter();
            ProjectAnalyzer analyzer = GetProjectAnalyzer(projectFile, log);

            // When
            Project project = analyzer.Load();

            // Then
            project.ShouldNotBeNull(log.ToString());
        }

        [TestCaseSource(nameof(_projectFiles))]
        public void CompilesProject(string projectFile)
        {
            // Given
            StringWriter log = new StringWriter();
            ProjectAnalyzer analyzer = GetProjectAnalyzer(projectFile, log);
                // Uncomment to generate a binary log if something isn't working
                //.WithBinaryLog(Path.Combine(@"E:\Temp\", Path.ChangeExtension(Path.GetFileName(projectFile), ".binlog")));

            // When
            ProjectInstance projectInstance = analyzer.Compile();

            // Then
            projectInstance.ShouldNotBeNull(log.ToString());
        }

        [TestCaseSource(nameof(_projectFiles))]
        public void GetsSourceFiles(string projectFile)
        {
            // Given
            StringWriter log = new StringWriter();
            ProjectAnalyzer analyzer = GetProjectAnalyzer(projectFile, log);

            // When
            IReadOnlyList<string> sourceFiles = analyzer.GetSourceFiles();

            // Then
            sourceFiles.ShouldContain(x => x.EndsWith("Class1.cs"), log.ToString());
        }

        [TestCaseSource(nameof(_projectFiles))]
        public void GetsVirtualProjectSourceFiles(string projectFile)
        {
            // Given
            StringWriter log = new StringWriter();
            projectFile = GetProjectPath(projectFile);
            XDocument projectDocument = XDocument.Load(projectFile);
            projectFile = projectFile.Replace(".csproj", "Virtual.csproj");
            ProjectAnalyzer analyzer = new AnalyzerManager(log).GetProject(projectFile, projectDocument);

            // When
            IReadOnlyList<string> sourceFiles = analyzer.GetSourceFiles();

            // Then
            sourceFiles.ShouldContain(x => x.EndsWith("Class1.cs"), log.ToString());
        }

        [Test]
        public void GetsProjectsInSolution()
        {
            // Given
            StringWriter log = new StringWriter();

            // When
            AnalyzerManager manager = new AnalyzerManager(GetProjectPath("TestProjects.sln"), log);

            // Then
            manager.Projects.Keys.ShouldBe(_projectFiles.Select(x => GetProjectPath(x)), true, log.ToString());
        }
        
        [Test]
        public void IgnoreSolutionItemsThatAreNotProjects()
        {
            // Given / When
            AnalyzerManager manager = new AnalyzerManager(GetProjectPath("TestProjects.sln"));
            
            // Then
            manager.Projects.Any(x => x.Value.ProjectFilePath.Contains("TestEmptySolutionFolder")).ShouldBeFalse();
        }

        private ProjectAnalyzer GetProjectAnalyzer(string projectFile, StringWriter log) =>
            new AnalyzerManager(log).GetProject(GetProjectPath(projectFile));

        private static string GetProjectPath(string file)
        {
            var path = Path.GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(typeof(NetCoreTestFixture).Assembly.Location),
                    @"..\..\..\..\projects\" + file));

            return path.Replace('\\', Path.DirectorySeparatorChar);
        }
    }
}