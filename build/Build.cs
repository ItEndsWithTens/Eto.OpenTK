using Nuke.Common;
using Nuke.Common.Git;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.VSWhere;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.Git.GitTasks;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;
using static Nuke.Common.Tools.VSWhere.VSWhereTasks;

class Build : NukeBuild
{
    public static int Main()
    {
        if (EnvironmentInfo.IsOsx)
        {
            return Execute<Build>(x => x.CompileMac);
        }
        else if (EnvironmentInfo.IsLinux)
        {
            return Execute<Build>(x => x.CompileLinux);
        }
        else
        {
            return Execute<Build>(x => x.CompileWindows);
        }
    }

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly string Configuration = IsLocalBuild ? "Debug" : "Release";

    [Parameter("Whether to build test projects - Default is false")]
    readonly bool BuildTests = false;

    [Solution("Eto.Gl.sln")] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts" / Configuration;

    AbsolutePath CustomMsBuildPath;

    Target SetVisualStudioPaths => _ => _
        .Unlisted()
        .Executes(() =>
        {
            if (EnvironmentInfo.IsWin)
            {
                Logger.Info("Windows build; setting Visual Studio paths.");

                VSWhereSettings vswhereSettings = new VSWhereSettings()
                    .EnableLatest()
                    .AddRequires(MsBuildComponent);

                IReadOnlyCollection<Output> output = VSWhere(s => vswhereSettings).Output;

                var outputPath = output.FirstOrDefault(o => o.Text.StartsWith("installationPath"));
                var outputVersion = output.FirstOrDefault(o => o.Text.StartsWith("installationVersion"));

                // A list of component IDs and friendly names can be found at
                // https://docs.microsoft.com/en-us/visualstudio/install/workload-and-component-ids
                if (String.IsNullOrEmpty(outputPath.Text) || String.IsNullOrEmpty(outputVersion.Text))
                {
                    throw new Exception(
                        "Couldn't find a suitable Visual Studio installation! " +
                        "Either VS is not installed, or no available version " +
                        "has all of the following components installed:" +
                        "\n" +
                        "\n" +
                        $"MSBuild ({MsBuildComponent})");
                }

                string vsPath = outputPath.Text.Replace("installationPath: ", "");
                string vsVersion = outputVersion.Text.Replace("installationVersion: ", "");
                Int32.TryParse(vsVersion.Split('.')[0], out int vsMajor);

                if (vsMajor < 15)
                {
                    throw new Exception("Can't build with less than VS 2017!");
                }

                // Windows developers with Visual Studio installed to a directory
                // other than System.Environment.SpecialFolder.ProgramFilesX86 need
                // to tell Nuke the path to MSBuild.exe themselves.
                CustomMsBuildPath = (AbsolutePath)GlobFiles(Path.Combine(vsPath, "MSBuild"), "**/Bin/MSBuild.exe").First();
            }
            else
            {
                Logger.Info("Mono build; no Visual Studio paths to set.");
            }
        });

    Target Clean => _ => _
        .Executes(() =>
        {
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    private void Compile(params string[] projects)
    {
        MSBuild(settings => settings
            .EnableRestore()
            .SetTargets("Build")
            .SetConfiguration(Configuration)
            .When(CustomMsBuildPath != null, s => s
                .SetToolPath(CustomMsBuildPath))
            .SetMaxCpuCount(Environment.ProcessorCount)
            .SetNodeReuse(IsLocalBuild)
            .CombineWith(projects, (s, p) => s
                .SetProjectFile(Solution.GetProject(p))
                .SetOutDir(ArtifactsDirectory / p)
                // This Appalachian Trail of a SetProperty call is necessary to
                // account for Xamarin.Mac's .app packaging process not paying
                // attention to the OutDir property, set just above. OutputPath
                // is also expected to be relative, and end with a separator.
                .SetProperty("OutputPath", GetRelativePath(Solution.GetProject(p).Directory, ArtifactsDirectory / p) + Path.DirectorySeparatorChar)));
    }

    Target CompileLibrary => _ => _
        .DependsOn(SetVisualStudioPaths, Clean)
        .Executes(() =>
        {
            Compile("Eto.Gl");
        });

    Target CompileWindows => _ => _
        .DependsOn(CompileLibrary)
        .Executes(() =>
        {
            Compile("Eto.Gl.WinForms", "Eto.Gl.WPF_WinformsHost");
        });

    Target CompileLinux => _ => _
        .DependsOn(CompileLibrary)
        .Executes(() =>
        {
            Compile("Eto.Gl.Gtk2");
        });

    Target CompileMac => _ => _
        .DependsOn(CompileLibrary)
        .Executes(() =>
        {
            Compile("Eto.Gl.Mac", "Eto.Gl.XamMac");
        });

    Target CompileTestLibrary => _ => _
        .OnlyWhenStatic(() => BuildTests == true)
        .TriggeredBy(CompileLibrary)
        .Executes(() =>
        {
            Compile("TestEtoGl");
        });

    Target CompileWindowsTests => _ => _
        .OnlyWhenStatic(() => BuildTests == true)
        .TriggeredBy(CompileWindows)
        .DependsOn(CompileTestLibrary)
        .Executes(() =>
        {
            Compile("TestEtoGl.WinForms", "TestEtoGl.WPF_WinformsHost");
        });

    Target CompileLinuxTests => _ => _
        .OnlyWhenStatic(() => BuildTests == true)
        .TriggeredBy(CompileLinux)
        .DependsOn(CompileTestLibrary)
        .Executes(() =>
        {
            Compile("TestEtoGl.Gtk2");
        });

    Target CompileMacTests => _ => _
        .OnlyWhenStatic(() => BuildTests == true)
        .TriggeredBy(CompileMac)
        .DependsOn(CompileTestLibrary)
        .Executes(() =>
        {
            Compile("TestEtoGl.Mac", "TestEtoGl.XamMac");
        });
}
