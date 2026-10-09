# Privacy

How the app handles the owner's voice, other people's speech, recognised text and configuration data, as implemented
in this repository. Statements are derived from the code and point to it; anything not implemented or not verified is
marked as such. This is an engineering description, not legal advice. Paths are relative to the solution root (the folder that contains
`Directory.Build.props`). Architecture: [ARCHITECTURE.md](ARCHITECTURE.md). Backend calls:
[BACKEND_INTEGRATION.md](BACKEND_INTEGRATION.md).

## Summary

| # | Requirement | Status | Main code |
|---|---|---|---|
| 1 | Request microphone permission only when needed | Implemented | `App/Services/MauiPlatformServices.cs` (`MauiPermissionService`), `MicrophoneTestViewModel`, `SearchViewModel.ToggleListeningAsync` |
| 2 | Explain the purpose of voice enrollment | Implemented | `ConsentText` (`Presentation/Services/SessionState.cs`), `Views/WelcomePage.xaml` |
| 3 | Obtain the owner's consent | Implemented (checkbox, recorded in the profile). Re-consent after a wording change: not implemented | `WelcomeViewModel`, `ConsentRecord` (`Core/Profiles/VoiceProfileContracts.cs`) |
| 4 | Process audio locally by default | Implemented. There is no remote-processing option at all | `AI/Runtime/OnnxVoiceModelRuntime.cs`, `Core/Pipeline/VoiceSearchPipeline.cs` |
| 5 | Do not retain raw recordings | Implemented: no code writes audio to storage. No opt-in retention feature exists | `Core/Enrollment/EnrollmentSession.cs`, §1.5 |
| 6 | Protect the stored voice profile | Implemented: AES-256-GCM, key in platform secure storage, excluded from backup | `Infrastructure/Storage/EncryptedVoiceProfileStore.cs`, `Infrastructure/Security/AesGcmDataProtector.cs` |
| 7 | Provide deletion and re-enrollment | Implemented | `VoiceSettingsViewModel`, `Infrastructure/Storage/PrivacyService.cs` |
| 8 | No audio upload without explicit configuration and disclosure | Implemented: no upload code exists. ONNX Runtime's built-in telemetry is disabled; on-device verification is pending (§5) | `AI/Onnx/OnnxRuntimeTelemetry.cs`, `Platforms/Android/AndroidManifest.xml` |
| 9 | Treat recognised medicine names as search candidates, not medical advice | Implemented | `MedicineSearch/Ranking/SearchDecisionPolicy.cs`, search and clarification screens |
| 10 | Do not recommend doses or change prescriptions | Implemented: no such function exists | `Core/Medicine/MedicineContracts.cs`, `MedicineSearch/Text/QueryParser.cs` |

## 1. Requirements

### 1.1 Request microphone permission only when needed

