# Phase reports

The product specification asked for nine phases and, at the end of each, a list of files, completed features, build
and run commands, tests and results, missing dependencies or model assets, and remaining limitations. This is that
list, written against the code at the end of the work (2026-10-09). Paths are relative to `BonikPOS Mobile AI/`.

Common build/test commands (verified in the development container; details in [TEST_RESULTS.md](TEST_RESULTS.md)):

```bash
dotnet build tests/BonikPOS.VoiceSearch.UnitTests/BonikPOS.VoiceSearch.UnitTests.csproj -c Release
tests/BonikPOS.VoiceSearch.UnitTests/bin/Release/net10.0/BonikPOS.VoiceSearch.UnitTests             # 414 passed
dotnet build tests/BonikPOS.VoiceSearch.IntegrationTests/BonikPOS.VoiceSearch.IntegrationTests.csproj -c Release
tests/BonikPOS.VoiceSearch.IntegrationTests/bin/Release/net10.0/BonikPOS.VoiceSearch.IntegrationTests # 41 passed
dotnet build src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj -f net10.0-android -c Debug   # 0 warnings, 0 errors
tools/ios-compile-check/ios-compile-check.sh                                                          # iOS C# compile check only
```

## Phase 1 — Environment and architecture

- **Environment found:** Ubuntu 24.04 x86-64 container, 4 logical CPUs, 16 GB RAM, no GPU, no hardware
  virtualisation (no Android emulator possible), no macOS. .NET SDK 10.0.401 (`global.json` requires 10.0.100 or a later feature band and selects the
  Microsoft.Testing.Platform test runner), MAUI
  workload 10.0.110 for Android only (the iOS workload cannot be installed on Linux), Android SDK + JDK 21, Python 3
  for the model tools. Outbound network goes through a proxy. Checked on 2026-10-09 with `curl` and the proxy's
  relay-failure log: connections to huggingface.co, openaipublic.azureedge.net (official Whisper checkpoints),
  dl.google.com (Android emulator images) and www.openslr.org (speech corpora) were refused (CONNECT 403), and
  github.com release pages answered HTTP 403; nuget.org and pypi.org worked. This decided what could be downloaded
  and verified.
