# Android deployment

How to build, sign, install and check the BonikPOS Voice Search app (`com.bonikpos.voicesearch`) on Android. Each
section says whether it was **verified here** (in the development container, 2026-10-09) or is **standard .NET MAUI
procedure that was not executed here**.

> **Status.**
>
> **Verified here:** the Debug build (0 warnings, 0 errors, with warnings treated as errors), and inspection of the
> resulting APK and merged manifest (§2, §4).
>
> **Not done here:**
>
> - A Release build, signing, an `.aab`, and a Play Console upload.
> - Any run on a device or emulator. The container has no hardware virtualisation (KVM), and dl.google.com, which is
>   needed for emulator system images, is blocked.
>
> As a result, nothing about the app's runtime behaviour on Android has been observed: model loading, XNNPACK,
> microphone capture, latency, battery and network traffic are all unobserved.

Environment of the verified build:

- Ubuntu 24.04 x86-64 container.
- .NET 10 SDK. `global.json` requires 10.0.100 or a later feature band; the build here used SDK 10.0.401.
- Workload `maui-android`: MAUI 10.0.110, Microsoft.Android.Sdk 36.1.69.
- OpenJDK 21.
- Android SDK in `/opt/android-sdk` (the build used build-tools 36.0.0).

## 1. Prerequisites

| Item | Requirement |
|---|---|
| .NET | .NET 10 SDK (see `global.json`) with the `maui-android` workload (`dotnet workload install maui-android`) |
| JDK | JDK 21 (OpenJDK 21 was used here) |
| Android SDK | Platform and build-tools for API 36. The merged manifest targets API 36. Missing pieces can be installed with the standard `dotnet build -t:InstallAndroidDependencies -f net10.0-android -p:AndroidSdkDirectory=<sdk> -p:JavaSdkDirectory=<jdk> -p:AcceptAndroidSDKLicenses=True`, which was not run here. |
| Device | Android 7.0 (API 24) or later (`SupportedOSPlatformVersion` 24.0), with an arm64-v8a CPU (or an x86_64 emulator) and a microphone. The manifest declares `android.hardware.microphone` as required, so Google Play does not offer the app to devices without one. |
| Tools for a device | `adb` (Android SDK `platform-tools`), with USB debugging enabled on the phone |

Environment variables (values from this container):

```bash
export DOTNET_ROOT=/opt/dotnet
export ANDROID_HOME=/opt/android-sdk ANDROID_SDK_ROOT=/opt/android-sdk
export JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64
```

## 2. Debug build (verified here)

From `BonikPOS Mobile AI/`:

```bash
dotnet build src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj -f net10.0-android -c Debug
```

Result here: `Build succeeded. 0 Warning(s) 0 Error(s)`. `TreatWarningsAsErrors` is on in `Directory.Build.props`.

The output is `src/BonikPOS.VoiceSearch.App/bin/Debug/net10.0-android/com.bonikpos.voicesearch-Signed.apk`
(51,349,355 bytes, about 51 MB), signed with the SDK's debug keystore. Inspection with `unzip -l` and `readelf`
showed:

- **Native libraries**: `lib/arm64-v8a/` and `lib/x86_64/` only. They contain `libonnxruntime.so` (34.2 MB and
  40.8 MB uncompressed) and `libonnxruntime4j_jni.so`, both from the ONNX Runtime 1.31.0 AAR, plus the .NET for
  Android runtime libraries. Every `.so` has 16 KB-aligned ELF `LOAD` segments (`0x4000`).
- **Models**: `assets/models/models.json`, `assets/models/vad/silero-vad-6.2.3-16k.onnx` and
  `assets/models/speaker/ge2e-resemblyzer-0.1.4.onnx`.
- **Benchmark clips (Debug only)**: `assets/benchmark/clips.json` and 10 `synthetic-*.wav` text-to-speech clips.
- **No managed assemblies**: this Debug build uses Fast Deployment (`embedassembliesintoapk=false` in
  `obj/Debug/net10.0-android/build.props`) and the Mono interpreter (`androidaotmode=interpreter`;
  Microsoft.Android.Sdk 36.1.69 sets `UseInterpreter` to true for Debug by default).

This has two consequences:

1. Installing this APK on its own with `adb install` is expected to fail at app start, because the assemblies are
   deployed separately (this was not tried here). Use one of the commands below.
2. Debug timings are pessimistic for the C# parts of the pipeline, which run interpreted. Use Release builds for
   benchmark numbers you report (§8).

