# iOS deployment

How to build, sign, run and check the BonikPOS Voice Search app (`com.bonikpos.voicesearch`) on iPhone. Each section
says whether it was **verified here** or is **standard .NET MAUI procedure that was not executed here**.

> **Status (2026-10-09).** The iOS app has **never been built**, and **nothing has run on an iPhone or in the iOS
> Simulator**.
>
> The development environment is Linux, and the iOS workload needs macOS with Xcode. Both projects with iOS code add
> the `net10.0-ios` target framework only when they are not building on Linux:
> `Condition="!$([MSBuild]::IsOSPlatform('linux'))"` in `src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj`
> and `src/BonikPOS.VoiceSearch.Audio/BonikPOS.VoiceSearch.Audio.csproj`.
>
> The only verification is the compile-only check in §1. It catches API and C# errors in the iOS-specific code. It is
> **not** a build, link, signing or device test.

## 1. What was verified here: compile check

`tools/ios-compile-check/ios-compile-check.sh` compiles the iOS-specific C# with the `IOS` symbol defined.

What it compiles:

- `IosAudioCheck.csproj` compiles all of `src/BonikPOS.VoiceSearch.Audio` except `Platforms/Android`. That includes
  `Platforms/iOS`: `IosMicrophoneCapture` (AVAudioEngine input tap) and `IosAudioOutput`.
- `IosAppServicesCheck.csproj` compiles `src/BonikPOS.VoiceSearch.App/Services/*.cs`, including the `#if IOS`
  branches. Those cover thermal state, backup exclusion, the Documents side-load path and UIKit battery sampling.

What it compiles against:

- `Microsoft.iOS.dll` from the **Microsoft.iOS.Ref.net10.0_27.0** package, version **27.0.10722**. The script
  downloads it from nuget.org with `curl` on the first run and caches it in `$IOS_REF_CACHE`, by default
  `~/.cache/bonikpos/…`.
- The **MAUI 10.0.110** iOS libraries (`lib/net10.0-ios26.0` of Microsoft.Maui.Core, Controls.Core, Essentials and
  Graphics) from the local NuGet cache. Restore or build the Android app first so that they are present.

Warnings are errors (`Directory.Build.props`), except the platform-compatibility analyzers CA1416 and CA1422, which
need the real `net10.0-ios` target framework and so only run in a real iOS build. Result: passed
([TEST_RESULTS.md](TEST_RESULTS.md)).

```bash
tools/ios-compile-check/ios-compile-check.sh
# last line on success: "iOS compile check passed (reference assemblies only; not a device build)."
```

Not covered by the check:

- `MauiProgram.cs` and the App's `Platforms/iOS/*.cs` (`AppDelegate`, `Program`, `SceneDelegate`).
- XAML and resource processing, and `Info.plist` / privacy-manifest processing.
- Trimming and AOT compilation.
- Native linking of ONNX Runtime.
- Entitlements, code signing and any execution.

`tools/IosCompileCheck/IosCompileCheck.csproj` is an older, narrower project that covers only the Audio project's
`Platforms/iOS` files. The script above is the check that was run.

## 2. Prerequisites (on a Mac; standard, not verified here)

| Item | Requirement |
|---|---|
| macOS + Xcode | The Xcode version required by the Microsoft.iOS SDK that your .NET MAUI workload installs. Check that SDK version's release notes; the pairing was not determined here. MAUI 10.0.110's iOS libraries are built for `net10.0-ios26.0`, and the compile check used the iOS 27 reference assembly. |
| .NET | .NET 10 SDK. `global.json` requires 10.0.100 or a later feature band; the Android builds here used SDK 10.0.401. |
| Workloads | `maui-ios`. On macOS the App project also targets `net10.0-android`, and restore may require the workloads of every target framework, so also install `maui-android` (or install the `maui` workload). Always pass `-f net10.0-ios`. |
| Apple account | Apple Developer Program membership for device builds, TestFlight and the App Store |
| Device | iPhone or iPad with iOS 16.0 or later (`SupportedOSPlatformVersion` 16.0), arm64 (`UIRequiredDeviceCapabilities`) |

## 3. Project configuration for iOS