- **Technology choice:** .NET MAUI (one C# code base for Android and iOS, native audio APIs through the platform
  bindings, the ONNX Runtime C# API with the official Android AAR and iOS xcframework), CommunityToolkit.Mvvm,
  Microsoft.Extensions.DependencyInjection, System.Text.Json source generation, central package management,
  xunit.v3.
- **Files:** `global.json`, `Directory.Build.props` (nullable, warnings as errors), `Directory.Packages.props`,
  `NuGet.config`, [ARCHITECTURE.md](ARCHITECTURE.md).
- **Limitations:** no device or macOS available for the whole project (see phase 9).

## Phase 2 — Solution, MVVM, navigation, DI, screens

- **Files:** `BonikPOS.VoiceSearch.sln`; `src/BonikPOS.VoiceSearch.{Core,Audio,AI,MedicineSearch,Infrastructure,Presentation,App}`;
  `src/BonikPOS.VoiceSearch.App/MauiProgram.cs` (DI composition), `AppShell.xaml`, `Views/*.xaml`, `Controls/`,
  `Resources/`; view models in `src/BonikPOS.VoiceSearch.Presentation/ViewModels/`.
- **Features:** Shell navigation with tabs (search, history, settings) and routed pages; all services injected;
  responsive layouts (two-column results on tablets/desktop idiom); accessible labels (`SemanticProperties`).
- **The 15 requested screens:**

| # | Requested screen | Implementation |
|---|---|---|
| 1 | Welcome | `WelcomePage` (purpose, privacy summary, consent) |
| 2 | Owner enrollment | `EnrollmentInstructionsPage` |
| 3 | Microphone permission and audio test | `MicrophoneTestPage` (permission requested here, live level meter) |
| 4 | Voice enrollment recording | `EnrollmentRecordingPage` (multiple samples, per-sample quality feedback, progress) |
| 5 | Enrollment results | `EnrollmentResultPage` |
| 6 | Voice-search home | `SearchPage` |
| 7–10 | Listening indicator, processing indicator, recognised text, ranked results | States of `SearchPage` (`ListeningIndicator` control, processing state, editable recognised text, ranked result list with brand, generic, strength, form and product id) |
| 11 | Ambiguous-result confirmation | `AmbiguityPage` |
| 12 | No match | `NoMatchPage` (with spelling suggestions from the catalog) |
| 13 | Search history | `HistoryPage` (encrypted, can be disabled and cleared) |
| 14 | Owner voice settings | `VoiceSettingsPage` (thresholds, extraction, opt-in parallel verification, re-enrol, delete) |
| 15 | Model and performance diagnostics | `DiagnosticsPage` (model status, speed test, full pipeline benchmark) |
| — | General settings | `SettingsPage` (catalog source and BonikPOS API, recognition language, audio input, accelerator, history, search thresholds, delete all data) |

- **Search screen:** large microphone button, listening indicator, honest extraction status ("unavailable" /
  "installed, re-enrol to activate" / "active" — never "other speakers removed"), recognised text with correction,
  repeat search, clear low-confidence state.
- **Tests:** `Presentation/ViewModelTests.cs` (navigation to clarification/no-match, history recording).
- **Limitations:** no UI automation tests; screens were not exercised on a device.

## Phase 3 — Native audio capture and buffer tests

- **Files:** `src/BonikPOS.VoiceSearch.Audio/` — `Platforms/Android/AndroidMicrophoneCapture.cs` (AudioRecord on a
  dedicated `URGENT_AUDIO` thread, 10 ms blocking reads, VOICE_RECOGNITION / MIC / UNPROCESSED sources, float or
  16-bit, 16/48/44.1 kHz), `Platforms/iOS/IosMicrophoneCapture.cs` (AVAudioEngine input tap, `Measurement` mode,
  interruption and route-change handling), `CaptureProcessor.cs` (conversion, polyphase resampling to 16 kHz,
  re-framing to 20 ms), `AudioFrameQueue.cs` (bounded, drop-oldest overrun policy, discontinuity flags, pooled
  buffers), `FileAudioCapture.cs` (file replay for tests and benchmarks), `WavFile.cs`.
- **Features:** the capture callback only copies into preallocated buffers and enqueues; inference never runs on it;
  platform input-latency estimates (Android `AudioRecord.getTimestamp`, iOS host time + `inputLatency`).
- **Tests:** `Audio/AudioFrameQueueTests.cs`, `Audio/CaptureProcessorTests.cs`, `Audio/FileAudioCaptureTests.cs`
  (all pass).
- **Limitations:** the Android and iOS capture classes compile (Android build; iOS compile check) but have not run on
  hardware.

## Phase 4 — Enrollment, speaker embeddings, secure profile, model adapters

- **Files:** `src/BonikPOS.VoiceSearch.Core/Enrollment/EnrollmentSession.cs` (5 accepted samples, quality,
  single-speaker and consistency checks, threshold suggestion), `Core/Profiles/` and
  `Infrastructure/Storage/EncryptedVoiceProfileStore.cs` (AES-GCM, key in platform secure storage, deletion with key
  destruction), `AI/Speakers/Ge2eSpeakerEncoder.cs` (bundled GE2E/Resemblyzer ONNX, Apache-2.0),
  `AI/Speakers/FbankSpeakerEncoder.cs` (alternative fbank encoders), `AI/Speakers/OwnerVoiceVerifier.cs`,
  model contracts and manifest provider (`Core/Models/ModelContracts.cs`, `Infrastructure/Models/FileSystemModelProvider.cs`),
  `tools/models/export_ge2e_onnx.py`, `models/models.json`.
- **Features:** profile stores the embedding with encoder id, version, checksum and dimension; an encoder change asks
  for re-enrolment instead of reusing an incompatible embedding; the embedding is not treated as a password
  (owner-voice matching only prefers the owner; it authorises nothing). Spoofing/replay/synthetic voices are not
  detected — the UI states that matching is not identity proof.
- **Tests:** `Profiles/OwnerProfileTests.cs`, `Infrastructure/EncryptionTests.cs`, `AI/OwnerVoiceVerifierTests.cs`,
  GE2E parity (cosine 1.0000000) in the integration tests.
- **Missing:** calibrated thresholds (current ones are provisional), anti-spoofing.

## Phase 5 — Target-speaker extraction

- **Outcome:** no published model met all requirements (commercial licence for code and weights, downloadable
  weights, streaming, phone-sized, conditioned on an enrolment embedding); see [TSE_MODEL_AUDIT.md](TSE_MODEL_AUDIT.md).
  **Target-speaker extraction is therefore reported as unavailable in the app**; owner verification still rejects
  other speakers' utterances after the fact, but overlapping speech is not removed.
- **Files:** `src/BonikPOS.VoiceSearch.AI/Extraction/OnnxStreamingTargetSpeakerExtractor.cs`
  (`streaming-tse-onnx/1`), `StftStreamingTargetSpeakerExtractor.cs` (`streaming-tse-stft-onnx/1`: C# STFT, model,
  inverse FFT + overlap-add, recurrent state), `UnavailableTargetSpeakerExtractor` (pass-through, reports inactive);
  `tools/models/tse/pgtcrn.py`, `export_pgtcrn_onnx.py`, `train_pgtcrn.py`, `LICENSE-GTCRN`;
  `tests/fixtures/tse/` (random-weight fixture + Python reference output).
- **Measured:** our GTCRN reimplementation reproduces the upstream streaming model (≤ 9.9e-6); the C# adapter
  reproduces the Python reference (3.3e-7); algorithmic latency 28 ms for 20 ms frames; host compute in
  [BENCHMARKS.md](BENCHMARKS.md).
- **Missing:** trained pGTCRN weights, licensed training data, legal review, device timings. The target-speaker-
  conditioned ASR alternative was reviewed in the audit; none was integrated.

## Phase 6 — Speech recognition and medicine matching

- **Files:** `src/BonikPOS.VoiceSearch.AI/Recognition/Whisper/` (log-mel front-end, KV-cached greedy decoder,
  Bangla/English language choice with a second-language alternative, no-speech rule, repetition guard,
  cancellation inside ONNX Runtime runs), `tools/models/export_whisper_onnx.py` (export, optional `--n-audio-ctx`
  and INT8, validation); `src/BonikPOS.VoiceSearch.MedicineSearch/` (normalisation, Bangla→Latin transliteration,
  number words in Bangla/English, strength and form parsing, trigram + consonant-skeleton index, matcher,
  `SearchDecisionPolicy`), `tools/BonikPOS.VoiceSearch.Evaluation/` (labelled evaluation, synthetic 20k catalog).
- **Measured:** search on a synthetic 20,000-product held-out test set: top-1 98.9 %, top-5 99.7 %, 10 false matches,
  0 confident false matches ([SEARCH_EVALUATION.md](SEARCH_EVALUATION.md) — developer/synthetic data, not field
  accuracy). Whisper decoding parity on a random-weight fixture.
- **Missing:** a real Whisper checkpoint export (the official weights could not be downloaded here) and therefore any
  recognition accuracy; real catalog and field queries.
- **Safety rules implemented:** never substitute a different product silently; distinct products are never merged;
  product ids and catalog values are shown as returned; low recognition confidence or an unconfirmed speaker caps
  the decision; results are search candidates, not medical advice; no dose recommendation.

## Phase 7 — Backend adapter, settings, diagnostics, privacy

- **Files:** `src/BonikPOS.VoiceSearch.Infrastructure/Catalog/` (BonikPOS API source with configurable URL, auth and
  field names, paging, retries, HTTPS-only; file cache; local JSON catalog), `Core/Settings/` + `Infrastructure/Storage/JsonSettingsStore.cs`,
  `Presentation/ViewModels/DiagnosticsViewModel.cs`, `Core/Telemetry/PerformanceTelemetry.cs` (numbers only),
  `AI/Onnx/OnnxRuntimeTelemetry.cs`, platform manifests.
- **Privacy:** see [PRIVACY.md](PRIVACY.md) — permission only when needed, consent, local processing, no raw
  recordings kept, encrypted profile and history, deletion and re-enrolment, no audio upload, backups excluded,
  ONNX Runtime's built-in usage telemetry disabled. Backend details: [BACKEND_INTEGRATION.md](BACKEND_INTEGRATION.md).
- **Tests:** `Infrastructure/BonikPosApiCatalogSourceTests.cs`, `SettingsAndHistoryTests.cs`, `FileSystemModelProviderTests.cs`.
- **Missing:** the real BonikPOS API contract (the adapter is configurable; a clearly labelled demo catalog is used
  until it is connected).

## Phase 8 — Automated tests and performance benchmarks

- **Tests:** 414 unit and 41 integration tests, all passing; the defects found and fixed while writing them and the docs
  ([TEST_RESULTS.md](TEST_RESULTS.md)).
- **Benchmarks:** in-app full pipeline benchmark (Diagnostics), `pipeline-bench`, `asr-bench`, `tse-bench`; host
  results and the device procedure in [BENCHMARKS.md](BENCHMARKS.md). Scheduling changes measured on the host:
  process CPU 354–373 % → 37–38 % of one core; end of speech → outcome for owner clips 693–761 ms → 279–308 ms
  (median); 0 dropped frames in every run.
- **Missing:** device measurements (no phones available).

## Phase 9 — Build and validate Android and iOS

- **Android:** Debug build succeeds with 0 warnings / 0 errors; APK contains the correct ONNX Runtime native
  libraries (arm64-v8a, x86_64), bundled models and, in Debug only, the synthetic benchmark clips; ONNX Runtime's
  telemetry content provider is removed. Not run on a device or emulator (no hardware virtualisation; emulator
  images blocked). Release signing not done. Steps: [DEPLOYMENT_ANDROID.md](DEPLOYMENT_ANDROID.md).
- **iOS:** cannot be built without macOS/Xcode; the iOS-specific C# compiles against the iOS reference assemblies
  (`tools/ios-compile-check/`). Steps for a Mac: [DEPLOYMENT_IOS.md](DEPLOYMENT_IOS.md).
- **Model integration instructions:** [MODELS.md](MODELS.md).
