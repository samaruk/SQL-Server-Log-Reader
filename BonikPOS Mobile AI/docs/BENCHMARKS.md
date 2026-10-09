# Performance benchmarks

> **Status (2026-10-09): every number in this document was measured on the development host, an x86-64 cloud VM
> (Ubuntu 24.04, 4 logical CPUs, no GPU/NPU, ONNX Runtime 1.31 CPU execution provider). No Android phone or iPhone
> was available, so there are no device measurements yet.** Section 5 is the procedure for collecting them, and
> section 6 lists targets, which are design goals, not results. Host numbers show the relative effect of the
> engineering changes; they do not predict phone latency, battery or thermal behaviour.

## 1. What is measured and how

### 1.1 Full pipeline (`PipelineBenchmark`)

`src/BonikPOS.VoiceSearch.Audio/Benchmark/PipelineBenchmark.cs` replays 16 kHz clips through `FileAudioCapture` in
real time (10 ms callbacks) into the **same** `VoiceSearchPipeline` the app uses with the microphone: bounded frame
queue → target-speaker extraction (when a model is installed) → VAD/end-pointer → owner verification → speech
recognition → medicine search. Clips marked `enroll` build a temporary owner reference; nothing is stored.

The same class runs in two places:

| Where | Command / UI | Accelerators |
|---|---|---|
| Phone | Diagnostics → *Full pipeline benchmark* → copy Markdown / JSON | CPU only, then `Auto` (XNNPACK on Android, CoreML on iOS) |
| Desktop | `dotnet tools/BonikPOS.VoiceSearch.Evaluation/bin/Release/net10.0/BonikPOS.VoiceSearch.Evaluation.dll pipeline-bench ...` | CPU only |

| Metric | Meaning |
|---|---|
| `audio.capture_to_processed_ms` | From a frame's capture-callback timestamp until the pipeline finished processing it (queueing + extraction + VAD). |
| `pipeline.frame_processing_ms` | Processing time per 20 ms frame on the audio-processing loop. Must stay well below 20 ms or the queue fills. |
| `tse.chunk_ms` | Target-speaker extraction per frame (only when a model is active). |
| `speaker.verify_ms` | Owner verification of one utterance. |
| `asr.total_ms` (+ per-clip encoder / decoder split) | Speech recognition of one utterance. |
| `search.query_ms` | Medicine search of the recognised text. |
| `pipeline.end_of_speech_to_outcome_ms` | From the end-pointer's decision (500 ms of silence after the last word) to the outcome shown to the owner: result, ignored (not the owner) or nothing recognised. **What the owner waits after finishing speaking = 500 ms end silence + this value.** |
| `pipeline.end_of_speech_to_result_ms` | The same, for utterances that produced a search result. |
| frames captured / dropped / discontinuities | Frames dropped by the bounded queue's drop-oldest overrun policy. |
| Process CPU, peak working set, managed heap | Sampled every 200 ms; CPU is % of one core over the whole run. |
| Battery and thermal (phone only) | Android: level, `BATTERY_PROPERTY_CURRENT_NOW`, voltage and temperature from the battery broadcast, `PowerManager` thermal status (Android 10+). iOS: battery level/state, `NSProcessInfo` thermal state; iOS exposes no battery current. |
| `audio.input_latency_ms` (live microphone only) | Platform input latency of the newest sample: Android `AudioRecord.getTimestamp` (CLOCK_MONOTONIC), iOS tap buffer host time + `AVAudioSession.inputLatency`. Recorded once per second while listening; shown on the Diagnostics screen. Not part of the replay benchmark. |

### 1.2 Model compute (`asr-bench`, `tse-bench`)

- `asr-bench` (`tools/BonikPOS.VoiceSearch.Evaluation/Asr/AsrBenchmark.cs`) times Whisper log-mel, encoder, the
  start-of-transcript step, the language/task prompt step and teacher-forced single-token decoder steps
  (`WhisperOnnxRecognizer.MeasureCompute`), one process per model and thread count so that peak RSS belongs to that
  model.