Install on a device (standard procedure, not executed here because no device was available):

```bash
adb devices                                   # the phone must be listed
# build, deploy (including the assemblies) and launch
dotnet build src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj -f net10.0-android -c Debug -t:Run

# alternative: a self-contained Debug APK that adb can install by itself
dotnet build src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj -f net10.0-android -c Debug -p:EmbedAssembliesIntoApk=true
adb install -r src/BonikPOS.VoiceSearch.App/bin/Debug/net10.0-android/com.bonikpos.voicesearch-Signed.apk
```

Add `-p:RuntimeIdentifier=android-arm64` to build for a single ABI.

## 3. Project settings that matter on Android

All of these are in `src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj` unless noted.

| Setting | Value / effect |
|---|---|
| `ApplicationId`, `ApplicationTitle` | `com.bonikpos.voicesearch`, "BonikPOS Voice Search" |
| `ApplicationDisplayVersion` / `ApplicationVersion` | `0.1.0` / `1` (`versionName` / `versionCode`) |
| `SupportedOSPlatformVersion` | `24.0` in the App and Audio projects, giving `minSdkVersion` 24. The merged manifest has `targetSdkVersion` 36, which comes from the SDK; the project does not set it. |
| Models | `MauiAsset` for every `*.onnx`, `*.json` and `*.txt` under the solution's `models/` folder, with logical name `models/...`. They are extracted at first launch ([MODELS.md](MODELS.md) §2). |
| Benchmark clips | `MauiAsset` items **only when `Configuration` is `Debug`**: `tests/fixtures/audio/benchmark-clips.json` → `benchmark/clips.json`, and `tests/fixtures/audio/synthetic-*.wav` → `benchmark/`. Release builds contain no audio. |
| ONNX Runtime | `PackageReference Microsoft.ML.OnnxRuntime` directly in the App, so that the package's build targets add the native mobile binaries. For Android, `build/net9.0-android35.0/Microsoft.ML.OnnxRuntime.targets` adds `runtimes/android/native/onnxruntime.aar` as an `AndroidLibrary`. |
| `ExcludeAssets="native"` | Set on that reference for the Android and iOS target frameworks (the reason follows this table). |
| Build strictness | `Directory.Build.props`: `TreatWarningsAsErrors`, `Nullable`, `Deterministic`, central package versions |

**Why `ExcludeAssets="native"`.** The NuGet package also contains desktop binaries, such as
`runtimes/linux-x64/native/libonnxruntime.so`, a glibc build. When NuGet restores for the `android-x64` runtime
identifier, its RID fallback (`android-x64` → `linux-x64`) selects that desktop library and packs it into the APK's
`lib/x86_64/`. There it shadows the AAR's Android x86_64 library and cannot load. Excluding the package's `native`
assets leaves the AAR as the only source of `libonnxruntime.so`. The verified APK above shows the result.

## 4. Manifest: permissions, backup, telemetry

Sources: `Platforms/Android/AndroidManifest.xml` and `Platforms/Android/Resources/xml/data_extraction_rules.xml`
(under `src/BonikPOS.VoiceSearch.App/`).

| Item | Configuration |
|---|---|
| `RECORD_AUDIO` | Requested at run time, only when the owner starts the microphone test or voice search |
| `INTERNET`, `ACCESS_NETWORK_STATE` | Only for the optional BonikPOS product API (catalog sync). The app enforces HTTPS for it, except for loopback addresses. The ONNX Runtime AAR's own manifest also declares both permissions. |
| `uses-feature android.hardware.microphone` | `required="true"` |
| Backup | `android:allowBackup="false"`. `android:dataExtractionRules` excludes the `root`, `file`, `database`, `sharedpref` and `external` domains from both cloud backup and device-to-device transfer (Android 12+). |
| ONNX Runtime telemetry | See below. |

**Verified here** in the merged manifest of the Debug build
(`obj/Debug/net10.0-android/android/manifest/AndroidManifest.xml`):

- `minSdkVersion` 24 and `targetSdkVersion` 36.
- Permissions: the three above, plus `com.bonikpos.voicesearch.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION`, a
  signature-level permission contributed by a library during the manifest merge.
- No `ai.onnxruntime.TelemetryInitializer`.

ONNX Runtime 1.31.0 ships Microsoft's 1DS telemetry client in its Android library: the arm64 `libonnxruntime.so` in
the AAR contains the collector URL `https://mobile.events.data.microsoft.com/OneCollector/1.0`. The AAR also declares
an `ai.onnxruntime.TelemetryInitializer` content provider, which runs at app start. The app turns telemetry off in
three ways:

