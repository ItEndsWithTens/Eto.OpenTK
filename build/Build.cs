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

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts" / Configuration;

    AbsolutePath CustomMsBuildPath;
    AbsolutePath VSDevCmdPath;

    // Some OpenTK projects are F#-based, namely the various test projects. At
    // the moment those tests don't run, since they fail on some platforms when
    // testing features etoViewport doesn't even use. Unfortunately the only way
    // to entirely avoid building them would be to modify the OpenTK build, and
    // that's its own can of worms. This FSharp component is only an extra 120MB
    // or so for a multi-gig VS install, so requiring it seems reasonable.
    const string FSharpComponent = "Microsoft.VisualStudio.Component.FSharp";

    Target SetVisualStudioPaths => _ => _
        .Unlisted()
        .Executes(() =>
        {
            if (EnvironmentInfo.IsWin)
            {
                Logger.Info("Windows build; setting Visual Studio paths.");

                // This will find the latest version of Visual Studio installed
                // on the current system that satisfies all the requirements; if
                // for example you have both VS2017 and VS2019 installed, and
                // the former has both MSBuild and FSharp components while the
                // latter is missing FSharp, this will find the 2017 install.
                VSWhereSettings vswhereSettings = new VSWhereSettings()
                    .EnableLatest()
                    .AddRequires(MsBuildComponent)
                    .AddRequires(FSharpComponent);

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
                        $"MSBuild ({MsBuildComponent})\n" +
                        $"F# language support ({FSharpComponent})");
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

                VSDevCmdPath = (AbsolutePath)Path.Combine(vsPath, "Common7", "Tools", "VsDevCmd.bat");
            }
            else
            {
                Logger.Info("Mono build; no Visual Studio paths to set.");
            }
        });

    Target CompileOpenTK => _ => _
        .OnlyWhenStatic(() => RebuildDependencies == true || !File.Exists(OpenTKRoot / "bin" / "OpenTK" / "OpenTK.dll"))
        .DependsOn(SetVisualStudioPaths)
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
                //
                // Finally, run the build script, skipping OpenTK's tests.
                commandProcessor = "cmd";
                args = $"/C call \"{VSDevCmdPath}\" && .\\build.cmd CopyBinaries";
            }
            else
            {
                commandProcessor = "bash";
                args = "-c \"./build.sh CopyBinaries\"";
            }

            // The version of FAKE used by OpenTK 3.0.1 is legacy, and doesn't
            // use VSWhere to find MSBuild, so an environment variable does the
            // job instead. See this description of FAKE's search procedure:
            // https://github.com/fsharp/FAKE/blob/694f616c97fa242162cfd36db905d7df3156018f/src/legacy/FakeLib/MSBuildHelper.fs#L60
            //
            // Earlier in that same file is a list of known VS versions, which
            // doesn't include anything after 2017:
            // https://github.com/fsharp/FAKE/blob/694f616c97fa242162cfd36db905d7df3156018f/src/legacy/FakeLib/MSBuildHelper.fs#L29
            //
            // Setting this MSBUILD variable is the simplest way to let OpenTK's
            // build script keep working without actually modifying it. Note how
            // this is initialized with EnvironmentInfo.Variables; whatever you
            // pass to StartProcess for environmentVariables will replace any
            // existing variables, and that's no good for a build.
            var vars = new Dictionary<string, string>(EnvironmentInfo.Variables)
            {
                { "MSBUILD", CustomMsBuildPath }
            };

            ProcessTasks.StartProcess(commandProcessor,
                arguments: args,
                workingDirectory: OpenTKRoot,
                environmentVariables: vars,
                logOutput: true,
                logInvocation: true).AssertZeroExitCode();
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
        .DependsOn(CompileOpenTK, Clean)
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