| Item | Configuration |
|---|---|
| Bundle id, name, version | `com.bonikpos.voicesearch`, "BonikPOS Voice Search", 0.1.0 (build 1) (`ApplicationDisplayVersion` / `ApplicationVersion`) |
| `Platforms/iOS/Info.plist` | `UIDeviceFamily` 1, 2; arm64 required; portrait and landscape orientations; scene manifest (`SceneDelegate`); `NSMicrophoneUsageDescription` (quoted below). No `UIBackgroundModes`, so capture is expected to stop when the app leaves the foreground (not tested). No App Transport Security exceptions. The optional BonikPOS API must use HTTPS anyway. |
| `Platforms/iOS/DebugFileSharing.plist` | Merged as a `PartialAppManifest` **only in Debug iOS builds**. Sets `UIFileSharingEnabled` and `LSSupportsOpeningDocumentsInPlace`, so the app's `Documents` folder appears in the Files app and in Finder, for side-loading models and benchmark clips (§6). Release builds do not expose `Documents`. |
| `Platforms/iOS/Resources/PrivacyInfo.xcprivacy` | Privacy manifest (§8). Expected at the root of the app bundle through MAUI's iOS resource folder; check this in the built `.app`. |
| ONNX Runtime native library | The App references `Microsoft.ML.OnnxRuntime` directly with `ExcludeAssets="native"` (also used for Android; see [DEPLOYMENT_ANDROID.md](DEPLOYMENT_ANDROID.md) §3). See below. |
| Accelerator | With *Settings → Processing → Automatic*, the Whisper encoder and decoder and the TSE sessions try the CoreML provider (`ModelFormat=MLProgram`, `MLComputeUnits=ALL`, `src/BonikPOS.VoiceSearch.AI/Onnx/OnnxSessionFactory.cs`). They fall back to the CPU when it is unavailable or rejects a model. VAD and speaker encoders always run on the CPU. |
| Backups | `MauiAppPaths` sets `NSURLIsExcludedFromBackupKey` on the app data folder (profile, history, settings) and on the extracted-models folder. |
| Secure storage | MAUI `SecureStorage` (Keychain) holds the profile and history data keys and the API token. The project has no `Entitlements.plist`. MAUI's SecureStorage documentation calls for a Keychain entitlement when running in the iOS Simulator; check that first if enrollment cannot be saved there. |

How the ONNX Runtime native library gets into the app: the package's
`build/net9.0-ios18.0/Microsoft.ML.OnnxRuntime.targets` adds `runtimes/ios/native/onnxruntime.xcframework.zip` as a
`NativeReference`, but only to app projects (`OutputType` other than `Library`). The reference is linked statically,
as C++ with `-lc++`, force-loaded, and with CoreML weak-linked. This is why the App references the package directly.

The xcframework in the 1.31.0 package has these slices: `ios-arm64` (device), `ios-arm64_x86_64-simulator` and a Mac
Catalyst slice. The device slice includes the CoreML provider header. The package has no `PrivacyInfo.xcprivacy` file.
All of this was checked by listing the package contents.

Microphone permission text (`NSMicrophoneUsageDescription`):

> BonikPOS Voice Search uses the microphone to hear the medicine name you say and, during voice enrollment, to create
> your voice profile. Audio is processed on this device; it is not uploaded or stored.

## 4. Debug build and run (standard .NET MAUI procedure, not executed here)

From `BonikPOS Mobile AI/` on the Mac:

```bash
dotnet build src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj -f net10.0-ios -c Debug

# Simulator: pick a UDID from `xcrun simctl list devices available`
dotnet build src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj -f net10.0-ios -c Debug -t:Run \
  -p:_DeviceName=:v2:udid=<SIMULATOR_UDID>

# Device: the UDID is shown in Xcode → Window → Devices and Simulators
dotnet build src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj -f net10.0-ios -c Debug -t:Run \
  -p:RuntimeIdentifier=ios-arm64 -p:_DeviceName=<DEVICE_UDID> \
  -p:CodesignKey="Apple Development: <Name> (<TEAMID>)" -p:CodesignProvision="<development profile name>"
```

Signing for a device:

1. In the Apple Developer portal or in Xcode, create an App ID for `com.bonikpos.voicesearch`, an Apple Development
   certificate, and a development provisioning profile that includes the test iPhone.
2. Install them on the Mac. Xcode → Settings → Accounts can download them.
3. If the build does not pick them automatically, pass `CodesignKey` and `CodesignProvision` as shown above.

The microphone needs no entitlement, only the usage description.

Use a physical iPhone for anything involving audio, latency or CoreML. The ONNX Runtime package has a simulator slice,
but the app has not been run in the simulator.

## 5. Release archive and App Store upload (standard .NET MAUI procedure, not executed here)

1. Bundle the models the release needs. **Release builds load only bundled models** and have no `Documents` file
   sharing. No speech-recognition model is bundled today, so follow [MODELS.md](MODELS.md) §4 first.