1. The provider is removed with `tools:node="remove"`. This is verified in the merged manifest above.
2. `OnnxRuntimeTelemetry.Disable()` is the first statement of `MauiProgram.CreateMauiApp`. It sets
   `ORT_DISABLE_TELEMETRY=1` in the native process environment through `setenv` (`src/BonikPOS.VoiceSearch.AI/Onnx/OnnxRuntimeTelemetry.cs`).
3. The same method calls `OrtEnv.DisableTelemetryEvents()`.

**Not verified here:** that no telemetry traffic leaves a real device. That needs a network capture on a phone (§11;
procedure in [PRIVACY.md](PRIVACY.md) §5).

## 5. Release build and signing (standard .NET MAUI procedure, not executed here)

1. Create an upload key once, and keep the keystore and its passwords outside the repository:

   ```bash
   keytool -genkeypair -v -keystore bonikpos-upload.keystore -alias bonikpos-upload -keyalg RSA -keysize 2048 -validity 10000
   ```

2. Bundle the models the release needs. Release builds **ignore side-loaded models** and load only bundled ones.
   Since no speech-recognition model is bundled today, the export and registration in [MODELS.md](MODELS.md) §4 must
   happen before the build.
3. Publish a signed build. Passwords are read from environment variables through the `env:` prefix:

   ```bash
   export BONIK_STORE_PASS=... BONIK_KEY_PASS=...
   dotnet publish src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj -f net10.0-android -c Release \
     -p:AndroidKeyStore=true \
     -p:AndroidSigningKeyStore=/secure/path/bonikpos-upload.keystore \
     -p:AndroidSigningKeyAlias=bonikpos-upload \
     -p:AndroidSigningStorePass=env:BONIK_STORE_PASS \
     -p:AndroidSigningKeyPass=env:BONIK_KEY_PASS \
     -p:ApplicationVersion=1 -p:ApplicationDisplayVersion=0.1.0
   ```

   Increase `ApplicationVersion` (`versionCode`) for every upload. The signed `.aab` and `.apk` are written to
   `src/BonikPOS.VoiceSearch.App/bin/Release/net10.0-android/publish/`.

4. Upload the `.aab` to a Play Console testing track. With Play App Signing, Google signs the delivered APKs and the
   key above is only the upload key.

Release defaults in the installed Microsoft.Android.Sdk 36.1.69 that differ from Debug:

- Assemblies are embedded in the package.
- Package formats are `aab;apk`.
- Trimming is enabled (link mode `SdkOnly`, `TrimMode` partial).
- No interpreter.

In this trim mode the SDK sets `SuppressTrimAnalysisWarnings` to true (unless `TrimMode` is `full`), so trimming
problems would not show up as build warnings. They would only appear at run time, which is one more reason to test a
Release build on a device. Release builds also contain no benchmark clips (§8).

## 6. ABIs

- The project does not set `RuntimeIdentifiers`, so the SDK default `android-arm64;android-x64` applies to Debug and
  Release. The APK therefore contains `arm64-v8a` (phones) and `x86_64` (emulators, some Chromebooks).
- The ONNX Runtime AAR also ships `armeabi-v7a` and `x86` libraries. They are not packaged, so 32-bit ARM phones are
  not supported. Adding `android-arm` to `RuntimeIdentifiers` would be needed, and has not been tried.
- `libonnxruntime4j_jni.so` is the AAR's Java binding. The .NET binding calls `libonnxruntime.so` directly; the JNI
  library comes along with the AAR.
- Native libraries in the verified Debug APK have 16 KB-aligned `LOAD` segments (§2). The Release package was not
  built, so it was not checked.

## 7. Models and benchmark clips on the device

| What | Location |
|---|---|
| Bundled models | Extracted from `assets/models/` to `<FileSystem.AppDataDirectory>/models-bundled` (app-private storage), each file verified against its SHA-256 |
| Side-loaded models (**Debug builds only**) | `getExternalFilesDir(null)/models`, normally `/sdcard/Android/data/com.bonikpos.voicesearch/files/models/` |
| Side-loaded benchmark clips (all builds) | The `benchmark` folder next to it: `/sdcard/Android/data/com.bonikpos.voicesearch/files/benchmark/` (`clips.json` + 16 kHz WAV files). When it holds a `clips.json`, it takes precedence over the clips packaged in Debug builds. |