- `tse-bench` (`tools/BonikPOS.VoiceSearch.Evaluation/Tse/TseBenchmark.cs`) times streaming enhancement/extraction
  graphs one 16 ms frame at a time, model only and through the C# STFT adapter.

### 1.3 Models used on the host

| Role | Model | Weights |
|---|---|---|
| VAD | Silero VAD 6.2.3 (bundled) | real |
| Owner verification | GE2E Resemblyzer 0.1.4 (bundled) | real |
| Target-speaker extraction | `tests/fixtures/tse/pgtcrn-c16-e256-random.onnx` | **random** (no trained model exists; see [TSE_MODEL_AUDIT.md](TSE_MODEL_AUDIT.md)) |
| Speech recognition | Whisper exports with the published *tiny* / *base* dimensions | **random** (official checkpoints could not be downloaded here) |

Random weights do the same arithmetic per step as trained weights of the same shape, so they are valid for timing.
Two consequences: the random recogniser outputs garbage, so owner clips end as *nothing recognised* and the search
step is not exercised in the pipeline runs (search cost is measured separately, section 2.4); and its decoder stops
after 8 repeated tokens (repetition guard), so every utterance decodes 8 tokens.

Clips: synthetic TTS (`tests/fixtures/audio/benchmark-clips.json`): voice A (owner, 2 clips for enrolment, 3 test
clips), voice B (other speaker), and A+B overlapping. Plumbing only: they say nothing about verification accuracy.

## 2. Host results (measured)

### 2.1 Pipeline scheduling: before and after

Command (from `BonikPOS Mobile AI/`, models as in 1.3, base-dimension Whisper with an 8 s window, INT8):

```bash
E="dotnet tools/BonikPOS.VoiceSearch.Evaluation/bin/Release/net10.0/BonikPOS.VoiceSearch.Evaluation.dll"
COMMON="--asr-config <dir>/shape-base-ctx400-int8-config.json --tse tests/fixtures/tse/pgtcrn-c16-e256-random.onnx --repetitions 3"
$E pipeline-bench $COMMON --no-warmup --spinning --streaming-threads 0   # baseline: previous scheduling
$E pipeline-bench $COMMON                                                # optimized default
$E pipeline-bench $COMMON --parallel                                     # + opt-in parallel verification
```

Each run: 5 test clips × 3 repetitions in real time (52–58 s); runs were interleaved
(baseline, optimized, parallel, ablations, then baseline, optimized, parallel again). Full reports with per-clip
rows: `eval/results/pipeline-bench-host-*.md` / `.json`.

| Configuration | Run | Process CPU (% of one core) | Peak RSS MB | Frames (dropped) | Capture→processed p95 / max ms | Frame processing p50 / p95 ms | TSE p50 / p95 ms | Verify p50 ms | ASR p50 ms | Owner clips: end of speech → outcome, median / max ms | Other voice: end of speech → ignored, median / max ms |
|---|---|---:|---:|---|---|---|---|---:|---:|---|---|
| Baseline | 1 | 373 | 385 | 2752 (0) | 15.1 / 34.0 | 4.8 / 15.0 | 3.2 / 13.2 | 154 | 534 | 693 / 799 | 168 / 215 |
| Baseline | 2 | 354 | 385 | 2761 (0) | 16.6 / 49.4 | 3.8 / 16.1 | 3.2 / 13.5 | 166 | 597 | 761 / 1202 | 196 / 308 |
| **Optimized (default)** | 1 | **38** | 395 | 2610 (0) | 7.3 / 29.7 | 3.6 / 7.2 | 3.3 / 6.6 | 62 | 252 | **308 / 378** | **64 / 77** |
| **Optimized (default)** | 2 | **37** | 378 | 2600 (0) | 6.8 / 19.0 | 3.3 / 6.7 | 3.0 / 6.2 | 56 | 236 | **279 / 353** | **64 / 77** |
| Optimized + parallel verification (opt-in) | 1 | 40 | 395 | 2598 (0) | 7.2 / 20.5 | 3.4 / 7.2 | 3.2 / 6.5 | 69 | 277 | 277 / 294 | 75 / 91 |
| Optimized + parallel verification (opt-in) | 2 | 39 | 383 | 2596 (0) | 6.4 / 14.4 | 3.2 / 6.3 | 2.9 / 5.8 | 66 | 286 | 286 / 329 | 64 / 88 |
| Ablation: optimized but ONNX Runtime spinning on | 1 | 53 | 392 | 2646 (0) | 6.7 / 23.6 | 3.2 / 6.6 | 3.0 / 5.9 | 56 | 321 | 407 / 487 | 56 / 73 |
| Ablation: optimized but 4-thread streaming models | 1 | 43 | 394 | 2615 (0) | 8.2 / 24.5 | 4.0 / 8.2 | 3.6 / 7.3 | 58 | 273 | 319 / 459 | 58 / 74 |