2. Create a distribution certificate and an App Store provisioning profile for `com.bonikpos.voicesearch`.
3. Archive and publish:

   ```bash
   dotnet publish src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj -f net10.0-ios -c Release \
     -p:ArchiveOnBuild=true -p:RuntimeIdentifier=ios-arm64 \
     -p:CodesignKey="Apple Distribution: <Name> (<TEAMID>)" \
     -p:CodesignProvision="<App Store profile name>" \
     -p:ApplicationVersion=1 -p:ApplicationDisplayVersion=0.1.0
   ```

   Increase `ApplicationVersion` (`CFBundleVersion`) for every upload. The `.ipa` is written to
   `src/BonikPOS.VoiceSearch.App/bin/Release/net10.0-ios/ios-arm64/publish/`, and `ArchiveOnBuild` also creates an
   archive that Xcode's Organizer can see.

4. Upload with Apple's Transporter app or the Xcode Organizer. Test through TestFlight before release.

Release builds are AOT-compiled and trimmed, as is standard for .NET on iOS. Whether a Release build produces any
warnings, which `TreatWarningsAsErrors` would turn into errors, is unknown. Trimming problems might also appear only
at run time.

## 6. Side-loading models and benchmark clips (Debug builds only)

The side-load folders are `Documents/models/` and `Documents/benchmark/`. The code paths are
`Environment.SpecialFolder.MyDocuments` and `MauiAppPaths` in `src/BonikPOS.VoiceSearch.App/Services/MauiPlatformServices.cs`.

1. Prepare a `models` folder: a `models.json` manifest plus the model files at the paths it names
   ([MODELS.md](MODELS.md) §4.4). For benchmark clips, prepare `clips.json` plus 16 kHz WAV files
   ([BENCHMARKS.md](BENCHMARKS.md) §5).
2. Copy the folders into the app's `Documents`. Either use the Files app on the iPhone (*On My iPhone → BonikPOS
   Voice Search*), or use Finder on a Mac with the iPhone connected (*Files* tab).
3. In the app, open **Diagnostics → Reload models** and check the *Models* card. It shows state, execution provider
   (`CoreML` or `CPU`), init time and any checksum or contract message.

Side-loaded models are ignored in Release builds ([MODELS.md](MODELS.md) §2).

## 7. Pipeline benchmark on an iPhone

The benchmark replays clips through the full pipeline (VAD, extraction, owner verification, recognition, search). It
runs on the CPU and then with CoreML. It never uses the microphone and stores no audio. The general procedure is in
[BENCHMARKS.md](BENCHMARKS.md) §5.

1. Install a **Debug** build. It packages the synthetic clips and accepts side-loaded models and clips.
   - Debug builds are not optimised. Record the configuration with the report; the report does not include it.
   - Release builds have neither the clips nor `Documents` file sharing, so a Release-configuration benchmark needs
     clips copied by some other route. That is not set up in this repository; one option is a local build that also
     merges `DebugFileSharing.plist`.
2. Install a Whisper export (§6). Without a Whisper or TSE model, every session runs on the CPU, so the CoreML run
   measures nothing new; the report then says that no accelerator was used.
3. Charge the phone, unplug it, close other apps and let it cool down.
4. Open **Diagnostics** (search-screen menu, or *Settings → Model and performance diagnostics*) and go to
   **Full pipeline benchmark**.
5. Leave **Compare CPU and accelerator** on (a CPU-only run, then *Auto* = CoreML for Whisper and TSE) and press
   **Run benchmark**. It runs 3 repetitions in real time, takes a few minutes and keeps the screen on.
6. Press **Copy Markdown** and **Copy JSON**. Store the reports with the device model, iOS version, build
   configuration, model ids and hashes ([EVALUATION_PLAN.md](EVALUATION_PLAN.md) §7).

iOS-specific readings:

- **Battery**: `UIDevice` battery level and state, read on the main thread. On the simulator the level is −1 and is
  shown as unavailable. iOS has **no public battery-current API**, so the report has no current or power figures. For
  energy, use Xcode's energy gauge or Instruments.
- **Thermal state**: `NSProcessInfo.ThermalState` (`Nominal`, `Fair`, `Serious`, `Critical`), recorded before and
  after each run. Diagnostics flags `Serious` and `Critical`.
- **Input latency**: not part of the replay. During a live voice search, read `audio.input_latency_ms` in the
  *Performance* card. The tap computes it as the age of the newest buffer's host time, minus the buffer duration,
  plus `AVAudioSession.InputLatency`.

## 8. App Store privacy: manifest and privacy label

`PrivacyInfo.xcprivacy` declares:

- `NSPrivacyTracking` false and an empty `NSPrivacyCollectedDataTypes`.
- Required-reason APIs for file timestamps (`C617.1`), system boot time (`35F9.1`) and disk space (`E174.1`), which
  the MAUI template includes for .NET and MAUI.
- User defaults (`CA92.1`), used through MAUI `Preferences` to remember whether the microphone permission was
  already requested.

ONNX Runtime is linked statically into the app binary, and its xcframework has no privacy manifest (§3). Whether its
code uses APIs that must be declared was not checked. App Store Connect's validation at upload is the first check.

For the App Privacy answers, describe this behaviour and keep it consistent with the manifest and with
[PRIVACY.md](PRIVACY.md). Check Apple's current definitions; under them, data processed only on the device and not
sent off it is not "collected".

| Data / behaviour | What the app does |
|---|---|
| Microphone audio | Processed in memory on the device; not uploaded or stored |
| Owner voice profile | Speaker embeddings stored on the device, encrypted with AES-256-GCM. The key is in the Keychain, the folder is excluded from backup, and *Delete all app data* erases them. |
| Search history | Text only, encrypted on the device; optional |
| Diagnostics and benchmark reports | Numbers only; copied to the clipboard only when the user taps a copy button |
| Network | Only the optional BonikPOS product API, when configured. HTTPS GET requests with the stored token or API key; the app receives product data. Whether this counts as collection depends on who operates that server. |
| Third-party SDKs | No analytics, advertising or crash-reporting SDK. ONNX Runtime telemetry is disabled in code (`ORT_DISABLE_TELEMETRY` through `setenv`, plus `OrtEnv.DisableTelemetryEvents()`, first thing in `MauiProgram.CreateMauiApp`); this is **not verified on a device**. |

## 9. Must be verified on a real iPhone

- [ ] Debug and Release builds succeed on a Mac. Warnings are errors, including CA1416/CA1422, which the compile check
      skipped.
- [ ] Signing, installation and launch work on a device with iOS 16 and on one with a current iOS.
- [ ] The privacy manifest is present in the `.app`, and the App Store Connect upload validates.
- [ ] First launch extracts the bundled models: Diagnostics shows VAD and the speaker encoder as *Ready · CPU*.
- [ ] The microphone permission prompt shows the text above. Denial is handled, and the Settings link works.
      `AVAudioApplication` is used on iOS 17+, and `AVAudioSession` record permission on iOS 16.
- [ ] **AVAudioEngine capture**: hardware sample rate and channel count, and the actual tap buffer sizes. The code
      requests sample rate / 50 frames (20 ms), but iOS chooses the delivered size, which the code's own comment says
      is typically 100 ms or more. Also check the resampling to 16 kHz, and that `audio.frames_dropped` and
      `audio.discontinuities` stay at 0 in long sessions.
- [ ] Whether the preferred 10 ms IO buffer is accepted; failure is only logged at debug level.
- [ ] The *Measurement* (default) and *Default microphone* input modes.
- [ ] Interruptions (call, Siri, alarm) and route changes (wired or Bluetooth headset) restart capture as coded.
- [ ] Behaviour when the app goes to the background or the screen locks.
- [ ] **Input-latency estimate** `audio.input_latency_ms` is plausible, ideally checked against an external
      measurement.
- [ ] **CoreML provider acceptance** for each Whisper encoder/decoder and TSE model:
  - the provider shown in Diagnostics, and "CoreML unavailable for `<file>`; using CPU" warnings in the log;
  - session init time (Core ML compiles the model when the session is created);
  - how much of each graph Core ML takes (the decoder has dynamic sequence axes);
  - results compared with the CPU, because `MLComputeUnits=ALL` lets Core ML use the GPU and Neural Engine, which
    may compute at reduced precision;
  - the CPU vs CoreML timings in the benchmark.
- [ ] **ONNX Runtime telemetry opt-out** by network capture: no lookup of or connection to
      `mobile.events.data.microsoft.com` during launch, warm-up, enrollment, searches and relaunch. The procedure,
      including the `rvictl` capture option and a control build, is in [PRIVACY.md](PRIVACY.md) §5.
- [ ] `OnnxRuntimeTelemetry.NativeFlagSet` is `true` on iOS. The `setenv` call through `libc` has been verified only
      on Linux, and the flag is not shown in the UI.
- [ ] Keychain storage: enrollment survives an app restart, and *Delete all app data* and *Delete voice profile* work.
- [ ] Memory (peak working set) and thermal state with the chosen Whisper model during long listening sessions.
- [ ] The trimmed, AOT-compiled Release build behaves like Debug: source-generated JSON, XAML source generation and
      ONNX Runtime interop with the static library.