Copying files to the device is standard `adb` usage; it was not executed here. Start the app once first, so that
Android creates its external files directory. Then, from a staging folder laid out as in [MODELS.md](MODELS.md) §4.4:

```bash
D=/sdcard/Android/data/com.bonikpos.voicesearch/files
adb shell mkdir -p $D/models/asr $D/benchmark
adb push models/models.json $D/models/
adb push models/asr/whisper-base-ctx400-int8-config.json models/asr/whisper-base-ctx400-int8-encoder.int8.onnx \
         models/asr/whisper-base-ctx400-int8-decoder.int8.onnx models/asr/whisper-base-ctx400-int8-tokens.txt $D/models/asr/
adb push clips.json owner-1.wav other-1.wav $D/benchmark/      # optional: your own consented clips
```

After copying, open **Diagnostics → Reload models**. The *Models* card shows each role's state, execution provider
and checksum message. This directory needs no storage permission, and Android deletes it when the app is uninstalled.
The benchmark clip format (`bonikpos-benchmark-clips/1`) is described in [BENCHMARKS.md](BENCHMARKS.md) §5.

## 8. On-device pipeline benchmark

The benchmark replays clips through the real pipeline: VAD, extraction when a model is installed, owner verification,
recognition and search. It runs on the CPU and then with XNNPACK. It never uses the microphone and stores no audio.
The full procedure, device classes and metric definitions are in [BENCHMARKS.md](BENCHMARKS.md) §1 and §5.

1. Install a build.
   - **Debug** builds carry the synthetic clips and accept side-loaded models, but their C# code is interpreted (§2).
   - **Release** builds give representative numbers, but need bundled models and side-loaded clips (§5, §7).
   - Record which configuration was used; the report does not record it.
2. Install the models to compare.
   - Without a speech-recognition model, the recognition stage is unavailable and its cost is not in the
     end-to-end numbers.
   - The VAD and the speaker encoder always run on the CPU. Without a Whisper or TSE model, both runs therefore use the
     CPU, and the report says so.
3. Charge the phone, **unplug it** (battery current is only meaningful on battery), close other apps and let it cool
   down.
4. Open **Diagnostics** (search screen overflow menu, or *Settings → Model and performance diagnostics*) and go to
   **Full pipeline benchmark**.
5. Leave **Compare CPU and accelerator** on and press **Run benchmark**. It runs 3 repetitions in real time, takes a
   few minutes and keeps the screen on.
6. Press **Copy Markdown** and **Copy JSON**. Store the reports with the device, Android version, build configuration,
   model ids and hashes, and battery state ([EVALUATION_PLAN.md](EVALUATION_PLAN.md) §7).

Android-specific readings:

- Battery level, `BATTERY_PROPERTY_CURRENT_NOW`, voltage and temperature (none of them needs a permission). From
  current and voltage the report derives the whole-device battery power, not the app's own.
- `PowerManager` thermal status. It needs Android 10 or later; on older versions it reads "unavailable".

Microphone input latency is not part of the replay. To see it, start a voice search, speak, and read
`audio.input_latency_ms` (from `AudioRecord.getTimestamp`) in the *Performance* card.

## 9. Play Console: data safety and permissions

This describes what the code does, so that the Data safety form and the store listing can match it. Fill in the form
from the current Play Console help. Under Google's definitions, data that is processed only on the device and never
sent off it does not need to be declared as collected.

| Data / behaviour | What the app does |
|---|---|
| Microphone audio | Processed in memory on the device for voice search, enrollment and the microphone test. It is not uploaded, and the app keeps no recordings. The benchmark replays clips and stores nothing. |
| Owner voice profile | Speaker embeddings, not audio, stored on the device. They are encrypted with AES-256-GCM, and the key is held in Android Keystore-backed secure storage. They are excluded from backup and transfer (§4), and erased by *Settings → Delete all app data*. |
| Search history | Text only, encrypted on the device. It can be turned off in Settings and is deleted with *Delete all app data*. |
| Diagnostics and benchmark reports | Numbers only. They leave the device only when the user copies them to the clipboard. |
| Network | Only the optional BonikPOS product API, when the owner configures it. The app sends HTTPS requests with the API token or key and paging parameters, and receives the product list. No audio, profile, transcripts or history is sent. Whether this counts as collection depends on who operates that server. |
| Third-party SDKs | No analytics, advertising or crash-reporting SDK is referenced (`Directory.Packages.props`). ONNX Runtime telemetry is disabled (§4), but this has not been verified on the wire. |

