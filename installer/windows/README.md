# Castmill Desktop — Windows x64 installer

Produces `Castmill-<version>-x64.msi`: a per-machine MSI that installs Castmill Desktop to
`%ProgramFiles%\Castmill`, adds a Start Menu shortcut, and registers an Add/Remove Programs
entry. Each build is a major upgrade, so installing a newer MSI replaces the previous one.

## What the payload contains

The MSI carries a **self-contained** publish: the .NET runtime and the Windows App SDK are
bundled, so target machines need neither installed. The only external dependency is the
**WebView2 runtime**, which ships with Windows 11 and current Windows 10.

`CastmillApiBaseAddress` is compiled in (see `src/Castmill.Desktop/Castmill.Desktop.csproj`),
and Release builds are locked to the production App Service. Passing a different endpoint
causes the build to fail instead of producing a misdirected installer.

## Prerequisites (build machine)

- .NET SDK per `global.json`, with the MAUI workload installed
- WiX **v5** as a global tool, plus its UI extension:

      dotnet tool install --global wix --version 5.0.2
      wix extension add -g WixToolset.UI.wixext/5.0.2

  WiX v6+ requires accepting the Open Source Maintenance Fee EULA, a commercial obligation.
  v5 is the last MS-RL licensed release, so the build stays on v5 until someone decides
  otherwise.

## Build

    # 1. Publish self-contained x64. RuntimeIdentifierOverride is required: this project
    #    multi-targets Mac Catalyst, and a plain -r leaks the RID into that TFM (which then
    #    demands a Mono runtime pack for a Windows RID). Never pass -p:TargetFrameworks here
    #    either — global properties propagate into the referenced libraries and break their
    #    restore assets.
    dotnet publish src/Castmill.Desktop \
      -f net10.0-windows10.0.19041.0 -c Release \
      -p:RuntimeIdentifierOverride=win-x64 \
      -p:SelfContained=true \
      -p:WindowsPackageType=None \
      -p:WindowsAppSDKSelfContained=true \
      -o <publish-dir>

    # 2. Pack the MSI. PublishDir and LicenseRtf MUST be ABSOLUTE paths: wix resolves
    #    -d values relative to the .wxs file, not the working directory. A relative path
    #    harvests nothing and still exits 0 — you get a ~600 KB MSI that installs an empty
    #    Program Files\Castmill, with only a WIX8601 warning to tell you. See step 3.
    wix build installer/windows/Castmill.wxs \
      -arch x64 -ext WixToolset.UI.wixext \
      -d ProductVersion=0.1.4 \
      -d PublishDir=<ABSOLUTE-publish-dir> \
      -d LicenseRtf=<repo-root>\installer\windows\License.rtf \
      -o installer/windows/out/Castmill-0.1.4-x64.msi

Verify the publish is genuinely self-contained before packing — `hostfxr.dll`,
`hostpolicy.dll` and `coreclr.dll` must all be present in the publish folder. Without
`-p:SelfContained=true` the publish silently produces a framework-dependent build that
fails on machines without the .NET Desktop Runtime.

    # 3. Verify the MSI actually carries the payload. Both silent failure modes above
    #    produce a well-formed MSI, so check size and file count, not the exit code.
    #    Expect ~89 MB and the same file count as the publish folder (861 for 0.1.4).
    ls -l installer/windows/out/Castmill-0.1.4-x64.msi   # must be ~89 MB, NOT ~600 KB

## Signing

The MSI is **unsigned**; SmartScreen warns recipients. To sign, use the Windows SDK's
signtool with an organisation code-signing certificate:

    signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 \
      /f <cert.pfx> /p <password> out\Castmill-0.1.4-x64.msi

## Install / uninstall

    msiexec /i Castmill-0.1.4-x64.msi              # interactive
    msiexec /i Castmill-0.1.4-x64.msi /qn          # silent
    msiexec /x Castmill-0.1.4-x64.msi /qn          # uninstall