*Baseline* = the scheduling before this work: no warm-up, ONNX Runtime's default busy-waiting worker threads,
and the streaming models (VAD, extraction) on 4-thread pools. *Optimized* = warm-up, no spinning, single-threaded
streaming models on the audio-processing loop, cancellable recognition. Owner verification runs before recognition
in both (the default).

What the measurements show on this host:

- **CPU: 354–373 % → 37–38 % of one core** for the same work. The ablations attribute it: with spinning back on,
  CPU is 53 % and recognition is slower (321 ms vs 236–252 ms) because spinning pools of concurrently running models
  compete for the 4 cores; with 4-thread streaming pools (spinning off) CPU is 43 % and frame processing p95 rises
  from 6.7–7.2 to 8.2 ms. The large baseline figure needs both: spinning pools that are woken every 10–32 ms by
  the streaming models never get to sleep.
- **Latency:** owner clips end-of-speech → outcome median 693–761 ms → 279–308 ms; another voice is rejected
  64 ms after the end of speech instead of 168–196 ms.
- **No dropped frames in any run**; the microphone path never waits for inference (section 3.1).
- **Parallel verification** (opt-in) changed the owner median by −31 to +7 ms between paired runs (277/286 ms vs
  308/279 ms) — within run-to-run variation on this host — while it transcribes other people's speech before
  rejecting it. It therefore stays off by default (see [PRIVACY.md](PRIVACY.md)).
- Warm-up took 325–378 ms after model load. Its effect on the first utterance was not isolated on this host (rep 1
  of the baseline was not consistently slower than reps 2–3); one-time costs are expected to be larger on phones
  with XNNPACK/CoreML, which is why it is kept.

### 2.2 Whisper compute (`eval/results/asr-bench-host.md`)

7 runs per row after a discarded warm-up; 3 s synthetic audio; 12 teacher-forced tokens; generated 2026-10-09 09:06
UTC (load average 1.1 at start). Random weights with published dimensions.

| Model | Quant | Window | Threads | Encoder p50 ms | Per token p50 ms | Total p50 ms (12 tokens) | Peak RSS MB | Encoder / decoder file MB |
|---|---|---|---:|---:|---:|---:|---:|---|
| tiny | float | 30 s | 1 / 2 / 4 | 665 / 407 / 274 | 33.3 / 21.5 / 19.8 | 1150 / 744 / 583 | 550–554 | 36 / 108 |
| base | float | 30 s | 1 / 2 / 4 | 1650 / 949 / 598 | 72.1 / 43.4 / 32.6 | 2722 / 1602 / 1073 | 821–825 | 91 / 186 |
| base | float | 8 s (`--n-audio-ctx 400`) | 1 / 2 / 4 | 259 / 187 / 135 | 34.8 / 24.8 / 21.7 | 777 / 559 / 455 | 588–596 | 88 / 186 |
| base | int8 | 8 s | 1 / 2 / 4 | 143 / 102 / 90 | 17.1 / 15.9 / 17.1 | 433 / 357 / 358 | 353–354 | 25 / 149 |