- Android declares `RECORD_AUDIO` (`Platforms/Android/AndroidManifest.xml`, comment: "Requested at runtime, only when
  the owner starts the microphone test or voice search"); iOS declares `NSMicrophoneUsageDescription` (§6).
- The permission is requested only by `MauiPermissionService.RequestMicrophoneAsync` (MAUI
  `Permissions.RequestAsync<Permissions.Microphone>`), which is called from two places:
  - the microphone-test screen (`MicrophoneTestViewModel`, part of enrollment): the "Allow microphone" and
    "Start test" buttons. Opening the screen only checks the status (`CheckMicrophoneAsync`, no prompt);
  - the search screen (`SearchViewModel.ToggleListeningAsync`), when the owner taps the microphone and permission is
    not granted.
- Nothing requests it at app start. The capture classes (`AndroidMicrophoneCapture`, `IosMicrophoneCapture`) never
  request it; they throw `MicrophonePermissionDeniedException` without it. Enrollment is reached through the
  microphone-test screen, whose "Continue to enrollment" button is enabled only after a test has run, which needs the
  permission; a recording without permission fails with an error message.
- If access was denied permanently, the app shows a message and an "Open system settings" button
  (`AppInfo.ShowSettingsUI`). To tell "denied" from "denied permanently" it stores one boolean,
  `permission.microphone.requested`, in MAUI `Preferences`.
- The microphone is open only while a voice search listens, an enrollment sample records (at most 9 s) or the 6-second
  microphone test runs. Voice search stops after one result, after 15 s without speech (default `SingleQuery` mode),
  when the search screen disappears, and when the window is deactivated or stopped (`App.xaml.cs`: "Never keep the
  microphone open in the background"). `EnrollmentRecordingPage` stops a recording when it disappears. The window
  handlers stop only the voice-search pipeline; an enrollment sample or the microphone test ends on its own. The iOS
  `Info.plist` declares no background modes.

### 1.2 Explain the purpose of voice enrollment

| Screen | What it says |
|---|---|
| Welcome (`Views/WelcomePage.xaml`) | "Search the medicine catalog by saying the medicine name. The app prefers your voice (the enrolled shop owner) and ignores speech it cannot attribute to you."; "Why enroll your voice?" followed by the consent text (§1.3); "Results are search suggestions only. This app does not give medical advice, recommend doses or change prescriptions." |
| Microphone test | "Voice search needs the microphone. Audio is processed on this phone; it is not uploaded or stored." |
| Enrollment instructions (`EnrollmentInstructionsViewModel`) | How to record, including "Only you should speak while recording. The profile must contain only the owner's voice." |
| Enrollment result | "The profile is not a password and does not prove who is speaking. It only helps the app prefer your voice. Recordings were discarded." and "Stored encrypted on this phone only." |
| Owner voice settings | "The profile is a speaker embedding (numbers), encrypted on this phone. It is not a password and cannot authorise anything." |
| Settings > "What is stored and why" | The consent text plus "Medicine results are search suggestions only - this app does not give medical advice, recommend doses or change prescriptions." |

### 1.3 Obtain the owner's consent

Consent text version **`2026-10-v1`** (`ConsentText.Version` in
`src/BonikPOS.VoiceSearch.Presentation/Services/SessionState.cs`), shown on the welcome screen:

> BonikPOS Voice Search records a few short samples of your voice to create a voice profile. The profile is a list of
> numbers (a speaker embedding) derived from your voice. It is encrypted and stored only on this phone, and it is used
> only to prefer your voice when you search for medicines. Raw recordings are not kept. Audio is processed on the phone
> and is not uploaded. You can delete the profile at any time in Settings > Owner voice. The voice profile is not a
> password: it does not prove who is speaking and must not be used to authorise anything.

Checkbox: "I am the shop owner and I agree to record my voice for this purpose."

- "Start voice enrollment" stays disabled until the box is ticked (`WelcomeViewModel.StartEnrollmentCommand`,
  `CanExecute = ConsentAccepted`). Starting creates `ConsentRecord(AcceptedUtc, "2026-10-v1")` in `SessionState`.
- `EnrollmentViewModel.FinishAsync` refuses to build a profile without that record. The record is stored inside the
  encrypted profile (`VoiceProfile.Consent`) and shown on the owner voice settings screen ("consent 2026-10-v1 accepted
  <date>").
- Saving is a separate explicit step: Save on the enrollment result screen writes the profile, Discard drops it, and
  cancelling enrollment discards the samples ("Nothing has been saved").
- Not implemented: asking again when `ConsentText.Version` changes (the stored version is displayed, not compared).
  When the wording changes, bump `ConsentText.Version` and update this section.
- The app cannot obtain consent from other people near the counter; §2 describes what happens to their speech.

### 1.4 Process audio locally by default

- Capture (`src/BonikPOS.VoiceSearch.Audio/Platforms/*`), VAD, speaker verification, target-speaker extraction (when
  installed) and speech recognition all run in the app process with ONNX Runtime sessions created from model files on
  the phone (`AI/Onnx/OnnxSessionFactory.cs`).
- The only network code in `src/` is `BonikPosApiCatalogSource` (catalog download, §4). No remote speech-recognition
  option exists, so local processing is not just the default but the only mode.
- Models are bundled in the app package (Debug builds may also read side-loaded models); the app never downloads
  models.

### 1.5 Do not retain raw recordings

- No code path writes audio to storage. (`WavFile.Write` exists in the Audio library but is called only by a unit
  test.)
- Audio exists only in memory:
  - capture frames in the pooled buffers of `AudioFrameQueue`, reused frame after frame;
  - the segmenter's buffers and each `Utterance` (`Audio`, `SpeechOnly`) until verification and recognition finish;
  - the recognizer's log-mel feature buffer, reused for the next utterance;
  - during enrollment, the last sample, kept only so the owner can play it back (`EnrollmentSession.LastRecording`).
    It is cleared (`Array.Clear`) when the next sample is recorded, when the profile is built, and when the session is
    disposed or cancelled; the speech-only copy is cleared right after the embedding is computed;
  - the microphone test's buffer, cleared after analysis (`MicrophoneTester`).
- The pipeline does not zero its buffers after use; they are overwritten or reclaimed by the garbage collector. Only
  enrollment and the microphone test clear theirs explicitly.
- The voice profile holds embeddings and statistics, never audio (`VoiceProfile` remarks).
- No feature retains recordings, so no "explicitly required and consented" retention exists. Audio for evaluation is
  collected outside the app ([EVALUATION_PLAN.md §1](EVALUATION_PLAN.md)).

### 1.6 Protect the stored voice profile

- **Contents** (`VoiceProfile`, `Core/Profiles/VoiceProfileContracts.cs`): the centroid embedding of the installed
  speaker encoder (256 floats for GE2E), encoder id, version and SHA-256, calibration statistics and suggested
  threshold, the consent record, an enrollment summary (accepted and rejected sample counts, seconds of speech, prompt
  languages, app version), timestamps and a random profile id. The code describes it as "biometric-derived data".
- **Encryption** (`AesGcmDataProtector`): AES-256-GCM with a 256-bit key generated on the phone
  (`RandomNumberGenerator`) and stored only in platform secure storage under `bonikpos.profile.datakey.v1`, never next
  to the data. Every write uses a fresh 12-byte nonce. The envelope header and the purpose string
  `bonikpos.voice-profile.v1` are authenticated, so a modified, truncated or swapped file is rejected
  (`tests/BonikPOS.VoiceSearch.UnitTests/Infrastructure/EncryptionTests.cs`).
- **Secure storage**: `MauiSecretStore` uses MAUI `SecureStorage` (Android Keystore-backed, iOS Keychain).
- **Handling**: the plaintext JSON is zeroed (`CryptographicOperations.ZeroMemory`) after encryption and decryption;
  only ciphertext is written, atomically (temp file, then move), to
  `FileSystem.AppDataDirectory/bonikpos/owner-voice-profile.bin`, which is private app storage and excluded from backup
  (§6).
- **Integrity failures**: a profile that cannot be decrypted or parsed raises `VoiceProfileCorruptException` and is
  treated as "no profile" (re-enrollment). A profile made with a different speaker model is never compared; the app
  asks for re-enrollment (`VoiceProfileCompatibility.Check`).
- **Use**: only to prefer the owner's voice. It is not an authentication factor.
- **Limits**: this protects the file at rest. Code running in the app process, or anyone able to read the app's secure
  storage, can decrypt it. No extra user authentication (PIN or biometrics) gates the profile; that is not implemented.

### 1.7 Deletion and re-enrollment

| Action in the UI | Code | Effect |
|---|---|---|
| Settings > Owner voice settings > Delete voice profile | `VoiceSettingsViewModel.DeleteAsync` → `EncryptedVoiceProfileStore.DeleteAsync` | Overwrites the file with zeros (best effort on flash), deletes it and removes its key |
| Settings > Owner voice settings > Re-enroll my voice | `VoiceSettingsViewModel.ReEnrollAsync` → `welcome` | New consent and enrollment; the old profile stays until the new one is saved, then it is replaced |
| History > Clear history | `EncryptedSearchHistoryStore.ClearAsync` | Deletes the history file and its key and clears the ranking hints |
| History or Settings > history switch off | `VoiceSearchSettings.HistoryEnabled` | Stops recording new entries; existing entries stay until cleared |
| Settings > empty the token field > Save settings | `SettingsViewModel.SaveAsync` | Removes the API token from secure storage |
| Settings > Delete all app data (with confirmation) | `PrivacyService.DeleteAllDataAsync` | Profile and key, history and key, API token, `catalog-cache.json`, `{data}/imported-catalog.json`, settings reset to defaults; then opens the welcome screen |

Removing the key makes any other copy of the encrypted file unreadable (crypto-shredding; test
`ARestoredBackupCopy_IsUnreadableAfterDelete`). "Delete all app data" does not remove: extracted models, the model checksum cache, the `permission.microphone.requested` flag, side-loaded
developer files, the operating-system permission grant, and in-memory state (loaded catalog, last results) until the
app restarts.

### 1.8 No audio upload without explicit configuration and disclosure

- No code uploads audio, embeddings, history or recognised text, with or without configuration. The only outbound
  request is the catalog download (§4), which carries no audio.
- Third-party code: ONNX Runtime contains Microsoft usage telemetry, which the app disables (§5).
  `Directory.Packages.props` references no analytics, crash-reporting or advertising SDK.

### 1.9 Recognised medicine names are search candidates, not medical advice

- Nothing is selected automatically. Even a `Confident` result only appears in the list; the owner taps Select (search
  screen) or Choose (ambiguity screen). `Ambiguous` results ask the owner to choose. `NoMatch` substitutes nothing;
  "did you mean" names only start a new search.
- A product with a different strength or form is never presented as the requested product; low recognition confidence
  and a speaker who is not confirmed as the owner lower the decision ([ARCHITECTURE.md §6.4](ARCHITECTURE.md#64-search-decision-policy)).
- Results show the catalog's own values and product id; missing values are shown as "not in catalog".
- On-screen notices: search screen "Results are catalog search suggestions, not medical advice. Always check the product
  before selling."; no-match screen "No product was substituted. Nothing has been selected."; ambiguity screen
  "Nothing is selected until you choose one."; plus the welcome and settings texts in §1.2.
- The demo catalog is labelled "DEMO catalog (N sample products) - not real stock data"; its file states it must not be
  used for dispensing.

### 1.10 No dose recommendations or prescription changes

No such function exists. `MedicineProduct` has no dose or prescription fields. Quantity words ("two strips",
"দুই পাতা") are recognised by `QueryParser` only so they can be ignored. Selecting a product records it in local
history and shows its id; nothing is sent to BonikPOS, whose adapter only issues GET requests.

## 2. Other people's speech

While the app listens, the microphone hears everyone near the phone. Every utterance passes through the VAD and the
speaker check in memory. What happens next for an utterance attributed to someone else (`OwnerVoiceDecision.NotOwner`)
depends on two switches in Settings > Owner voice settings:

| "Ignore speech that does not match the owner's voice" (`RequireOwnerMatch`) | "Faster results: transcribe while the voice is checked" (`ParallelVerification`) | Non-owner utterance |
|---|---|---|
| On (default) | Off (default) | Never transcribed. Ignored; the screen says "Ignored speech that did not match the owner's voice." |
| On | On (opt-in) | Transcription starts before the check finishes. When the check says "not the owner", the transcription is cancelled, including an ONNX Runtime run in progress (`RunOptions.Terminate`), and whatever was produced is discarded: never searched, shown or saved. Another person's words are therefore partly transcribed in memory. |
| Off | either | Transcribed, searched and shown, capped at "please confirm"; saved in history if the owner selects a product or taps "None of these" while history is on. |

The opt-in switch carries this description on screen: "Off by default. When on, transcription starts before the voice
check finishes, so another person's words are briefly transcribed on this phone and then discarded (never searched,
shown or saved). When off, other voices are never transcribed."

Regardless of the switches: an `Uncertain` utterance (borderline similarity, or a second voice detected inside the
owner's utterance) is transcribed and searched, never shown as a confident match, and flagged on screen; an utterance
with too little speech to check is ignored. A target-speaker extraction model, if installed, would process all incoming
audio in memory to suppress other voices; none is bundled. Speaker verification can be wrong in both directions; its
thresholds are provisional ([EVALUATION_PLAN.md §2](EVALUATION_PLAN.md)).

## 3. Data inventory

`{data}` = `FileSystem.AppDataDirectory/bonikpos`; `{cache}` = `FileSystem.CacheDirectory/bonikpos` (`MauiAppPaths.Create`,
`App/Services/MauiPlatformServices.cs`).

| Data | Location | Protection | Retention | Removed by |
|---|---|---|---|---|
| Voice profile (§1.6) | `{data}/owner-voice-profile.bin` | AES-256-GCM, key `bonikpos.profile.datakey.v1` | Until deleted or replaced | Delete voice profile; Delete all app data |
| Search history: time, query text (recognised, typed or corrected), source, decision, outcome (selected, selected after correction, dismissed), selected product id and label, recognition confidence, speaker decision. No audio. | `{data}/search-history.bin` | AES-256-GCM, key `bonikpos.history.datakey.v1` | Newest 500 entries (`MaxEntries`). Written only when the owner selects a product or taps "None of these", and history is on (default on) | Clear history; Delete all app data |
| Settings: catalog source, API address and field names, local catalog path, language, audio, thresholds, owner-voice options | `{data}/settings.json` | Plain JSON, no secrets | Until changed | Delete all app data (reset to defaults) |
| BonikPOS API token or key | Secure storage `bonikpos.api.token` | MAUI `SecureStorage` | Until cleared | Empty token + Save; Delete all app data |
| Catalog cache: products of the last successful API sync, time, source description (includes the API host name) | `{cache}/catalog-cache.json` | Plain JSON | Replaced at each successful sync | Delete all app data |
| Imported product index | `{data}/imported-catalog.json` | Plain copy of the picked file | Until another import | Removed by "Delete all app data" |
| Model checksum cache (paths, sizes, hashes) | `{cache}/model-checksums.json` | Plain JSON | – | Not removed |
| Extracted bundled models | `FileSystem.AppDataDirectory/models-bundled/` | SHA-256 verified | – | Not removed |
| "Permission already requested" flag | MAUI `Preferences`, `permission.microphone.requested` | – | – | Not removed |

Held in memory only and never written: audio (§1.5); performance telemetry (numbers only, the last 512 values per
metric, until the app exits or Diagnostics > "Reset numbers"); the current recognised text, verification result and
results (`SearchViewModel`, `SessionState`); the enrollment draft until Save; ranking hints derived from history.

## 4. What leaves the device

| Flow | When | Content | Protection |
|---|---|---|---|
| Catalog sync | Only when the catalog source is "BonikPOS API" and an address is configured: when the search screen first prepares and no catalog is loaded, on "Save settings" and on "Sync catalog now" | `GET {BaseUrl}/{ProductsPath}?page=N&pageSize=M` (parameter names configurable) with `Accept: application/json` and, if a token is stored, `Authorization: Bearer <token>` or the configured API-key header. The app adds nothing else; the platform HTTP stack's default headers were not inspected. | HTTPS required (plain HTTP accepted only for loopback addresses, for development); platform TLS validation; no certificate pinning |
| Clipboard | Only when the owner taps a copy button on the Diagnostics screen | Diagnostics or benchmark report: numbers, model ids, device model, OS and app version, the catalog source description (includes the API host name). No audio, recognised text or token. | User action |

Nothing else in the app's code sends data. Protocol details: [BACKEND_INTEGRATION.md](BACKEND_INTEGRATION.md).
ONNX Runtime telemetry: §5. Operating-system backup: §6.

## 5. ONNX Runtime telemetry

**Finding.** The native ONNX Runtime 1.31.0 binaries for Android, iOS and Linux contain Microsoft's 1DS usage-telemetry
client; the collector host is `mobile.events.data.microsoft.com` (the comment in `OnnxRuntimeTelemetry.cs` cites
`onnxruntime/core/platform/telemetry_1ds.cc`). Without an opt-out, the runtime embedded in the app could send usage
events from the phone, which contradicts "no data leaves the phone without explicit configuration".

**Mitigation in the code:**

| Layer | Where | What it does |
|---|---|---|
| Native environment variable | `OnnxRuntimeTelemetry.Disable()`, `src/BonikPOS.VoiceSearch.AI/Onnx/OnnxRuntimeTelemetry.cs` | Sets `ORT_DISABLE_TELEMETRY=1` in the .NET environment and also through libc `setenv`, because on Unix `Environment.SetEnvironmentVariable` does not change the native environment that ONNX Runtime reads. ONNX Runtime reads the variable when its telemetry provider starts and keeps the opt-out for the whole process. `NativeFlagSet` reports whether `setenv` succeeded. |
| API opt-out | same method | Calls `OrtEnv.Instance().DisableTelemetryEvents()` after the variable is set. |
| Called first | First statement of `MauiProgram.CreateMauiApp`; static constructor of `OnnxSessionFactory` | Runs before any ONNX Runtime environment or session is created by the app, and by tests and tools that use the factory. The method is idempotent and locked. |
| Android manifest | `src/BonikPOS.VoiceSearch.App/Platforms/Android/AndroidManifest.xml` | Removes the AAR's `ai.onnxruntime.TelemetryInitializer` content provider (`tools:node="remove"`), which would start telemetry at app launch. Android initialises content providers before `Application.OnCreate`, which is where MAUI calls `CreateMauiApp`, so the managed opt-out alone would come too late for this provider. |
| Tooling | `tools/models/*.py`, `tools/models/tse/*.py`; `asr-bench` and `tse-bench` in `tools/BonikPOS.VoiceSearch.Evaluation` | The Python scripts set `ORT_DISABLE_TELEMETRY=1` unless it is already set; the two benchmark commands call `Disable()` before creating sessions. |

**Verification status:**

| Check | Result |
|---|---|
| Integration test `OnnxRuntimeTelemetryTests.Disable_IsIdempotent_AndSetsTheOptOutForTheNativeRuntime` (`tests/BonikPOS.VoiceSearch.IntegrationTests/Models/BundledModelTests.cs`) | Asserts that the variable is `1` in .NET and, on Linux, in the native environment (`getenv`). |
| Development container (Linux) | With the opt-out, the ONNX Runtime telemetry event store did not grow across two runs; a control run without it added one event. Before the opt-out existed, 63 events had been queued in this container's store during development. |
| Android device | **Not verified yet.** |
| iOS device | **Not verified yet.** Also unverified: whether the `libc` `setenv` import resolves on iOS and Android. If it does not, `NativeFlagSet` is `false` and only `DisableTelemetryEvents()` (plus, on Android, the manifest change) remains. `NativeFlagSet` is not shown on the Diagnostics screen. |

Test code that creates `InferenceSession` directly
(`tests/BonikPOS.VoiceSearch.IntegrationTests/ReferenceParity/TsePlumbingFixtureTests.cs`) does not call `Disable()`
itself and depends on another test having done so first in the same process.

**On-device verification procedure** (to be run for Android and iOS before release):

1. Build the Release configuration with the models that will ship.
2. Android, static check: inspect the merged manifest of the built APK or AAB (for example Android Studio's APK
   Analyzer, or `apkanalyzer manifest print app.apk`). `ai.onnxruntime.TelemetryInitializer` must be absent.
3. Put the test device on a network you control and record its DNS queries and TLS connections (for example a laptop
   hotspot with Wireshark or tcpdump, a logging DNS resolver, PCAPdroid on Android for per-app capture, or an `rvictl`
   remote virtual interface on macOS for an iPhone). The host name is visible in DNS and in the TLS SNI, so traffic does
   not need to be decrypted.
4. Install fresh, launch, let the search screen load and warm up the models, run the microphone test, an enrollment,
   several voice searches (requires a speech-recognition model), the Diagnostics self-test and the pipeline benchmark.
   Send the app to the background and back, force-stop it and launch it again (queued events could be sent at a later
   start), and keep it running for a while.
5. Pass criterion: no lookup of, or connection to, `mobile.events.data.microsoft.com` (or any other
   `*.events.data.microsoft.com` host) from the app. The only expected app traffic is the configured BonikPOS API host,
   if the API catalog is used.
6. Control: repeat with a build that has the opt-out removed (manifest change reverted, `Disable()` not called) to show
   that the setup can observe ONNX Runtime telemetry on that platform. If the control shows no such traffic either, the
   result is inconclusive.
7. Record device, OS version, app build, ONNX Runtime version, date, capture method and result.

## 6. Platform declarations and backup

**Android** (`src/BonikPOS.VoiceSearch.App/Platforms/Android/`):

- Permissions in `AndroidManifest.xml`: `RECORD_AUDIO` (§1.1); `INTERNET` and `ACCESS_NETWORK_STATE` (comment: "Only
  used for the optional BonikPOS product API; audio is never uploaded"; no app code queries the network state);
  `uses-feature android.hardware.microphone` with `required="true"`.
- `android:allowBackup="false"` and `android:dataExtractionRules="@xml/data_extraction_rules"`.
  `Resources/xml/data_extraction_rules.xml` excludes the `root`, `file`, `database`, `sharedpref` and `external`
  domains from both `cloud-backup` and `device-transfer` (Android 12+).
- The ONNX Runtime telemetry provider is removed (§5).
- The merged manifest of a Debug build was checked ([DEPLOYMENT_ANDROID.md](DEPLOYMENT_ANDROID.md) §4): the three
  permissions above plus the signature-level `com.bonikpos.voicesearch.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION`
  contributed by a library, and no `ai.onnxruntime.TelemetryInitializer`. The ONNX Runtime AAR itself also declares
  `INTERNET` and `ACCESS_NETWORK_STATE`. A Release build's merged manifest has not been checked.

**iOS** (`src/BonikPOS.VoiceSearch.App/Platforms/iOS/`):

- `Info.plist` contains one usage description, `NSMicrophoneUsageDescription`: "BonikPOS Voice Search uses the
  microphone to hear the medicine name you say and, during voice enrollment, to create your voice profile. Audio is
  processed on this device; it is not uploaded or stored." It declares no background modes and no App Transport
  Security exceptions.
- Backup: `MauiAppPaths.Create` sets `NSUrlIsExcludedFromBackupKey` on `{data}` (profile, history, settings) and on
  the extracted-models directory. The app does not mark the cache directory. The imported product index is stored in
  `{data}`.
- `Resources/PrivacyInfo.xcprivacy`: `NSPrivacyTracking` false, `NSPrivacyCollectedDataTypes` empty, required-reason
  APIs for file timestamps (`C617.1`), system boot time (`35F9.1`) and disk space (`E174.1`) from the MAUI template,
  and user defaults (`CA92.1`) for the permission flag. The ONNX Runtime 1.31.0 package contains no privacy manifest
  for its statically linked xcframework ([DEPLOYMENT_IOS.md](DEPLOYMENT_IOS.md) §3); whether its code uses APIs that
  must be declared has not been checked.
- Debug builds only: `DebugFileSharing.plist` (merged by the App csproj for Debug iOS builds) sets
  `UIFileSharingEnabled` and `LSSupportsOpeningDocumentsInPlace`, which exposes the app's Documents folder (side-loaded
  `models/` and `benchmark/`) in the Files app. Profile, history and settings are under `FileSystem.AppDataDirectory`,
  not in Documents.

## 7. Diagnostics, benchmark and logs

- **Diagnostics screen** (`DiagnosticsViewModel`): the page states "Numbers only: this screen never shows or stores
  audio or recognised text". The self-test uses synthetic signals, and the copied report ends with "(This report
  contains no audio and no recognised text.)".
- **Pipeline benchmark** (`src/BonikPOS.VoiceSearch.Audio/Benchmark/PipelineBenchmark.cs`): replays clips from memory
  through `FileAudioCapture` and never opens the microphone. Clips come from the side-load `benchmark` folder when a
  developer has put a `clips.json` there (any build), otherwise from synthetic TTS clips that only Debug builds bundle;
  Release builds contain no audio (`MauiBenchmarkClipSource`). The owner reference for the run is built in memory from
  the clips marked `enroll` and discarded. The report contains timings, outcome codes (such as `result:<Decision>`,
  `ignored:<Decision>`, `nothing-recognized`, `error:<message>`), model ids, resource readings and the device
  description; no audio and no recognised text. It is not written to storage; it is copied to the clipboard only on
  request.
- **Telemetry**: in memory only (§3).
- **Logs**: `MauiProgram` adds the Debug logger in Debug builds; the app's code configures no other logging provider.
  Log calls in `src/` record exceptions, model roles and ids, catalog warnings (which can contain product ids), and
  capture and device status. None logs audio, recognised text, embeddings or the API token. ONNX Runtime's own log level
  is set to `ERROR` (`OnnxSessionFactory`).

## 8. Known gaps and open items

1. *(Fixed.)* The imported product index used to be copied to `FileSystem.AppDataDirectory/imported-catalog.json`,
   outside the folder that "Delete all app data" clears and (on iOS) outside the backup-excluded folder. It is now
   copied to `{data}/imported-catalog.json`, which is both backup-excluded and deleted.
2. The catalog cache and the imported product index are stored unencrypted (product data, not personal data).
3. Pipeline audio buffers are not zeroed after use (§1.5).
4. Re-consent after a change of the consent wording is not implemented (§1.3).
5. With "Ignore speech that does not match the owner's voice" switched off, other people's words are transcribed,
   shown and can end up in history (§2); the switch's on-screen description does not mention history.
6. ONNX Runtime telemetry opt-out: on-device verification pending; `NativeFlagSet` unverified on devices and not
   displayed (§5).
7. Not checked: whether secure-storage entries (API token, data keys) survive uninstalling and reinstalling the app on
   iOS. "Delete all app data" before uninstalling removes them.
8. Not checked: the merged manifest of an Android Release build, and whether ONNX Runtime's iOS code uses APIs that
   need a privacy-manifest declaration (§6).
9. "Delete all app data" leaves non-personal files (models, checksum cache, permission flag) and the in-memory state
   until the app restarts (§1.7).
