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

    static AbsolutePath OpenTKRoot = RootDirectory / "lib" / "opentk";

    [Parameter("Whether to rebuild vendored dependencies - Default is false (local) or true (server)")]
    readonly bool RebuildDependencies = IsLocalBuild ? false : true;

    [Solution("Eto.Gl.sln")] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    AbsolutePath VSInstallationPath;
    AbsolutePath CustomMsBuildPath;
    AbsolutePath VSDevCmdPath;

    public string GetMsBuildPath()
    {
        if (!EnvironmentInfo.IsWin)
        {
            throw new PlatformNotSupportedException("GetMsBuildPath only works in Windows!");
        }

        VSWhereSettings vswhereSettings = new VSWhereSettings()
            .EnableLatest()
            .AddRequires(MsBuildComponent);

        IReadOnlyCollection<Output> output = VSWhere(s => vswhereSettings).Output;

        string vsPath = output.FirstOrDefault(o => o.Text.StartsWith("installationPath")).Text.Replace("installationPath: ", "");
        VSInstallationPath = (AbsolutePath)vsPath;
        string vsVersion = output.FirstOrDefault(o => o.Text.StartsWith("installationVersion")).Text.Replace("installationVersion: ", "");
        Int32.TryParse(vsVersion.Split('.')[0], out int vsMajor);

        if (vsMajor < 15)
        {
            throw new Exception("Can't build with less than VS 2017!");
        }

        return Path.Combine(vsPath, "MSBuild", vsMajor.ToString() + ".0", "Bin", "MSBuild.exe");
    }

    public static string GetVSDevCmdPath()
    {
        if (!EnvironmentInfo.IsWin)
        {
            throw new PlatformNotSupportedException("GetVSDevCmdPath only works in Windows!");
        }

        IReadOnlyCollection<Output> output = VSWhere(s => s.EnableLatest()).Output;

        string vsPath = output.FirstOrDefault(o => o.Text.StartsWith("installationPath")).Text.Replace("installationPath: ", "");

        return Path.Combine(vsPath, "Common7", "Tools", "VsDevCmd.bat");
    }

    Target Clean => _ => _
        .Executes(() =>
        {
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target SetVisualStudioPaths => _ => _
        .Executes(() =>
        {
            if (EnvironmentInfo.IsWin)
            {
                // Windows developers with Visual Studio installed to a directory
                // other than System.Environment.SpecialFolder.ProgramFilesX86 need
                // to tell Nuke the path to MSBuild.exe themselves.
                CustomMsBuildPath = (AbsolutePath)GetMsBuildPath();

                VSDevCmdPath = (AbsolutePath)GetVSDevCmdPath();
            }
        });

    Target Restore => _ => _
        .DependsOn(Clean, SetVisualStudioPaths)
        .Executes(() =>
        {
            MSBuild(settings => settings
                .When(CustomMsBuildPath != null, s =>s
                    .SetToolPath(CustomMsBuildPath))
                .SetTargetPath(Solution)
                .SetTargets("Restore"));
        });

    Target CompileOpenTK => _ => _
        .OnlyWhenStatic(() => RebuildDependencies == true || !File.Exists(OpenTKRoot / "bin" / "OpenTK" / "OpenTK.dll"))
        .DependsOn(Restore)
        .Executes(() =>
        {
            // If the OpenTK directory doesn't exist, or it exists but is empty,
            // the submodule needs to be cloned before the build. Otherwise, it
            // can probably be assumed there's a copy of the code in place, but
            // with uncommitted changes someone wants to test, so leave them be.
            if (!DirectoryExists(OpenTKRoot) || !Directory.EnumerateFileSystemEntries(OpenTKRoot).Any())
            {
                Git("submodule update --init lib/opentk");
            }

            string commandProcessor;
            string args;

            if (EnvironmentInfo.IsWin)
            {
                // Expressly run the OpenTK build script with the 'cmd' command
                // processor, in case this Nuke script is running in PowerShell.
                //
                // Use '/C' to run the following string as a command, then exit.
                //
                // Run VsDevCmd.bat first to set up the proper environment for
                // the OpenTK build, using 'call' to ensure said .bat doesn't
                // exit the enclosing shell when it's finished.
                //
                // Use a double ampersand so cmd will wait for the first command
                // to finish before starting the second one.
                commandProcessor = "cmd";
                args = $"/C call \"{VSDevCmdPath}\" && build.cmd";
            }
            else
            {
                commandProcessor = "bash";
                args = "-c build.sh";
            }

            ProcessTasks.StartProcess(commandProcessor,
                arguments: args,
                workingDirectory: OpenTKRoot,
                logOutput: true,
                logInvocation: true).AssertZeroExitCode();
        });

    private void Compile(params string[] projects)
    {
        MSBuild(settings => settings
            .SetTargets("Build")
            .SetConfiguration(Configuration)
            .When(CustomMsBuildPath != null, s => s
             .SetToolPath(CustomMsBuildPath))
            .SetMaxCpuCount(Environment.ProcessorCount)
            .SetNodeReuse(IsLocalBuild)
            .CombineWith(projects, (s, p) => s
                .SetProjectFile(Solution.GetProject($"{p}"))));
    }

    Target CompileLibrary => _ => _
        .DependsOn(CompileOpenTK)
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
