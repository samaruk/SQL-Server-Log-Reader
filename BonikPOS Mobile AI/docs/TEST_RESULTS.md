# Test results

All results below were produced by actually running the commands on 2026-10-09 in the development container
(Ubuntu 24.04 x86-64, 4 logical CPUs, .NET SDK 10.0.401, MAUI workload 10.0.110 for Android). Nothing was run on a
phone. Accuracy of real speech models is **not** covered here because no trained recognition or extraction model was
available (see [EVALUATION_PLAN.md](EVALUATION_PLAN.md)).

## 1. Commands

From `BonikPOS Mobile AI/` with `DOTNET_ROOT`, `ANDROID_HOME` and `JAVA_HOME` set:

```bash
dotnet build tests/BonikPOS.VoiceSearch.UnitTests/BonikPOS.VoiceSearch.UnitTests.csproj -c Release
dotnet build tests/BonikPOS.VoiceSearch.IntegrationTests/BonikPOS.VoiceSearch.IntegrationTests.csproj -c Release
tests/BonikPOS.VoiceSearch.UnitTests/bin/Release/net10.0/BonikPOS.VoiceSearch.UnitTests
tests/BonikPOS.VoiceSearch.IntegrationTests/bin/Release/net10.0/BonikPOS.VoiceSearch.IntegrationTests
# or: dotnet test --project tests/<project>/<project>.csproj

dotnet build src/BonikPOS.VoiceSearch.App/BonikPOS.VoiceSearch.App.csproj -f net10.0-android -c Debug
tools/ios-compile-check/ios-compile-check.sh
```

The integration tests locate `models/` and `tests/fixtures/` by walking up from the test binary
(`BONIKPOS_SOLUTION_ROOT` overrides this).

## 2. Results

| Suite / build | Result |
|---|---|
| Unit tests (`BonikPOS.VoiceSearch.UnitTests`) | **414 passed, 0 failed, 0 skipped** (≈2.6 s) |
| Integration tests (`BonikPOS.VoiceSearch.IntegrationTests`) | **41 passed, 0 failed, 0 skipped** (≈3 s) |
| Repeated runs (flakiness check) | Unit suite 16 consecutive green runs and integration suite 8 (by the test author); integration suite 3 more after the latest additions |
| Android Debug build (`net10.0-android`) | **Succeeded, 0 warnings, 0 errors** (warnings are errors); APK ≈51 MB with arm64-v8a and x86_64 ONNX Runtime libraries; merged manifest without the ONNX Runtime telemetry provider |
| iOS | **Not built** (needs macOS + Xcode). `tools/ios-compile-check/ios-compile-check.sh` compiled the iOS-only C# (audio capture, platform services) against the Microsoft.iOS 27 reference assembly and the MAUI 10.0.110 iOS libraries: **passed** |
| Evaluation tool (`tools/BonikPOS.VoiceSearch.Evaluation`) | Builds and runs (search evaluation, benchmarks) |

## 3. What the automated tests cover

Mapped to the testing list in the product specification:

| Required area | Tests |
|---|---|
| Audio buffering and cancellation | `Audio/AudioFrameQueueTests.cs` (bounded queue, drop-oldest overrun, discontinuity flags, cancellation while waiting, completion, concurrent producer/consumer), `Audio/FileAudioCaptureTests.cs` (stop mid-stream, real-time pacing), pipeline start/stop/race tests in `Pipeline/VoiceSearchPipelineTests.cs` |
| Model adapter input validation | `AI/TensorContractTests.cs`, `AI/StreamingTseAdapterTests.cs`, `AI/VoiceModelRuntimeTests.cs`, `Infrastructure/FileSystemModelProviderTests.cs` (manifests, checksums, path containment, test-fixture refusal) |
| Audio stream continuity | `Audio/CaptureProcessorTests.cs` (exact re-framing, int16 conversion, channel 0, chunked resampling identical to one-shot, a full queue drops oldest instead of blocking the callback), `Pipeline/UtteranceSegmenterTests.cs`, overrun → discontinuity → VAD/extractor reset in `VoiceSearchPipelineTests`, integration `PgtcrnStftAdapterTests` (continuous output when the block size changes) |
| Text normalisation | `MedicineSearch/TextProcessingTests.cs`, `MedicineSearch/QueryParserTests.cs` (Bangla digits and number words, units, transliteration, Banglish) |
| Medicine ranking | `MedicineSearch/RankingTests.cs`, `MedicineSearch/MatcherRegressionTests.cs` (one case per defect found in the search evaluation) |
| Strength and dosage-form disambiguation | `RankingTests` (ambiguous strengths, mismatches), `MatcherRegressionTests` (form named in the catalog entry, cream vs ointment), `SearchDecisionPolicyTests` |
| No-match handling | `RankingTests` (no-match suggestions), `MatcherRegressionTests` (everyday conversation and missing brands never become a product) |
| Owner-profile versioning | `Profiles/OwnerProfileTests.cs` (encoder id/version/checksum/dimension compatibility, re-enrolment after an encoder change, corruption), `Infrastructure/EncryptionTests.cs` (AES-GCM envelope, tamper detection, purpose binding, crypto-shredding on delete) |
| API error handling | `Infrastructure/BonikPosApiCatalogSourceTests.cs` (paging and field mapping, auth header, client errors, server-error retries with backoff, `Retry-After`, invalid JSON, timeouts, network failures, caller cancellation, HTTPS required except loopback) |
| Pipeline behaviour | `Pipeline/VoiceSearchPipelineTests.cs`: owner / not-owner / uncertain / insufficient-speech / no-profile / no-model outcomes, decision caps, opt-in parallel verification (another voice's started recognition is cancelled; off by default), continuous and single-query modes, listen timeout, capture overrun |
| View models | `Presentation/ViewModelTests.cs` |

## 4. Reference parity (integration tests; values printed by the tests)

These prove that the C# feature extraction and model plumbing reproduce the Python reference implementations. They
say nothing about recognition or verification accuracy.

| Component | Measured difference vs Python reference |
|---|---|
| Silero VAD | 0.0 (bit-identical) |
| GE2E speaker embedding | cosine 1.0000000; elements ≤ 3.1e-7; GE2E mel 1.9e-6 |
| Mel filter banks | ≤ 1.9e-9 |
| Kaldi fbank (alternative encoder front-end) | median 3.8e-6, max 8.8e-4 (weak low-frequency bins) |
| Whisper random-weight fixture | log-mel 3.6e-7; log-probabilities 2.2e-6; KV-cached decoder vs full-sequence PyTorch decoder 7.6e-6; greedy tokens identical |
| pGTCRN target-speaker-extraction adapter (random-weight fixture) | 3.3e-7 for 256-, 320- and 160-sample blocks |
| TSE plumbing graph (arithmetic fixture) | 1.2e-7 direct, 1.7e-6 through the adapter |

End-to-end pipeline integration test (real VAD and GE2E, synthetic TTS clips, a recogniser explicitly named STUB):
enrolment on two clips of voice A gave consistency 0.932 and an accept threshold of 0.860; a held-out clip of voice A
was accepted as the owner (0.934) and its stub transcript found the expected demo product; two clips of voice B were
rejected (0.577 and 0.583). This checks the plumbing only (synthetic voices, three speakers).

## 5. Defects found by testing and evaluation (all fixed, each with a test)

| Defect | Fix |
|---|---|
| Low recognition confidence did not cap `Ambiguous`; unconfirmed speakers could still get `Confident` | `SearchDecisionPolicy` caps both |
| Malformed streaming-TSE manifests crashed at run time or failed all model loading | validated as `ModelContractException` |
| Any unexpected model-load exception failed every model | only the affected component becomes unavailable |
| Manifest entries without `sha256` threw (source-generated JSON sets missing members to null) | defaults restored |
| `../models-evil/x.onnx` passed the "inside the models folder" check | separator-aware containment check |
| Partial `settings.json` loaded `RequireOwnerMatch = false` | stored JSON overlaid on defaults |
| Pipeline start/stop race left it stuck in Listening/Stopping (3 in 500 sessions) | capture loop starts after registration |
| Parallel verification transcribed other speakers, contradicting the documented `RequireOwnerMatch` contract | made opt-in, off by default, with a disclosure |
| STFT adapter used a larger-than-needed delay and left a gap when the block size changed | minimal constant delay, delay grows without a gap |
| Benchmark tool resolved INT8 Whisper exports to their float files | reads the files named by the export config |
| TSE benchmark's adapter rows labelled "2 threads" ran on one thread | thread count passed to the streaming policy; table re-measured |
| A random-weight TSE or Whisper export without the manifest's test-fixture flag would have loaded (and extraction would have looked active) | the runtime also reads the export's own marker (`bonik.weights` metadata / Whisper config source) and refuses untrained models |
| A timeout or dropped connection while reading the API response body escaped as a silent cancellation (no retry, no cache fallback) | retried, then reported as a catalog error so the cache is used |
| A server that ignores the page parameter caused 2000 requests | paging stops at the first page that only repeats products, with a warning; a short first page without `hasMore` now warns |
| "Delete all app data" missed an imported product index (copied outside the private data folder) | imported into the private, backup-excluded data folder that is deleted |
| Matcher defects found on labelled data (conversation matched to brands, sound-alike over exact name, partial name over fuller name, …) | see [SEARCH_EVALUATION.md](SEARCH_EVALUATION.md); covered by `MatcherRegressionTests` |

## 6. Evaluations and benchmarks (not unit tests)

- Medicine search evaluation (labelled datasets, top-1/top-5/false matches): [SEARCH_EVALUATION.md](SEARCH_EVALUATION.md).
- Pipeline, ASR and TSE performance on the development host: [BENCHMARKS.md](BENCHMARKS.md).

## 7. Not covered

- Native microphone capture on real Android/iOS hardware (code compiles; behaviour must be checked on devices).
- MAUI UI automation (view models are unit-tested; pages were not driven by a UI test framework).
- Recognition accuracy (WER), verification accuracy (FAR/FRR) and extraction quality: require trained models and
  consented recordings ([EVALUATION_PLAN.md](EVALUATION_PLAN.md)).
- iOS build, signing and device run.
