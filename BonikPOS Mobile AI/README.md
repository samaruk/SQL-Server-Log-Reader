# BonikPOS Owner-Voice Medicine Search (Android and iOS)

A .NET MAUI app for medicine shops. The owner enrols their voice once; during work they tap the microphone, say a
medicine name in Bangla, English or Banglish, and get ranked catalog products (brand, generic, strength, form,
product id) to confirm. Speech from other people near the phone is not accepted as the owner's, and the app never
silently substitutes a different medicine. All audio is processed on the phone.

## Status at a glance (2026-10-09)

| Area | State | Evidence |
|---|---|---|
| Android app | Builds (Debug, 0 warnings / 0 errors); **not yet run on a phone** | [TEST_RESULTS.md](docs/TEST_RESULTS.md), [DEPLOYMENT_ANDROID.md](docs/DEPLOYMENT_ANDROID.md) |
| iOS app | Code complete; iOS-only C# compile-checked against the iOS SDK reference assemblies; **needs a Mac to build and run** | [DEPLOYMENT_IOS.md](docs/DEPLOYMENT_IOS.md) |
| Audio capture | AudioRecord (Android) / AVAudioEngine (iOS) → bounded queue; inference never on the audio callback | [ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Owner enrolment and verification | Implemented with the bundled GE2E speaker encoder; thresholds are **provisional** (no FAR/FRR measured yet) | [EVALUATION_PLAN.md](docs/EVALUATION_PLAN.md) |
| Target-speaker extraction | Streaming adapters, training/export tooling and parity tests exist; **no trained model is available, so the app reports extraction as unavailable** | [TSE_MODEL_AUDIT.md](docs/TSE_MODEL_AUDIT.md) |
| Speech recognition | Whisper ONNX adapter (Bangla/English, KV cache, cancellation) and export tool; **no model is bundled** — export and install one; accuracy not yet measured | [MODELS.md](docs/MODELS.md) |
| Medicine search | Implemented for ~20,000 products; evaluated on developer-authored and synthetic labelled data (not field data) | [SEARCH_EVALUATION.md](docs/SEARCH_EVALUATION.md) |
| BonikPOS backend | Configurable API adapter (URL, auth, field names); a clearly labelled demo catalog is the default | [BACKEND_INTEGRATION.md](docs/BACKEND_INTEGRATION.md) |
| Privacy | Consent, local processing, no raw recordings kept, encrypted profile/history, deletion, ONNX Runtime telemetry disabled | [PRIVACY.md](docs/PRIVACY.md) |
| Performance | Measured on the development host only; in-app benchmark ready for phones | [BENCHMARKS.md](docs/BENCHMARKS.md) |
| Tests | 414 unit + 41 integration tests passing | [TEST_RESULTS.md](docs/TEST_RESULTS.md) |

## Quick start

Prerequisites: .NET 10 SDK (`global.json` accepts 10.0.100 or a later feature band; verified with 10.0.401) with the MAUI workload (`dotnet workload install maui-android`,
plus `maui-ios` on a Mac), Android SDK and JDK 21 for Android, Xcode for iOS.

```bash
cd "BonikPOS Mobile AI"

# Tests
dotnet test --project tests/BonikPOS.VoiceSearch.UnitTests/BonikPOS.VoiceSearch.UnitTests.csproj
dotnet test --project tests/BonikPOS.VoiceSearch.IntegrationTests/BonikPOS.VoiceSearch.IntegrationTests.csproj

# Android (Debug APK in src/BonikPOS.VoiceSearch.App/bin/Debug/net10.0-android/)
dotnet build src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj -f net10.0-android -c Debug

# iOS (on a Mac)
dotnet build src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj -f net10.0-ios -c Debug

# Medicine search evaluation and benchmarks
dotnet run --project tools/BonikPOS.VoiceSearch.Evaluation -c Release -- --help
```

To recognise speech, install a Whisper export (see [MODELS.md](docs/MODELS.md)); without one the app shows that
speech recognition is not installed and typed search still works.

## Layout

```
BonikPOS.VoiceSearch.sln
src/
  BonikPOS.VoiceSearch.Core/            contracts, pipeline, enrolment, settings, telemetry (no platform code)
  BonikPOS.VoiceSearch.Audio/           capture (Android/iOS), resampling, bounded frame queue, WAV, benchmark
  BonikPOS.VoiceSearch.AI/              ONNX Runtime adapters: VAD, speaker encoders, Whisper, target-speaker extraction
  BonikPOS.VoiceSearch.MedicineSearch/  normalisation, transliteration, index, matcher, decision policy
  BonikPOS.VoiceSearch.Infrastructure/  model manifests/checksums, encrypted storage, BonikPOS API, catalog cache
  BonikPOS.VoiceSearch.Presentation/    view models (MVVM), UI services
  BonikPOS.VoiceSearch.App/             MAUI app: pages, DI composition, platform services
tests/
  BonikPOS.VoiceSearch.UnitTests/       414 tests
  BonikPOS.VoiceSearch.IntegrationTests/ 41 tests (real models, parity with Python references)
  fixtures/                             synthetic audio, reference values, random-weight test models
tools/
  BonikPOS.VoiceSearch.Evaluation/      search evaluation, synthetic catalog, pipeline/ASR/TSE benchmarks
  models/                               Python export/verification scripts (Whisper, GE2E, pGTCRN)
  ios-compile-check/                    iOS C# compile check without macOS
models/                                 bundled models (Silero VAD, GE2E) + manifest
eval/                                   labelled search datasets and measured results
docs/                                   documentation (index below)
```

## Documentation

| Document | Contents |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Projects, MVVM/DI, threading and data flow, storage |
| [MODELS.md](docs/MODELS.md) | Model inventory, tensor contracts, how to export and install models |
| [TSE_MODEL_AUDIT.md](docs/TSE_MODEL_AUDIT.md) | Target-speaker extraction model audit, recommendation, missing artefacts |
| [SEARCH_EVALUATION.md](docs/SEARCH_EVALUATION.md) | Labelled search dataset format and measured results |
| [EVALUATION_PLAN.md](docs/EVALUATION_PLAN.md) | How verification, recognition, extraction and the whole flow must be evaluated |
| [BENCHMARKS.md](docs/BENCHMARKS.md) | Performance measurements (host) and the on-device procedure |
| [TEST_RESULTS.md](docs/TEST_RESULTS.md) | Test suites, results and parity measurements |
| [PRIVACY.md](docs/PRIVACY.md) | Privacy and safety behaviour |
| [BACKEND_INTEGRATION.md](docs/BACKEND_INTEGRATION.md) | Connecting the BonikPOS product API |
| [DEPLOYMENT_ANDROID.md](docs/DEPLOYMENT_ANDROID.md) / [DEPLOYMENT_IOS.md](docs/DEPLOYMENT_IOS.md) | Build, sign, install, side-load, benchmark |
| [PHASE_REPORTS.md](docs/PHASE_REPORTS.md) | Per-phase report (files, features, commands, tests, missing assets, limitations) |

## Placing the project at `E:\Project\Running\Bonik\BonikPOS Mobile AI`

This folder is the complete project; all paths inside it are relative. Clone the repository and copy this folder to
`E:\Project\Running\Bonik\BonikPOS Mobile AI` (or clone and move it there). On Windows, Android builds work from the
command line or Visual Studio with the MAUI workload; iOS builds need a Mac (directly or paired from Visual Studio).

## Safety

Recognised medicine names are search candidates, not medical advice. The app never recommends doses or changes
prescriptions; every result must be confirmed by the owner.
