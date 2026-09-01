# Castmill Desktop — Windows x64 installer

Produces `Castmill-<version>-x64.msi`: a per-machine MSI that installs Castmill Desktop to
`%ProgramFiles%\Castmill`, adds a Start Menu shortcut, and registers an Add/Remove Programs
entry. Each build is a major upgrade, so installing a newer MSI replaces the previous one.

## What the payload contains

The MSI carries a **self-contained** publish: the .NET runtime and the Windows App SDK are
bundled, so target machines need neither installed. The only external dependency is the
**WebView2 runtime**, which ships with Windows 11 and current Windows 10.

`CastmillApiBaseAddress` is compiled in (see `src/Castmill.Desktop/Castmill.Desktop.csproj`),
so the installed client is permanently pointed at whichever API that build targeted —
by default the production App Service.

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

    # 2. Pack the MSI.
    wix build installer/windows/Castmill.wxs \
      -arch x64 -ext WixToolset.UI.wixext \
      -d ProductVersion=0.1.0 \
      -d PublishDir=<publish-dir> \
      -d LicenseRtf=installer/windows/License.rtf \
      -o installer/windows/out/Castmill-0.1.0-x64.msi

Verify the publish is genuinely self-contained before packing — `hostfxr.dll`,
`hostpolicy.dll` and `coreclr.dll` must all be present in the publish folder. Without
`-p:SelfContained=true` the publish silently produces a framework-dependent build that
fails on machines without the .NET Desktop Runtime.

## Signing

The MSI is **unsigned**; SmartScreen warns recipients. To sign, use the Windows SDK's
signtool with an organisation code-signing certificate:

    signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 \
      /f <cert.pfx> /p <password> out\Castmill-0.1.0-x64.msi

## Install / uninstall

    msiexec /i Castmill-0.1.0-x64.msi              # interactive
    msiexec /i Castmill-0.1.0-x64.msi /qn          # silent
    msiexec /x Castmill-0.1.0-x64.msi /qn          # uninstall
