#!/usr/bin/env bash
# Compile-only check of the iOS-specific C# on Linux/Windows without the iOS workload (see docs/DEPLOYMENT_IOS.md).
# It verifies API names and C# against Microsoft.iOS 27 reference assemblies and the MAUI iOS libraries; it does not
# replace a real build, signing or a run on an iPhone, which need macOS with Xcode.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
ios_ref_version="27.0.10722"
maui_version="10.0.110"
cache="${IOS_REF_CACHE:-$HOME/.cache/bonikpos/microsoft.ios.ref.net10.0_27.0.$ios_ref_version}"
if [[ ! -f "$cache/ref/net10.0/Microsoft.iOS.dll" ]]; then
  mkdir -p "$cache"
  curl -fsSL -o "$cache/pkg.nupkg" \
    "https://api.nuget.org/v3-flatcontainer/microsoft.ios.ref.net10.0_27.0/$ios_ref_version/microsoft.ios.ref.net10.0_27.0.$ios_ref_version.nupkg"
  (cd "$cache" && unzip -q -o pkg.nupkg 'ref/*' && rm pkg.nupkg)
fi
nuget_root="$(dotnet nuget locals global-packages --list | sed 's/^global-packages: //')"
# The App references these MAUI packages; restoring the Android app (or any MAUI project) puts their iOS libraries in the cache.
for p in microsoft.maui.core microsoft.maui.controls.core microsoft.maui.essentials microsoft.maui.graphics; do
  [[ -d "$nuget_root/$p/$maui_version/lib/net10.0-ios26.0" ]] || { echo "Missing $p $maui_version in $nuget_root (restore the App first)"; exit 2; }
done
dotnet build "$root/src/BonikPOS.VoiceSearch.Presentation/BonikPOS.VoiceSearch.Presentation.csproj" -c Release -nologo -v q
dotnet build "$root/src/BonikPOS.VoiceSearch.Audio/BonikPOS.VoiceSearch.Audio.csproj" -c Release -f net10.0 -nologo -v q
dotnet build "$root/src/BonikPOS.VoiceSearch.Infrastructure/BonikPOS.VoiceSearch.Infrastructure.csproj" -c Release -nologo -v q
for proj in IosAudioCheck IosAppServicesCheck; do
  dotnet build "$here/$proj.csproj" -c Release -nologo -v q -p:IosRefDir="$cache/ref/net10.0" -p:MauiVersion="$maui_version" \
    -p:NuGetPackageRoot="$nuget_root/"
done
echo "iOS compile check passed (reference assemblies only; not a device build)."