Observations (host only): shortening the audio window from 30 s to 8 s cuts the base encoder 4–6×; dynamic INT8
quantisation (8 s window) cuts per-token time by 21–51 % (most on one thread), the encoder by 33–45 % and peak memory
by ~40 %; the INT8 decoder does not get faster with more threads (memory-bound), while the encoder does. Per-thread-count tuning per session (e.g. encoder 2–4 threads, decoder
1–2) is a candidate optimisation **not implemented**. INT8 quality was not evaluated (no real checkpoint);
quantisation must pass the ASR evaluation in [EVALUATION_PLAN.md](EVALUATION_PLAN.md) before use.

### 2.3 Target-speaker extraction compute (`eval/results/tse-bench-host.md`)

20 s of synthetic input per row, generated 2026-10-09 09:02 UTC (load average 0.9 at start). One frame = 256 new
samples = 16 ms.

| Model | Size | Mode | 1 thread p50 / p95 ms | 2 threads p50 / p95 ms | Real-time factor (1 thread, mean) |
|---|---:|---|---|---|---:|
| GTCRN (upstream streaming ONNX, denoiser) | 523 KB | model only | 1.86 / 3.24 | 3.35 / 4.15 | 0.130 |
| UL-UNAS (upstream streaming ONNX, denoiser) | 770 KB | model only | 2.33 / 4.30 | 3.39 / 5.35 | 0.167 |
| pGTCRN c16 (random) | 329 KB | model only | 1.26 / 2.01 | 1.75 / 3.81 | 0.089 |
| pGTCRN c16 (random) | 329 KB | C# adapter (STFT + model + iSTFT) | 1.35 / 2.56 | 1.39 / 2.38 | 0.096 |
| pGTCRN c32 (random) | 563 KB | model only | 1.22 / 2.15 | 2.13 / 3.60 | 0.086 |
| pGTCRN c32 (random) | 563 KB | C# adapter | 1.59 / 2.84 | 1.82 / 2.96 | 0.112 |
| pGTCRN c48 (random) | 919 KB | model only | 1.74 / 2.96 | 3.03 / 4.88 | 0.124 |
| pGTCRN c48 (random) | 919 KB | C# adapter | 1.90 / 3.47 | 2.32 / 3.85 | 0.135 |

These graphs are too small to benefit from a second thread; the app runs them single-threaded. In the full pipeline
the extractor took 2.9–3.3 ms p50 per 20 ms frame (table 2.1), including adapter overhead.

### 2.4 Medicine search

From [SEARCH_EVALUATION.md](SEARCH_EVALUATION.md) (20,000-product synthetic catalog, same host): query p50 2.21 ms,
p95 4.57 ms; index build 1,576 ms at catalog load.

## 3. Optimisations in the code (and how they were checked)

1. **The microphone callback never waits for inference.** Android reads on a dedicated `URGENT_AUDIO` thread and
   iOS uses the AVAudioEngine tap; both only convert/resample into preallocated buffers and enqueue into the bounded
   `AudioFrameQueue` (drop-oldest overrun policy with discontinuity flags). Extraction, VAD, verification and
   recognition run on other threads. Checked by unit tests (queue, continuity, cancellation) and by 0 dropped frames
   in every run above (frame processing p95 ≤ 16 ms even in the baseline).
2. **ONNX Runtime worker spinning off** (`OnnxSessionPreferences.AllowSpinning`, setting
   `InferenceThreadSpinning`, default off): measured effect in 2.1.
3. **Streaming models single-threaded** on the audio-processing loop (`ForStreaming()`, setting
   `StreamingModelThreads` = 1): measured effect in 2.1 and 2.3.
4. **Warm-up** after model load (search screen preparation; `WarmUpModels`).
5. **Cancellable recognition**: cancellation sets `RunOptions.Terminate`, so a running encoder/decoder Run stops
   between operators; verified by an integration test (cancellation at 0–13 ms always ended as cancelled and the
   next recognition gave the same result).