For the deletion question: *Delete all app data* removes the voice profile, history, API token, catalog cache, an
imported product index and the settings ([PRIVACY.md](PRIVACY.md) §1.7 and §8 list what remains, e.g. bundled models).

`RECORD_AUDIO` is a runtime permission, and the app asks for it only when a voice feature starts. The in-app privacy
text and consent wording are in [PRIVACY.md](PRIVACY.md).

## 10. Troubleshooting

| Symptom | Cause / action |
|---|---|
| A Debug APK installed with plain `adb install` closes at start | It has no managed assemblies (Fast Deployment, §2). Deploy with `-t:Run` / `-t:Install`, or build with `-p:EmbedAssembliesIntoApk=true`. This is expected from the build settings; it was not observed here. |
| Diagnostics shows `CPU` where XNNPACK was expected | Fallback is automatic: when XNNPACK is missing or rejects a model, the session is created on the CPU and "XNNPACK unavailable for `<file>`; using CPU" is logged. VAD and speaker encoders always run on the CPU, by design. *Settings → Processing → CPU only* forces the CPU for comparison. |
| "Could not start listening: The microphone is in use by another app." | Another app holds the microphone: a call, a recorder or an assistant. Close it and try again. |
| "...could not be opened with any supported configuration." | None of 16/48/44.1 kHz × float/16-bit worked for the selected source. Try another *Settings → Microphone input* option (speech-recognition input, unprocessed, default). |
| "Microphone access is needed..." / "Microphone access is off. Enable it in the system settings..." | Grant the permission in Android Settings → Apps → BonikPOS Voice Search → Permissions → Microphone. On a test device you can use `adb shell pm grant com.bonikpos.voicesearch android.permission.RECORD_AUDIO`. |
| A side-loaded model does not appear | Release builds ignore side-loaded models. Otherwise check the message in Diagnostics → *Models*: manifest format, paths relative to the manifest, SHA-256 values, and that no test-fixture entry shadows the role. Then *Reload models* ([MODELS.md](MODELS.md) §2). |
| Speech recognition "not installed" | Expected: no ASR model is bundled ([MODELS.md](MODELS.md)). |
| `audio.frames_dropped` > 0 ("Needs attention") | Frame processing fell behind real time. Check the thermal state and the *Processing* setting, and include the device in a benchmark report. |
| Battery shows "unknown" under Diagnostics → *Device* | That row uses the MAUI `Battery` API, which MAUI documents as requiring `BATTERY_STATS` to be declared on Android. The manifest does not declare it, and the app catches the exception. This was not observed on a device. Benchmark battery readings use `BatteryManager` directly and are not affected. |
| Thermal state "unavailable (Android 10+ required)" | Android 7–9 has no `PowerManager` thermal status. |
| No battery current or power in the report | The device reports `CURRENT_NOW` as unsupported (`Integer.MIN_VALUE` or 0). |
| Need logs | `adb logcat`. Debug builds also log through `Microsoft.Extensions.Logging.Debug`. |

## 11. Still to verify on a real device

- [ ] Debug deployment (`-t:Run`) and a Release `.aab` installed from a Play testing track start correctly.
- [ ] First launch extracts the bundled models: Diagnostics → *Models* shows VAD and the speaker encoder as
      *Ready · CPU*.
- [ ] The microphone permission flow works, including after a permanent denial.
- [ ] `AudioRecord` configuration chosen on the device: the `audio.queue` row shows the device rate, callback size and
      source, such as `VOICE_RECOGNITION/float`.
- [ ] No dropped frames or discontinuities during long listening sessions.
- [ ] Plausible `audio.input_latency_ms`.
- [ ] XNNPACK acceptance per model, with a real Whisper export installed. Check CPU against XNNPACK timings and that
      results are identical or acceptable.
- [ ] Network capture during launch, enrollment and search shows no connection to `mobile.events.data.microsoft.com`
      (procedure in [PRIVACY.md](PRIVACY.md) §5).
- [ ] `OnnxRuntimeTelemetry.NativeFlagSet` is `true` on the device. The `setenv` call through `libc` has been
      verified only on Linux, and the flag is not shown in the UI, so check it with a debugger.
- [ ] Release-only behaviour (trimming, embedded assemblies) matches Debug. This covers source-generated JSON, XAML
      source generation and ONNX Runtime interop.
- [ ] Benchmark reports from the device classes listed in [BENCHMARKS.md](BENCHMARKS.md) §5.
