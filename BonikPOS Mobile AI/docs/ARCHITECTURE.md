# Architecture

BonikPOS Owner-Voice Medicine Search is a .NET 10 / .NET MAUI 10.0.110 app for Android and iOS. The shop owner says a
medicine name; the app checks that the voice belongs to the enrolled owner, transcribes it on the phone with ONNX
Runtime 1.31.0 and ranks products from the shop's catalog. Nothing is selected until the owner taps a product.

This document describes the code in this repository. Paths are relative to the solution root (the folder that
contains `Directory.Build.props`). Related documents: [PRIVACY.md](PRIVACY.md),
[BACKEND_INTEGRATION.md](BACKEND_INTEGRATION.md), [MODELS.md](MODELS.md), [SEARCH_EVALUATION.md](SEARCH_EVALUATION.md),
[EVALUATION_PLAN.md](EVALUATION_PLAN.md), [BENCHMARKS.md](BENCHMARKS.md), [TEST_RESULTS.md](TEST_RESULTS.md),
[DEPLOYMENT_ANDROID.md](DEPLOYMENT_ANDROID.md), [DEPLOYMENT_IOS.md](DEPLOYMENT_IOS.md).

## 1. Solution layout

| Project | Target frameworks | References | Responsibility |
|---|---|---|---|
| `src/BonikPOS.VoiceSearch.Core` | `net10.0` | only `Microsoft.Extensions.Logging.Abstractions` | Domain records and service contracts (audio, VAD, speakers, extraction, recognition, medicine, models, settings, history); the pipeline (`Pipeline/VoiceSearchPipeline.cs`, `Pipeline/UtteranceSegmenter.cs`); enrollment (`Enrollment/`); owner profiles (`Profiles/`); `Settings/SettingsService.cs`; `Telemetry/PerformanceTelemetry.cs`; benchmark contracts (`Benchmark/`). |
| `src/BonikPOS.VoiceSearch.Audio` | `net10.0`, `net10.0-android`, plus `net10.0-ios` when not building on Linux | Core | `AudioFrameQueue`, `CaptureProcessor`, `PolyphaseResampler`, `WavFile`, `FileAudioCapture` (replays audio through the same conversion path; used by tests, tools and the in-app benchmark, never registered as the app's microphone), native capture and playback in `Platforms/Android` and `Platforms/iOS` (each compiled only for its TFM), `Benchmark/PipelineBenchmark.cs`. |
| `src/BonikPOS.VoiceSearch.AI` | `net10.0` | Core, `Microsoft.ML.OnnxRuntime` | Feature front-ends (`Dsp/`), ONNX session factory and telemetry opt-out (`Onnx/`), Silero VAD (`Vad/`), speaker encoders and `OwnerVoiceVerifier` (`Speakers/`), Whisper (`Recognition/`), target-speaker extraction (`Extraction/`), `Runtime/OnnxVoiceModelRuntime.cs`. |
| `src/BonikPOS.VoiceSearch.MedicineSearch` | `net10.0` | Core | Catalog sources and the embedded demo catalog (`Catalog/`), `Index/MedicineIndex.cs`, `Ranking/MedicineMatcher.cs`, `Ranking/SearchDecisionPolicy.cs`, text processing (`Text/`), `MedicineSearchService.cs`. |
| `src/BonikPOS.VoiceSearch.Infrastructure` | `net10.0` | Core, MedicineSearch | BonikPOS API adapter, `CatalogManager` and catalog cache (`Catalog/`), `FileSystemModelProvider` and `BundledModelExtractor` (`Models/`), `AesGcmDataProtector` (`Security/`), encrypted profile and history stores, `JsonSettingsStore`, `PrivacyService`, `AppPaths` (`Storage/`). |
| `src/BonikPOS.VoiceSearch.Presentation` | `net10.0` | Core, MedicineSearch, CommunityToolkit.Mvvm 8.4.2 | View models (`ViewModels/`) and UI-service interfaces (`Services/`). No MAUI dependency, so the view models are unit-tested. |
| `src/BonikPOS.VoiceSearch.App` | `net10.0-android`, plus `net10.0-ios` when not building on Linux | all six projects above, MAUI 10.0.110 | Composition root (`MauiProgram.cs`), XAML pages (`Views/`), MAUI implementations of the UI and platform services (`Services/`), platform heads (`Platforms/`). Minimum Android 7.0 (API 24) and iOS 16. |

| Tests | Notes |
|---|---|
| `tests/BonikPOS.VoiceSearch.UnitTests` | xunit.v3 on Microsoft.Testing.Platform (`global.json`). References every `src` project except App. Small ONNX graphs are generated in code (`TestSupport/OnnxGraphBuilder.cs`) and audio is synthesised, so no model files are needed. |
| `tests/BonikPOS.VoiceSearch.IntegrationTests` | Uses the real models in `models/` and the reference data in `tests/fixtures/` (feature and model parity with the Python reference implementations, bundled-model checks, enrollment and the end-to-end pipeline on synthetic TTS clips with a stub recognizer, a random-weight Whisper fixture). Test-fixture models load only with `allowTestFixtures: true`. |
| `tests/fixtures/` | Synthetic TTS clips (`audio/`, labelled "plumbing tests only"), reference tensors (`reference/`), TSE and Whisper fixture models. |

| Tools (not shipped) | Notes |
|---|---|
| `tools/BonikPOS.VoiceSearch.Evaluation` | Console harness (`Program.cs`): `search`, `make-catalog`, `make-cases`, `tse-bench`, `asr-bench`, `pipeline-bench`. Inputs in `eval/search/`, reports in `eval/results/`; `eval/data/` is generated and git-ignored. |
| `tools/models/` | Python: `export_ge2e_onnx.py`, `export_whisper_onnx.py` (+ `selftest_whisper_hf_conversion.py`), `make_reference_fixtures.py`, `make_test_audio.py`, `register_model.py` (writes manifest entries with SHA-256), `tse/` (pGTCRN architecture, ONNX export and training recipe; no trained weights exist). |
| `tools/ios-compile-check/` | `ios-compile-check.sh` with `IosAudioCheck.csproj` and `IosAppServicesCheck.csproj`: compile-only checks of the iOS code against the Microsoft.iOS reference assembly and MAUI iOS libraries on Linux/Windows. Not a device build. `tools/IosCompileCheck/IosCompileCheck.csproj` is a separate compile-only project for the Audio iOS sources. |

Build settings: `Directory.Build.props` (latest C# version, nullable, implicit usings, warnings as errors,
deterministic builds, version 0.1.0; it deliberately does not import the parent repository's props) and
`Directory.Packages.props` (central package versions). `global.json` pins SDK 10.0.100 (`rollForward: latestFeature`).

### 1.1 Dependency direction

```
App --+--> Presentation ----> MedicineSearch --> Core
      +--> Infrastructure --> MedicineSearch, Core
      +--> AI --------------> Core
      +--> Audio -----------> Core
      +--> MedicineSearch, Core
```

Core has no platform, UI or ONNX dependency. Platform code exists only in `src/BonikPOS.VoiceSearch.Audio/Platforms/*`
and in the App project.

### 1.2 Composition root

`src/BonikPOS.VoiceSearch.App/MauiProgram.cs` first calls `OnnxRuntimeTelemetry.Disable()` (see
[PRIVACY.md §5](PRIVACY.md#5-onnx-runtime-telemetry)), then registers everything in the MAUI DI container. All services
are singletons:

| Contract | Implementation |
|---|---|
| `IAppPaths` | `MauiAppPaths.Create()` (`App/Services/MauiPlatformServices.cs`) |
| `ISecretStore` | `MauiSecretStore` (MAUI `SecureStorage`) |
| `ISettingsStore` | `JsonSettingsStore`, wrapped by `SettingsService` (caches the settings, raises `Changed`) |
| `IPerformanceTelemetry` | `PerformanceTelemetry` |
| `IMicrophoneCapture`, `IAudioOutput` | `AndroidMicrophoneCapture`, `AndroidAudioOutput` / `IosMicrophoneCapture`, `IosAudioOutput` (`#if ANDROID` / `#elif IOS`) |
| `IModelProvider` | `BootstrappingModelProvider` (extracts bundled models, then wraps `FileSystemModelProvider`) |
| `IVoiceModelRuntime` | `OnnxVoiceModelRuntime` |
| `IVoiceSearchPipeline` | `VoiceSearchPipeline` |
| `IVoiceProfileStore` | `EncryptedVoiceProfileStore`, used by `OwnerProfileService` |
| `ISearchHistoryStore`, `IMedicineSearchHints` | one `EncryptedSearchHistoryStore` instance |
| `IMedicineSearchService` | `MedicineSearchService` |
| `ICatalogCache`, `ICatalogManager` | `FileCatalogCache`, `CatalogManager` (shared `HttpClient` with `Timeout = InfiniteTimeSpan`; the adapter applies per-request timeouts) |
| `IPrivacyService` | `PrivacyService` |
| `IResourceSampler`, `IBenchmarkClipSource`, `IPipelineBenchmark` | `MauiResourceSampler`, `MauiBenchmarkClipSource`, `PipelineBenchmark` |
| UI services | `ShellNavigationService`, `MauiDialogService`, `MauiPermissionService`, `MauiUiDispatcher`, `MauiClipboardService`, `MauiFilePickerService`, `MauiAppInfoService`, `MauiDeviceStatusService` |

`SearchViewModel` and `SearchPage` are singletons, so the search screen keeps its state while the clarification screens
are open; every other page and view model is transient. Debug builds add the Debug logger.

App project notes (`BonikPOS.VoiceSearch.App.csproj`): every `*.onnx`, `*.json` and `*.txt` under `models/` is packaged
as a MAUI asset under `models/`; Debug builds also package the synthetic benchmark clips under `benchmark/`; ONNX
Runtime is referenced with `ExcludeAssets="native"` for Android and iOS so that only the AAR / xcframework binaries are
packaged (the comment explains that the desktop `libonnxruntime.so` would otherwise shadow the Android library); XAML is
source-generated (`MauiXamlInflator=SourceGen`) with compiled bindings.

## 2. Presentation (MVVM)

- View models in `src/BonikPOS.VoiceSearch.Presentation/ViewModels/` derive from `ViewModelBase` (CommunityToolkit
  `ObservableObject` with `IsBusy`, `ErrorMessage` and `RunAsync`, which turns exceptions into `ErrorMessage` and
  ignores `OperationCanceledException`). Properties and commands are generated by the CommunityToolkit.Mvvm source
  generators (`[ObservableProperty]` on partial properties, `[RelayCommand]`, `[NotifyPropertyChangedFor]`,
  `[NotifyCanExecuteChangedFor]`).
- Pages in `src/BonikPOS.VoiceSearch.App/Views/` get their view model through constructor injection; most run its
  `Appearing` (Diagnostics: `Refresh`) command from `OnAppearing`.
- Platform features used by view models are interfaces in `Presentation/Services/UiServices.cs` (`INavigationService`,
  `IDialogService`, `IPermissionService`, `IUiDispatcher`, `IClipboardService`, `IFilePickerService`, `IAppInfoService`,
  `IDeviceStatusService`), implemented in `App/Services/MauiPlatformServices.cs`. Navigation is MAUI Shell
  (`ShellNavigationService` calls `Shell.Current.GoToAsync`); dialogs use `Shell.Current.DisplayAlertAsync`.
- `SessionState` (`Presentation/Services/SessionState.cs`) carries state across screens: the consent record, the
  enrollment draft, and the pending result for the clarification screens. User-facing wording lives in `UserMessages`
  and `ConsentText`.

The shell (`AppShell.xaml`, `AppShell.xaml.cs`) has a tab bar with three tabs and registers nine more routes. Route
names are constants in `Routes` (`Presentation/Services/UiServices.cs`):

| Route | Page (`App/Views/`) | View model | Purpose |
|---|---|---|---|
| `//search` (tab) | `SearchPage` | `SearchViewModel` | Microphone, listening indicator, recognised text (editable), ranked results, Select / "None of these" |
| `//history` (tab) | `HistoryPage` | `HistoryViewModel` | Encrypted search history; history on/off; clear |
| `//settings` (tab) | `SettingsPage` | `SettingsViewModel` | Catalog source and API, language, audio input, accelerator, thresholds, privacy, delete all data |
| `welcome` | `WelcomePage` | `WelcomeViewModel` | Purpose of enrollment and the consent checkbox |
| `microphone-test` | `MicrophoneTestPage` | `MicrophoneTestViewModel` | Microphone permission and a 6-second level/SNR test |
| `enrollment-instructions` | `EnrollmentInstructionsPage` | `EnrollmentInstructionsViewModel` | How to record |
| `enrollment-recording` | `EnrollmentRecordingPage` | `EnrollmentViewModel` | Records samples with quality feedback |
| `enrollment-result` | `EnrollmentResultPage` | `EnrollmentResultViewModel` | Save or discard the new profile |
| `ambiguity` | `AmbiguityPage` | `AmbiguityViewModel` | "Which one did you mean?" |
| `no-match` | `NoMatchPage` | `NoMatchViewModel` | What was understood, "did you mean" names, edit / try again |
| `voice-settings` | `VoiceSettingsPage` | `VoiceSettingsViewModel` | Owner check, threshold, extraction, parallel verification, re-enroll, delete profile |
| `diagnostics` | `DiagnosticsPage` | `DiagnosticsViewModel` | Model status, telemetry, self-test, pipeline benchmark |

Flows:

- **First start.** The search tab prepares: `IVoiceModelRuntime.InitializeAsync`, optional `WarmUpAsync`, and
  `ICatalogManager.ReloadAsync` if no catalog is loaded. Without an enrolled profile it navigates to `welcome`.
- **Enrollment.** `welcome` (consent) → `microphone-test` → `enrollment-instructions` → `enrollment-recording` →
  `enrollment-result` (Save writes the encrypted profile; Discard drops it).
- **Search.** Voice search is enabled only when the VAD model, a speech-recognition model and a compatible profile are
  available (`SearchViewModel.RefreshVoiceAvailabilityAsync`); otherwise the screen offers typed search and says why.
  `Ambiguous` and `NoMatch` results open `ambiguity` / `no-match`; other results stay on the search screen.
- **Lifecycle.** `App.CreateWindow` stops the pipeline on `Window.Deactivated` and `Window.Stopped`. `SearchPage` stops
  listening and `EnrollmentRecordingPage` stops recording in `OnDisappearing`.

## 3. Voice pipeline

`src/BonikPOS.VoiceSearch.Core/Pipeline/VoiceSearchPipeline.cs` implements `IVoiceSearchPipeline`.

### 3.1 Threads and data flow

```
 Android: AudioRecord.read in 10 ms blocks on the          iOS: AVAudioEngine input-node tap
 "BonikPOS.AudioCapture" thread (URGENT_AUDIO priority)         (buffer size chosen by iOS)
                         \                                      /
          CaptureProcessor, still on the native thread: PCM16 -> float, channel 0,
          polyphase resampling to 16 kHz when needed, cut into 20 ms frames (320 samples)
                                         |
                                         v
          AudioFrameQueue: bounded (150 frames = 3 s by default), drop-oldest,
          pooled buffers, Discontinuity flag on the first frame after a drop
                                         |
 capture worker (Task.Run -> CaptureLoopAsync), for every 20 ms frame:
   ITargetSpeakerExtractor.Process     (copies the frame unchanged when no TSE model is active)
   UtteranceSegmenter.Push             (Silero VAD on 512-sample / 32 ms chunks, end-pointing)
   AudioLevelEvent every 3rd frame; state Listening <-> HearingSpeech
                                         |   Utterance { Audio = pre-roll + speech + end silence,
                                         |               SpeechOnly = VAD-positive chunks, timestamps }
                                         v
          Channel<Utterance>: capacity 2, drop-oldest (counted as pipeline.recognition_queue_drops)
                                         |
 recognition worker (Task.Run -> RecognitionLoopAsync), for every utterance:
   1. owner verification on SpeechOnly  -> ignored          -> UtteranceIgnoredEvent
   2. speech recognition on Audio       -> empty text       -> NothingRecognizedEvent
   3. medicine search (text + alternative readings)         -> SearchCompletedEvent (+ PipelineTimings)
                                         |
                                         v
 EventRaised (raised on the worker thread) -> SearchViewModel -> IUiDispatcher.Post -> UI thread
```

The native thread or callback only converts, resamples and copies (`IMicrophoneCapture` contract in
`Core/Audio/AudioContracts.cs`). No inference runs on it or on the UI thread. When a consumer falls behind, the oldest
data is dropped and counted, so latency stays bounded.

### 3.2 Capture

| | Android: `Audio/Platforms/Android/AndroidMicrophoneCapture.cs` | iOS: `Audio/Platforms/iOS/IosMicrophoneCapture.cs` |
|---|---|---|
| API | `AudioRecord` with blocking 10 ms reads on a dedicated background thread at `ThreadPriority.UrgentAudio` | `AVAudioEngine` input-node tap (requested 20 ms; iOS picks the real size) |
| Format | Tries 16 000, 48 000, 44 100 Hz; PCM float first, then PCM16; device buffer at least 200 ms | Hardware rate (typically 48 kHz), channel 0 |
| Input path (`AudioInputPreference`) | `VOICE_RECOGNITION` (default), `UNPROCESSED` if the device supports it, `MIC` | `AVAudioSessionMode.Measurement` (default) or `Default`; category `PlayAndRecord`, preferred IO buffer 10 ms |
| Permission | Throws `MicrophonePermissionDeniedException` if `RECORD_AUDIO` is not granted (it never requests it) | Same, based on the record permission |
| Interruptions | A read error sets state `Interrupted` and completes the queue | Interruption and route-change notifications restart the engine; a failed restart sets `Faulted` |
| Input latency estimate | `AudioRecord.getTimestamp` every 10 reads | Buffer host time plus `AVAudioSession.InputLatency` |

`AudioFrameQueue` (`Audio/AudioFrameQueue.cs`) is a bounded single-producer/single-consumer channel with
`BoundedChannelFullMode.DropOldest`. `TryWrite` never blocks. Frame buffers come from a pool of capacity + 2
preallocated arrays. Every drop is counted, and the next frame read carries `AudioFrameInfo.Discontinuity` and the
number of dropped frames; the pipeline then resets the extractor and the segmenter, including the VAD state.
`CaptureProcessor` (`Audio/CaptureProcessor.cs`) uses preallocated buffers and `PolyphaseResampler` (Kaiser-windowed
sinc FIR, state carried across calls).

### 3.3 Capture loop and end-pointing

`UtteranceSegmenter` (`Core/Pipeline/UtteranceSegmenter.cs`) runs the VAD on fixed chunks (Silero: 512 samples = 32 ms)
and cuts utterances with hysteresis: two consecutive chunks at or above the start threshold open an utterance (with
pre-roll); it ends after the configured end silence below the end threshold; it is truncated at the maximum length;
utterances with less speech than the minimum are discarded. `Audio` (pre-roll + speech + trailing silence) goes to
speech recognition and `SpeechOnly` (chunks at or above the end threshold) goes to speaker verification. When capture
stops while the owner is still speaking, `Flush()` emits the utterance in progress (tap-to-stop).

In `SingleQuery` mode the capture loop raises `ListeningTimedOutEvent` and stops after `MaxListenMilliseconds` without
speech.

### 3.4 Recognition loop: verification, recognition, search

For each utterance, `ProcessUtteranceAsync`:

1. Throws `SpeechRecognitionUnavailableException` if no recognizer is installed; the loop reports a fatal
   `PipelineErrorEvent` and stops.
2. Verifies the speaker (`OwnerVoiceVerifier`, §5.3) unless no owner reference or no speaker encoder exists.
3. Decides whether to ignore the utterance, then recognises and searches:

| `OwnerVoiceDecision` | Default settings | Effect on the search decision |
|---|---|---|
| `Owner` | Transcribed and searched | none |
| `Uncertain` (borderline similarity, or another voice inside the utterance) | Transcribed and searched; counted in `pipeline.utterances_uncertain` | `Confident` is capped to `NeedsConfirmation` |
| `NotOwner` | Ignored and never transcribed (`UtteranceIgnoredEvent`), when `RequireOwnerMatch` is on (default) | With `RequireOwnerMatch` off: transcribed, searched and capped to `NeedsConfirmation` |
| `InsufficientSpeech` (less than the encoder's minimum, 0.8 s for GE2E) | Always ignored | – |
| `NoProfile`, `ModelUnavailable` | Transcribed and searched | Capped to `NeedsConfirmation`. The app's microphone button is disabled without a usable profile (§2), so this occurs only in tests, tools and the Diagnostics benchmark when it has no enrollment clips. |

**Parallel verification** (`PipelineSettings.ParallelVerification`, opt-in, off by default; Settings > Owner voice >
"Faster results: transcribe while the voice is checked"). When on, and an owner reference and a verifier exist,
recognition starts immediately and verification runs beside it on a thread-pool thread. If the utterance is then
ignored, the recognition is cancelled and its output discarded. Another person's speech is therefore partly transcribed
in memory before being discarded; with the option off, non-owner utterances are never transcribed (while
`RequireOwnerMatch` is on). The tests `ParallelVerification_*` in `tests/BonikPOS.VoiceSearch.UnitTests/Pipeline/VoiceSearchPipelineTests.cs`
cover both modes.

Recognition uses `SpeechRecognitionOptions { LanguageMode }`. Empty text (including Whisper's no-speech rule) raises
`NothingRecognizedEvent` and listening continues. Otherwise `IMedicineSearchService.SearchAsync` receives a
`MedicineQuery` with the recognition confidence, the speaker decision and the recogniser's alternative readings, and
`SearchCompletedEvent` carries the result, recognition, verification, extractor status and `PipelineTimings`
(verification, recognition, search, end of speech to result). In `SingleQuery` mode the pipeline stops after the first
search result or after a non-fatal error; ignored and empty utterances keep it listening.

### 3.5 States and events

`PipelineState`: `Idle`, `Starting`, `Listening`, `HearingSpeech`, `Processing`, `Stopping`, `Faulted`.

| Event (`PipelineEvent`) | Raised when |
|---|---|
| `PipelineStateChangedEvent(State, Detail)` | Every state change |
| `AudioLevelEvent(RmsDbfs, PeakDbfs, SpeechProbability, InSpeech)` | Every 3rd 20 ms frame, from the raw microphone frame |
| `UtteranceCapturedEvent(SpeechMilliseconds, Truncated)` | The segmenter closed an utterance |
| `UtteranceIgnoredEvent(Verification, Reason)` | Not the owner, or too little speech |
| `NothingRecognizedEvent(Recognition)` | Recognition returned no text |
| `SearchCompletedEvent(Search, Recognition, Verification, Extraction, Timings)` | A search result is ready |
| `ListeningTimedOutEvent` | `SingleQuery` mode heard no speech for `MaxListenMilliseconds` |
| `PipelineErrorEvent(Message, Fatal)` | Fatal: no recognizer installed. Non-fatal: one utterance failed. |

Events are raised synchronously on the worker threads; exceptions thrown by handlers are caught and logged.

### 3.6 Settings

`PipelineSettings` (`Core/Settings/VoiceSearchSettings.cs`). Only `ParallelVerification` has a switch in the app (owner
voice settings screen); the others keep their code defaults.

| Setting | Default | Meaning |
|---|---|---|
| `Mode` | `SingleQuery` | `SingleQuery`: stop after one search result (or the idle timeout). `Continuous`: process every utterance until stopped. |
| `VadStartThreshold` / `VadEndThreshold` | 0.5 / 0.35 | Speech-probability hysteresis |
| `MinSpeechMilliseconds` | 250 | Shorter utterances are discarded |
| `EndSilenceMilliseconds` | 500 | Silence that ends an utterance; end-of-speech latency is measured from this point |
| `PreRollMilliseconds` | 300 | Audio kept before the detected start |
| `MaxUtteranceMilliseconds` | 8000 | Longer utterances are truncated |
| `MaxListenMilliseconds` | 15000 | Idle timeout in `SingleQuery` mode |
| `QueueCapacityFrames` | 150 | Capture queue capacity (20 ms frames) |
| `ParallelVerification` | false | See §3.4 |
| `WarmUpModels` | true | Run `IVoiceModelRuntime.WarmUpAsync` when the search screen prepares |

Related `VoiceSearchSettings` values: `LanguageMode` (`AutoBanglaEnglish`, `Bangla`, `English`), `AudioInput`,
`Accelerator` (`Auto`, `CpuOnly`), `InferenceThreads` (0 = min(4, cores)), `StreamingModelThreads` (1),
`InferenceThreadSpinning` (false), `TargetSpeakerExtractionEnabled` (true), and `Verification`
(`RequireOwnerMatch` true, `AcceptThresholdOverride` null, `UncertainBand` 0.08, `WindowDrop` 0.15). Language, audio
input and accelerator are on the Settings screen; extraction, `RequireOwnerMatch` and the threshold override are on the
owner voice settings screen; the thread settings, `UncertainBand` and `WindowDrop` have no UI.

### 3.7 Start, stop and cancellation

`StartAsync` (serialised by a semaphore; only from `Idle` or `Faulted`) initialises the runtime, creates a VAD instance,
the segmenter and an extractor (`SetTarget` receives the owner's extraction embedding only when
`TargetSpeakerExtractionEnabled`), starts capture and the two workers. The capture worker starts only after the session
is registered, so a stream that ends at once cannot leave a stale session behind.

`StopAsync` requests a stop (`Stopping`) and stops capture; the queue completes, the capture loop flushes the current
utterance, and queued utterances are still processed. If the `CancellationToken` passed to `StopAsync` fires, the session
token is cancelled and in-flight work is abandoned. The session then disposes the VAD, extractor and token source and
returns to `Idle` (or `Faulted` after an exception).

Every async API in the pipeline, runtime, recognizer, search service and catalog adapter takes a `CancellationToken`.
`WhisperOnnxRecognizer` also registers a callback that sets `RunOptions.Terminate = true`, so cancellation stops an ONNX
Runtime `Run` that is already executing (checked between operators); the resulting `OnnxRuntimeException` is reported as
`OperationCanceledException`, and the greedy decoder checks the token before every step.

## 4. Enrollment and the owner profile

- `EnrollmentSession` (`Core/Enrollment/EnrollmentSession.cs`) records each sample (up to 9 s, ends after 1.2 s of
  silence or 7 s without speech), runs `RecordingQualityAnalyzer` (speech ≥ 1.5 s, level between −40 and −6 dBFS, SNR
  ≥ 15 dB, clipping ≤ 0.2 %), embeds the speech with the speaker encoder, rejects samples with a window far from the
  rest (possible second speaker) or inconsistent with the earlier samples, and needs 5 accepted samples.
  Prompts (`EnrollmentPrompts`) mix Bangla, English and Banglish; the last one contains random digits (the code states
  this is not liveness detection).
- `Complete` builds a `VoiceProfile`: the centroid embedding of the installed encoder (the schema allows one entry per
  encoder), leave-one-out calibration (suggested accept threshold = min(mean − 2·std, lowest score − 0.03), clamped to
  the encoder's limits from `models/models.json`), the consent record and an enrollment summary. No audio is part of
  the profile.
- `OwnerProfileService` (`Core/Profiles/OwnerProfileService.cs`) loads the profile and checks it with
  `VoiceProfileCompatibility.Check`: the encoder id, version, SHA-256 and dimension must match the installed encoder,
  otherwise re-enrollment is required. `BuildReference` turns the profile into an `OwnerVoiceReference` (accept
  threshold from the enrollment or the owner's override; reject = accept − `UncertainBand`; window floor = accept −
  `WindowDrop`). `ExtractionTarget` returns the embedding from the encoder an installed TSE model requires.
- `MicrophoneTester` (`Core/Enrollment/MicrophoneTester.cs`) records 6 s for the microphone test and reports level,
  noise floor, SNR, clipping and dropped frames.

## 5. AI runtime

### 5.1 Model manifest and integrity

Models are described by `models.json` manifests (format `bonikpos-models/1`; `ModelDescriptor` in
`Core/Models/ModelContracts.cs`): `id`, `version`, `role` (`VoiceActivity`, `SpeakerEncoder`,
`TargetSpeakerExtraction`, `SpeechRecognition`), adapter `format`, `files` (logical name → relative path), `sha256`
(relative path → expected hash), `license`, `source`, `isTestFixture` and adapter `options`.

`FileSystemModelProvider` (`Infrastructure/Models/FileSystemModelProvider.cs`) reads manifests from its roots in
priority order and, per role, returns the first entry whose files exist and pass the SHA-256 check. Entry states:
`Ready`, `NotInstalled` (file missing), `ChecksumMismatch`, `InvalidManifest` (a path escapes the model directory), and
adapter-reported `IncompatibleContract` / `LoadFailed`. A file whose hash differs is never handed to an adapter. An entry
without a pinned hash is accepted with the message "no checksum pinned in the manifest" (`register_model.py` pins
hashes). Hashes are cached by path, size and modification time in `model-checksums.json` in the cache directory.

`BootstrappingModelProvider` (`App/Services/MauiPlatformServices.cs`) first runs `BundledModelExtractor`, which copies
the packaged `models/` assets into `{AppDataDirectory}/models-bundled` (ONNX Runtime loads from file paths), verifies
each copied file against the manifest (temp file, then move), skips unchanged files and never extracts test fixtures.
Release builds use only the bundled models; Debug builds also read a side-load folder first (Android: the app's
external files directory `models/`; iOS: `Documents/models`). Tensor contracts, export, registration and side-loading
are described in [MODELS.md](MODELS.md).

### 5.2 `OnnxVoiceModelRuntime`

`src/BonikPOS.VoiceSearch.AI/Runtime/OnnxVoiceModelRuntime.cs` loads every installed model once on a worker thread and
keeps the sessions. `InitializeAsync` does nothing if the models were loaded with the same session preferences;
`ReloadAsync` forces a reload.

- **Per-component availability.** Each role is loaded in its own `try`: a missing or broken model (bad config, missing
  file entry, unknown format, tensor-contract mismatch) leaves only that component unavailable and is reported to the
  provider and in `VoiceModelRuntimeStatus`. Missing components are never replaced by fakes:
  `UnavailableSpeechRecognizer` throws `SpeechRecognitionUnavailableException`, and `UnavailableTargetSpeakerExtractor`
  copies audio unchanged and reports itself as not installed.
- **Test fixtures.** Entries with `isTestFixture: true` (random-weight Whisper, arithmetic TSE graph) are refused
  unless the runtime was constructed with `allowTestFixtures: true`, which only tests and tools do.
- **TSE/encoder pairing.** A TSE model conditioned on a different encoder than the installed one is rejected.
- **Warm-up.** `WarmUpAsync` runs a 1 s synthetic tone (never speech) through the VAD, the speaker encoder, the TSE
  model and the recognizer (2 tokens) so that one-time ONNX Runtime costs do not land on the first utterance. It does
  the work once per load (later calls return 0) and records `model.warm_up_ms`.

### 5.3 Adapters

| Component | Code | Format | Notes |
|---|---|---|---|
| Voice activity | `Vad/SileroVad.cs` | (manifest label `silero-vad`) | Silero VAD v5/v6 contract checked at load: `input` [1, 64 + 512], `state` [2, 1, 128], optional `sr`; outputs `output`, `stateN`. Tensors are bound once to pinned arrays. |
| Speaker encoder | `Speakers/Ge2eSpeakerEncoder.cs` | `ge2e-onnx` | Resemblyzer 0.1.4 port: power mel (n_fft 400, hop 160, 40 mels), 160-frame partials at 1.3 per second, mean of partial embeddings, L2-normalised 256-dim; quiet input raised to −30 dBFS. Window 1.6 s, minimum speech 0.8 s. |
| Speaker encoder | `Speakers/FbankSpeakerEncoder.cs` | `fbank-speaker-onnx` | Kaldi fbank front-end for WeSpeaker / 3D-Speaker style exports; parameters from the manifest. Not bundled. |
| Owner verification | `Speakers/OwnerVoiceVerifier.cs` | – | Cosine similarity of the utterance embedding to the enrolled centroid: ≥ accept → `Owner`; ≥ reject → `Uncertain`; else `NotOwner`. For speech ≥ 1.5 windows it also embeds overlapping windows; a window below the window floor turns `Owner` into `Uncertain` (possible second voice). This is verification only: it cannot separate overlapping voices. |
| Speech recognition | `Recognition/Whisper/WhisperOnnxRecognizer.cs`, `WhisperModelConfig.cs` | `bonikpos-whisper-onnx/1` (files `config`, `encoder`, `decoder`, `tokens`, produced by `tools/models/export_whisper_onnx.py`) | Encoder computes cross-attention K/V once; the decoder uses a self-attention KV cache; greedy decoding with openai-whisper suppression rules, no timestamps, up to 128 tokens, repetition guard; no-speech rule (no-speech probability > 0.6 and mean log-prob < −1 gives empty text). Language: forced Bangla or English, or automatic (Bangla vs English logits). In automatic mode, when the chosen language's probability between the two is below 0.85, the other language is decoded too and returned as an alternative reading that the search uses only as a correction candidate. Confidence = exp(mean token log-prob). One recognition at a time. |
| Target-speaker extraction | `Extraction/OnnxStreamingTargetSpeakerExtractor.cs` | `streaming-tse-onnx/1` | Chunk-based stateful model; every tensor (chunk size, embedding size, states, `required_encoder_id`) is declared in the manifest and checked against the graph. |
| Target-speaker extraction | `Extraction/StftStreamingTargetSpeakerExtractor.cs` | `streaming-tse-stft-onnx/1` | One STFT frame per call (default n_fft 512, hop 256, periodic sqrt-Hann, 50 % overlap); the adapter does STFT, inverse STFT and overlap-add. |
| No extraction | `Extraction/UnavailableTargetSpeakerExtractor.cs` | – | Used when no compatible TSE model is installed; audio passes through and the UI says other voices are not removed. |

Both TSE adapters process 20 ms frames on the capture worker, keep recurrent state in preallocated buffers, delay the
output by a constant number of samples so each call returns exactly as many samples as it received, pass audio through
unchanged when no owner embedding is set, and switch themselves off (`Failed`) if the model throws at run time.

### 5.4 ONNX Runtime session policy

`Onnx/OnnxSessionFactory.cs` creates one long-lived `InferenceSession` per model file. Its static constructor also
calls `OnnxRuntimeTelemetry.Disable()`.

| Model | Preferences | Accelerator when `Accelerator = Auto` | Intra-op threads |
|---|---|---|---|
| Silero VAD | streaming | no (CPU) | `StreamingModelThreads` (default 1) |
| Speaker encoder | per utterance | no (CPU) | `InferenceThreads` (0 = min(4, cores)) |
| TSE (both formats) | streaming | yes | `StreamingModelThreads` |
| Whisper encoder and decoder | per utterance | yes | `InferenceThreads` |

- Common options: `ORT_ENABLE_ALL`, sequential execution, one inter-op thread, memory pattern and CPU arena on, ORT log
  level `ERROR`, and `session.intra_op.allow_spinning` = `0` unless `InferenceThreadSpinning` is on (worker spinning
  is off by default).
- With one intra-op thread (the default for streaming models) a `Run` executes on the calling thread, i.e. the capture
  worker, and no worker pool exists.
- Accelerators: XNNPACK on Android (ORT's own intra-op pool reduced to 1; XNNPACK gets the thread count) and CoreML on
  iOS (`ModelFormat=MLProgram`, `MLComputeUnits=ALL`). If creating the accelerated session throws
  `OnnxRuntimeException`, `EntryPointNotFoundException` or `DllNotFoundException`, the factory logs a warning and
  creates a CPU session. The provider actually used is shown on the Diagnostics screen.

### 5.5 What is bundled

`models/models.json` currently lists two models, both with pinned SHA-256:

| Role | Model | License |
|---|---|---|
| `VoiceActivity` | `silero-vad` 6.2.3 (`models/vad/silero-vad-6.2.3-16k.onnx`) | MIT |
| `SpeakerEncoder` | `ge2e-resemblyzer` 0.1.4 (`models/speaker/ge2e-resemblyzer-0.1.4.onnx`), verification thresholds marked PROVISIONAL | Apache-2.0 |

There is no `SpeechRecognition` and no `TargetSpeakerExtraction` entry (`models/asr/` and `models/tse/` are
git-ignored). A build from this repository therefore runs without speech recognition, so voice search is disabled and
typed search works, until a Whisper export is added with `tools/models/export_whisper_onnx.py` and
`tools/models/register_model.py`. No trained TSE model exists. Because only Whisper and TSE may use an accelerator,
all bundled models run on the CPU.

## 6. Medicine search

### 6.1 Catalog sources

`IMedicineCatalogSource` implementations: `DemoCatalogSource` (embedded `Catalog/demo-catalog.json`, 55 products with
`DEMO-` ids, labelled as demo data in the UI), `JsonCatalogSource` (a local product index file), `InMemoryCatalogSource`
(tests), and `BonikPosApiCatalogSource` in Infrastructure. `CatalogManager.CreateSource` picks one from
`VoiceSearchSettings.CatalogSource`; a failing real catalog never falls back to the demo catalog, and
`MedicineSearchService.LoadCatalogAsync` replaces the active index only after the new catalog has loaded and been
indexed. Details and formats: [BACKEND_INTEGRATION.md](BACKEND_INTEGRATION.md).

### 6.2 Normalisation, transliteration and the index

- `Text/TextNormalizer.cs`: Unicode NFC, lower case, Bengali digits to ASCII, zero-width joiners removed, punctuation to
  spaces except strength characters (`%`, `/`, `+`, decimal points), digits split from letters ("500mg" → "500 mg").
- `Text/BanglaTransliterator.cs`: Bengali script to Latin, tuned for loanword brand names (the inherent vowel is
  normally not emitted).
- `Text/PhoneticKey.cs` and `Text/PhoneticSimilarity.cs`: a pronunciation key shared by Latin names, Banglish and
  transliterated Bangla, and a weighted edit distance in which vowel edits are cheap.
- `Text/NumberWords.cs`, `Text/QueryParser.cs`, `Text/MedicineVocabulary.cs`: spoken numbers in English and Bangla,
  strengths (number + unit, "per" combinations, g → mg), bare numbers, dosage forms, ignorable quantity and filler
  words ("two strips", "দিন"), and conversational words that may only match a brand exactly.
- `Index/MedicineIndex.cs`: per product, name variants for the brand, local name, aliases, generic name (with
  parenthesised synonyms) and each molecule of a combination; exact maps on compact text and on phonetic keys, a
  consonant-skeleton map (keys of 3+ consonants) and a trigram inverted index on phonetic keys. A repeated product id
  keeps only its first row and produces a warning.

### 6.3 Matching

`Ranking/MedicineMatcher.cs` builds windows of up to 4 name tokens and scores retrieved variants: same compact text
1.0 (`Exact`); same phonetic key 0.96 (`ExactPhonetic`); prefix of 3+ characters 0.78–0.92 (`Prefix`); otherwise the
larger of phonetic and spelling similarity (`Fuzzy`); windows made only of conversational words never match
approximately. Scores are weighted by how much of the spoken name the window covers. Generic-name matches are scaled
by 0.92 (a single molecule of a combination by a further 0.85). A requested strength or form that the product lacks
lowers the score and is recorded as a `Mismatch`; it never makes the product a match. A product found only through an
alternative recogniser reading has its score multiplied by 0.97 and is marked with `MatchedReading`. Products the
owner previously confirmed for the same normalised text (from history) are added as exact candidates with a note;
confirmation counts only break ties. Candidates below `MinCandidateScore` (0.55) are dropped; at most `MaxCandidates`
(10) are returned.

### 6.4 Search decision policy

`Ranking/SearchDecisionPolicy.cs` maps the ranked candidates to a `SearchDecision` (thresholds in `SearchThresholds`,
`Core/Medicine/MedicineContracts.cs`; four of them are adjustable on the Settings screen):

| Order | Condition | Decision |
|---|---|---|
| 1 | No candidate | `NoMatch` (up to 3 "did you mean" brand names with name similarity ≥ 0.45; never applied automatically) |
| 2 | The spoken name is a catalog name, but the top result is another, similar name; or a longer spoken name exactly matches another catalog name than the top result | `LowConfidence` ("… is in the catalog but not as requested") |
| 3 | Top candidate's strength or form is a `Mismatch` | `LowConfidence` ("No X with the requested strength/form was found …") |
| 4 | Several candidates within `AmbiguityMargin` (0.06) of the top, top ≥ `MinConfirmScore` (0.75), not a weak fuzzy match | `Ambiguous` (the owner chooses) |
| 5 | Top ≥ `ConfidentScore` (0.90), `Exact`/`ExactPhonetic` name, found through the primary text, form not merely `Compatible` | `Confident` |
| 6 | Top ≥ `MinConfirmScore`, not a weak fuzzy match | `NeedsConfirmation` |
| 7 | Otherwise | `LowConfidence` |

A weak fuzzy match is a `Fuzzy` top match with name similarity below `MinFuzzyConfirmNameScore` (0.84). Two caps apply
afterwards: recognition confidence below `MinRecognitionConfidence` (0.45) turns `Confident`, `NeedsConfirmation` and
`Ambiguous` into `LowConfidence`; any speaker decision other than `Owner` turns `Confident` into `NeedsConfirmation`.

Even `Confident` results are only shown; the owner must tap Select (search screen) or Choose (ambiguity screen).
`MedicineProduct` values are kept exactly as the source returned them, the index keeps its own normalised copies, and
the selected product's id is shown and stored. Evaluation method and results: [SEARCH_EVALUATION.md](SEARCH_EVALUATION.md).

## 7. Storage and security

| Item | Code | Location | Protection |
|---|---|---|---|
| Voice profile | `Infrastructure/Storage/EncryptedVoiceProfileStore.cs` | `{data}/owner-voice-profile.bin` | AES-256-GCM, key `bonikpos.profile.datakey.v1` |
| Search history (≤ 500 entries) | `Infrastructure/Storage/EncryptedSearchHistoryStore.cs` | `{data}/search-history.bin` | AES-256-GCM, key `bonikpos.history.datakey.v1` |
| Settings | `Infrastructure/Storage/JsonSettingsStore.cs` | `{data}/settings.json` | Plain JSON, no secrets |
| API token and data keys | `ISecretStore` → `MauiSecretStore` (`App/Services/MauiPlatformServices.cs`) | MAUI `SecureStorage` (Android Keystore-backed, iOS Keychain) | Platform |
| Catalog cache | `FileCatalogCache` (`Infrastructure/Catalog/BonikPosApiCatalogSource.cs`) | `{cache}/catalog-cache.json` | Plain JSON |

- `AesGcmDataProtector` (`Infrastructure/Security/AesGcmDataProtector.cs`): a 256-bit key generated on the device and
  kept only in the secret store; a fresh 12-byte nonce per write; envelope `"BVSE" | version 1 | nonce | tag |
  ciphertext`; the header and a purpose string are authenticated as associated data. `DestroyKeyAsync` removes the
  key, which makes every copy of the ciphertext unreadable (crypto-shredding).
- Files are written atomically (`AtomicFile`: temp file, then move); the profile is encrypted before it is written.
- Paths (`MauiAppPaths.Create`): `{data}` = `FileSystem.AppDataDirectory/bonikpos`, `{cache}` =
  `FileSystem.CacheDirectory/bonikpos`, bundled models in `FileSystem.AppDataDirectory/models-bundled`. On iOS the data
  and bundled-model directories are marked excluded from backup; on Android `allowBackup="false"` and
  `data_extraction_rules.xml` exclude all app data.

Data inventory, retention, deletion and backup details: [PRIVACY.md](PRIVACY.md).

## 8. Diagnostics and telemetry

`PerformanceTelemetry` (`Core/Telemetry/PerformanceTelemetry.cs`) is thread-safe and in memory only: each metric keeps
its last 512 values in a ring (count, mean, p50, p95, max, last) plus integer counters. It is never written to storage
or sent anywhere, and it records numbers only.

| Metric (`TelemetryMetrics`) | Recorded by |
|---|---|
| `audio.capture_to_processed_ms`, `pipeline.frame_processing_ms`, `tse.chunk_ms` (when extraction is active), `audio.input_latency_ms` (once per second) | Capture loop |
| `speaker.verify_ms`, `asr.total_ms`, `search.query_ms`, `pipeline.end_of_speech_to_result_ms`, `pipeline.end_of_speech_to_outcome_ms` | Recognition loop |
| `asr.features_ms`, `asr.encoder_ms`, `asr.decoder_ms` | `WhisperOnnxRecognizer` |
| `model.init_ms.<Role>`, `model.warm_up_ms` | `OnnxVoiceModelRuntime` |
| `vad.chunk_ms`, `speaker.embed_ms`, `device.process_cpu_percent` | Diagnostics self-test and refresh |
| Counters: `audio.frames_captured`, `audio.frames_dropped`, `audio.discontinuities`, `pipeline.utterances`, `pipeline.utterances_not_owner`, `pipeline.utterances_uncertain`, `pipeline.recognition_queue_drops` | Pipeline |

`device.managed_memory_mb` and `device.working_set_mb` are defined but not recorded by any code.

The Diagnostics page (`DiagnosticsViewModel`) shows: each model's state, execution provider, load time, licence and
message; the telemetry snapshot and capture-queue statistics; battery, thermal state, memory, process CPU and catalog
status. It offers a synthetic-signal self-test (VAD time per 32 ms chunk, embedding time for 3 s, recognition time), a
model reload, a telemetry reset, and the full pipeline benchmark: `PipelineBenchmark` replays clips through a real
`VoiceSearchPipeline` via `FileAudioCapture` in real time, three repetitions, once with `CpuOnly` and once with `Auto`
(only the configured setting when "Compare CPU and accelerator" is off), and reports latency, dropped frames, CPU,
memory, battery and thermal readings (`bonikpos-pipeline-bench/1`, copied as JSON or Markdown to the clipboard). Clips
come from the `benchmark` folder next to the side-load models folder when it contains `clips.json` (any build),
otherwise from the synthetic TTS clips that only Debug builds bundle. Procedure and results:
[BENCHMARKS.md](BENCHMARKS.md).

## 9. Known gaps (verified in the code)

- No speech-recognition or target-speaker-extraction model is in `models/models.json` (§5.5).
- `TargetSpeakerExtractionAvailability.Disabled` is never produced: with extraction switched off while a TSE model is
  installed, the extractor reports `NeedsReEnrollment`.
- `IMedicineSearchService.InitializeAsync` (which loads the demo catalog) is not called by the app; catalogs are loaded
  through `CatalogManager.ReloadAsync`.
- `HistoryOutcome.NoMatch` is defined but never written.
- The iOS target cannot be built on Linux; only compile-only checks exist (`tools/ios-compile-check`).
- Known catalog-adapter and privacy gaps are listed in [BACKEND_INTEGRATION.md §10](BACKEND_INTEGRATION.md#10-known-limitations-verified-in-the-code)
  and [PRIVACY.md §8](PRIVACY.md#8-known-gaps-and-open-items).
