A customized version of [etoViewport](https://github.com/philstopford/etoViewport), forked primarily to support a [customized version of OpenTK](https://github.com/ItEndsWithTens/opentk).

Additionally, you'll find a build system implemented in [Nuke](https://nuke.build). To build, just clone the repository and run the Nuke build script: with the Nuke global tool installed, simply open a command prompt and run `nuke` in the root directory. Without it, open a PowerShell or Bash terminal as appropriate for your OS and run `.\build.ps1` or `./build.sh`, respectively.

Running the build at least once will ensure OpenTK is built; with its assemblies available to serve as references, the project can be edited and built from within an IDE.

Test projects can be built by appending `--BuildTests` to your build command. OpenTK will be rebuilt if you append `--RebuildDependencies`, or if `lib/opentk/bin/OpenTK/OpenTK.dll` is missing.