6. **Extraction alignment**: the STFT adapter's constant delay is the minimum for the frame size
   (hop − gcd(frame, hop)): 448 samples (28 ms) instead of 511 for 20 ms frames.
7. **Persistent sessions and reusable buffers**: sessions are created once per model load; the STFT adapter, VAD and
   recogniser reuse preallocated buffers per frame/step.
8. **Parallel verification** is available as an opt-in (section 2.1 for why it is not the default).

## 4. Limits of the host numbers

- An x86-64 VM is not a phone: no big.LITTLE scheduling, different memory bandwidth, no thermal throttling, no
  battery. Absolute values will differ; relative effects of scheduling (spinning, thread counts) are expected to
  matter more on phones, where idle CPU costs battery, but that is unverified.
- Recognition decoded 8 tokens per utterance (random weights); a real medicine query is typically a few to ~20
  tokens, so real decode time scales with the transcript length (per-token cost in 2.2).
- Synthetic TTS clips; no real shop noise, distance or overlap.
- The baseline runs started with a busy 1-minute load average from the preceding run (the benchmark itself);
  all runs were interleaved to spread drift.

## 5. On-device procedure (Android phones and iPhones)

Representative device classes to cover (choose concrete models available to the team): an entry-level Android
(4 GB RAM, Android 10–12), a mid-range Android, a recent high-end Android, an older supported iPhone and a recent
iPhone. For each device:

1. Install a **Debug** build (it packages the synthetic clips) or side-load clips: copy `clips.json`
   (format `bonikpos-benchmark-clips/1`, see `src/BonikPOS.VoiceSearch.Audio/Benchmark/BenchmarkClipManifest.cs`)
   and 16 kHz WAV files to the `benchmark` folder next to the side-load models folder (Android: the app-specific
   external files directory; iOS: Documents, visible in the Files app in Debug builds). Use consented recordings for
   meaningful verification/recognition outcomes.
2. Install the models to be compared (real ASR export, any TSE model) as described in [MODELS.md](MODELS.md).
3. Unplug the phone, charge ≥ 50 %, close other apps, note room temperature, let the phone cool to ambient.
4. Diagnostics → *Full pipeline benchmark* with *Compare CPU and accelerator* on. The screen stays on during the run.
5. Copy Markdown and JSON; store them as `eval/results/devices/<device>-<date>.md/.json` with the app version and
   model ids/hashes (the report contains no audio and no recognised text).
6. Thermal behaviour: run the benchmark 5 times back to back and record the thermal state before/after each run
   (in the report) and the battery temperature (Android).
7. Live microphone latency: start a voice search, speak a few queries, then read `audio.input_latency_ms`,
   `audio.capture_to_processed_ms` and `pipeline.end_of_speech_to_result_ms` on the Diagnostics screen.
8. Battery: on Android the report includes mean whole-device battery current and power during the run (meaningful
   only unplugged; sign/unit conventions vary by manufacturer). On iOS use Xcode's energy gauges / Instruments for
   power; the app records battery level and thermal state only.

## 6. Targets (not measurements)

From the product specification and engineering budget; to be checked with section 5 data:

| Item | Target |
|---|---|
| Audio frames | 10–20 ms (implemented: 10 ms reads on Android, 20 ms pipeline frames) |
| Frame processing (extraction + VAD) | p95 < 10 ms per 20 ms frame on the slowest supported phone; 0 dropped frames |
| Continuous audio latency (input latency + extraction delay) | ≈100–300 ms where technically feasible (extraction adds 28 ms algorithmic delay) |
| Final medicine result | Measured separately: end of speech → result; goal p50 ≤ 1 s, p95 ≤ 1.5 s on a mid-range phone (plus the 500 ms end-silence) |
| Peak memory | ≤ 500 MB with the chosen ASR model |
| Thermal | no serious/critical thermal state during 10 minutes of continuous listening |
