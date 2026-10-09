# On-device models

This document covers the models the app runs with ONNX Runtime 1.31.0 on the phone. It lists which of them ship in
the app package and the tensor contracts that the C# adapters check when a model loads. It also explains how to add
or side-load a model and what has been measured so far. Status: 2026-10-09. Measured numbers come from files in this
repository, and the source of each one is named.

> **Status.** Two models ship: **Silero VAD 6.2.3** (voice activity) and **GE2E / Resemblyzer 0.1.4** (owner
> verification). **No speech-recognition model and no target-speaker-extraction (TSE) model is bundled.** The official
> Whisper checkpoints could not be downloaded in the development environment because the download host was blocked.
> No trained TSE model exists. The app reports both components as unavailable and never substitutes a fake. Every
> Whisper and TSE graph in the repository has random or arithmetic weights and is a test fixture.

## 1. Inventory

### 1.1 Models for the app

| Role (`role`) | Model (`id` @ `version`) | Format (`format`) | Licence | Source | Shipped in the app | Verified by |
|---|---|---|---|---|---|---|
| Voice activity (`VoiceActivity`) | `silero-vad` @ 6.2.3 | `silero-vad` | MIT | PyPI `silero-vad` 6.2.3, `silero_vad/data/silero_vad_openvino_16k.onnx`. This is the fixed-shape 16 kHz graph, with outputs identical to `silero_vad.onnx`. `tools/models/make_reference_fixtures.py` copies it unchanged. | **Yes**: `models/vad/silero-vad-6.2.3-16k.onnx` (1,288,203 bytes) | SHA-256 pinned in `models/models.json`. Streaming probabilities are bit-identical to the official Python wrapper (§5). |
| Speaker encoder (`SpeakerEncoder`) | `ge2e-resemblyzer` @ 0.1.4 | `ge2e-onnx` | Apache-2.0 | PyPI `Resemblyzer` 0.1.4, `resemblyzer/pretrained.pt` (SHA-256 `39373b86598fa3da9fcddee6142382efe09777e8d37dc9c0561f41f0070f134e`), exported by `tools/models/export_ge2e_onnx.py` | **Yes**: `models/speaker/ge2e-resemblyzer-0.1.4.onnx` (5,700,576 bytes) | SHA-256 pinned. The export is validated against PyTorch, and C# utterance embeddings match Resemblyzer (§5). |
| Speech recognition (`SpeechRecognition`) | none | `bonikpos-whisper-onnx/1` | MIT for OpenAI Whisper weights; a fine-tune carries its own licence | Must be exported with `tools/models/export_whisper_onnx.py` (§4.1) | **No** (`models/asr/` is git-ignored) | Adapter and decoding parity on a random-weight fixture only. No recognition accuracy has been measured. |
| Target-speaker extraction (`TargetSpeakerExtraction`) | none (no trained model exists) | `streaming-tse-onnx/1`, `streaming-tse-stft-onnx/1` | — | Candidate architecture pGTCRN in `tools/models/tse/` (derived from GTCRN, MIT), untrained. The audit and training plan are in [TSE_MODEL_AUDIT.md](TSE_MODEL_AUDIT.md). | **No** (`models/tse/` is git-ignored) | Adapter parity on random-weight and arithmetic fixtures only. No extraction quality has been measured. |
| Speaker encoder, alternative format | none | `fbank-speaker-onnx` | — | Adapter for encoders that take Kaldi fbank features, such as WeSpeaker and 3D-Speaker exports. No such model was integrated. | No (`models/speaker-extra/` is git-ignored) | Kaldi fbank front-end parity (§5). The adapter is exercised only with a generated arithmetic graph in unit tests. |

### 1.2 Test fixtures and benchmark-only exports (never loaded by the app)

| Files | What they are | Used for |
|---|---|---|
| `tests/fixtures/whisper-test-tiny/` (`-config.json`, `-encoder.onnx`, `-decoder.onnx`, `-tokens.txt`, `-reference.json`) | Whisper with **random weights**, from `export_whisper_onnx.py --random-preset test-tiny` with seed 20261009. It has 2 encoder and 2 decoder layers of width 32, the real 51,865-token multilingual vocabulary and `n_audio_ctx` 150 (a 3 s window). Config `source`: `RANDOM WEIGHTS preset=test-tiny seed=20261009 - test fixture, not a speech model`. | Front-end, tensor-plumbing and decoding parity (`tests/BonikPOS.VoiceSearch.IntegrationTests/Whisper/WhisperFixtureTests.cs`) |
| Compute-shape exports `shape-tiny` and `shape-base`, plus `shape-base` with `--n-audio-ctx 400` in float and int8. Not committed. | Random weights with the published Whisper tiny and base dimensions. Config `source`: `RANDOM WEIGHTS preset=shape-… seed=… - COMPUTE BENCHMARK ONLY (published … dimensions), not a speech model`. The output text is meaningless. | Timing and memory only (§6) |
| `tests/fixtures/tse/pgtcrn-c16-e256-random.onnx` (+ `pgtcrn-fixture-*.f32`, `README.json`) | pGTCRN with 16 channels and FiLM conditioning on a 256-dim embedding, with **random, untrained weights** (SHA-256 `fd26f859302fe5605b5b6e671d4c3eb16908a7339d18b9af63aa44a80bce2f89`). It extracts no speaker. | `streaming-tse-stft-onnx/1` contract and C# STFT-adapter parity |
| `tests/fixtures/reference/tse-plumbing-fixture.onnx` | Arithmetic graph with the tensor layout of a streaming TSE model (320-sample chunks, 256-dim embedding, 8-value state). It is not a speech model. | `streaming-tse-onnx/1` chunking, state carry-over and embedding binding |

## 2. How the app finds and verifies models

### 2.1 Roots

| Root (search order) | Location on the device | Builds |
|---|---|---|
| 1. Side-load folder | **Android**: `getExternalFilesDir(null)/models`, normally `/sdcard/Android/data/com.bonikpos.voicesearch/files/models/` (it falls back to `<app data>/models` when no external files directory exists). **iOS**: `Documents/models`, which the Files app shows only in Debug builds (`Platforms/iOS/DebugFileSharing.plist`). | **Debug only** (`#if DEBUG`) |
| 2. Bundled models | `<FileSystem.AppDataDirectory>/models-bundled`, extracted from the app package | All builds |

The code is in `MauiAppPaths` and `BootstrappingModelProvider` (`src/BonikPOS.VoiceSearch.App/Services/MauiPlatformServices.cs`).
**Release builds load only bundled models**, so a Release build that needs speech recognition must bundle the model
(§4.3).

How bundling works:

- The App project packages every `*.onnx`, `*.json` and `*.txt` file under the solution's `models/` folder as MAUI
  assets named `models/...`. On Android these become `assets/models/...`.
- At app start, before any model is resolved, `BundledModelExtractor` copies the files listed in the bundled
  `models.json` into `models-bundled`. It skips entries marked `isTestFixture` and hashes each file while copying.
  ONNX Runtime loads models from file paths, which is why the copy is needed.
- Files already present with the right hash are not copied again. In practice, copying happens on first launch and
  after an update.
- If a copied file does not match its SHA-256, it is deleted and extraction stops with an error, logged as
  "Extracting bundled models failed".

### 2.2 Manifest (`models.json`, format `bonikpos-models/1`)

```json
{ "format": "bonikpos-models/1", "models": [ { "id": "…", "version": "…", "role": "…", "format": "…", "files": { }, "sha256": { } } ] }
```

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Model id. For a speaker encoder, the id is also stored in the owner profile, and TSE models refer to it through `required_encoder_id`. |
| `version` | yes | Model version (shown in Diagnostics; stored in the owner profile for speaker encoders). |
| `role` | yes | `VoiceActivity`, `SpeakerEncoder`, `TargetSpeakerExtraction` or `SpeechRecognition`. |
| `format` | yes | Adapter format (§3). |
| `files` | yes | Logical file name → path relative to the manifest's folder. |
| `sha256` | no, but always set it | Relative path → lowercase hex SHA-256. |
| `license`, `source` | no | Shown in Diagnostics; default `unknown`. |
| `isTestFixture` | no | `true` for random-weight or arithmetic graphs. The app never loads them (§2.3). |
| `options` | per format | Adapter options (§3). |

Rules enforced by `src/BonikPOS.VoiceSearch.Infrastructure/Models/FileSystemModelProvider.cs`:

- A manifest that is not valid JSON, or whose `format` is not `bonikpos-models/1`, is ignored and the reason is logged.
- A path that resolves outside the manifest's folder, such as `../x.onnx` or the sibling `../models-evil/x.onnx`,
  makes the entry `InvalidManifest`.
- A missing file makes the entry `NotInstalled`. A file whose SHA-256 differs from the pinned value makes it
  `ChecksumMismatch`, and such a file is never handed to an adapter.
- An entry without a pinned checksum is accepted, and its status says "no checksum pinned in the manifest".
  `register_model.py` always pins checksums.
- For each role, the **first** entry that passes is used, in root order (side-load, then bundled) and then in
  manifest order. A broken entry falls through to the next one.
- Hashes are cached in `<FileSystem.CacheDirectory>/bonikpos/model-checksums.json`, keyed by path, size and
  last-write time. A file is hashed again when its size or timestamp changes.
- After the file checks, the adapter validates the graph (§3). A contract violation is reported as
  `IncompatibleContract` and any other load error as `LoadFailed`. Only that component becomes unavailable; the other
  models still load.
- Diagnostics → *Models* shows, for each role: state, execution provider, init time, id, version, licence and the
  provider's message. *Reload models* reads the roots again.

### 2.3 Test fixtures are refused

`OnnxVoiceModelRuntime` refuses any entry marked `"isTestFixture": true` unless the runtime is constructed with
`allowTestFixtures: true`. Only tests and the evaluation tool's benchmarks do that. In the app the role is reported as
"model not installed (only a test fixture is present)".

The flag is not the only safeguard: the app also refuses models that declare untrained weights themselves, even when a
manifest omits the flag — a target-speaker-extraction export whose ONNX metadata `bonik.weights` is anything but
`trained:<sha256>` (`export_pgtcrn_onnx.py` writes `RANDOM-UNTRAINED …` or `SMOKE-TEST …` otherwise), and a Whisper
export whose config `source` starts with `RANDOM WEIGHTS`. Such a role is reported as unavailable with the reason
(tests `UntrainedGraph_IsRefusedByTheApp_EvenWhenTheManifestDoesNotFlagIt`,
`RandomWeightExport_IsRefused_EvenWhenTheManifestDoesNotFlagIt`).

The tools set or enforce the flag:

- `export_whisper_onnx.py --register` sets it for every `--random-preset` export.
- `register_model.py --test-fixture` sets it explicitly.
- `export_pgtcrn_onnx.py --register` refuses random weights and smoke-test checkpoints.

Do not put fixture entries in a side-load manifest. The runtime receives the first verified entry for a role, and a
fixture is refused rather than skipped. A side-loaded fixture therefore disables that role even when the bundled model
is fine.

Separately, any Whisper config whose `source` starts with `RANDOM WEIGHTS` is labelled
"RANDOM-WEIGHT TEST MODEL - output is meaningless" wherever it is loaded.

## 3. Tensor contracts

`src/BonikPOS.VoiceSearch.AI/Onnx/TensorContract.cs` reads each model's actual inputs and outputs from the ONNX graph
when it loads and applies these rules:

- Each named input and output must exist with the stated element type.
- When a shape is listed, the rank must match.
- A positive dimension must equal the model's dimension unless the model declares that axis dynamic. `any` means the
  axis is not checked.
- Inputs that the adapter does not feed make the model incompatible. This applies to every format below.

Execution providers (`src/BonikPOS.VoiceSearch.AI/Onnx/OnnxSessionFactory.cs`):

| Session | Platform accelerator tried when *Processing* is *Automatic* | Thread setting |
|---|---|---|
| Silero VAD | no, always CPU | streaming models (default 1 thread) |
| Speaker encoder (both formats) | no, always CPU | per-utterance models |
| Whisper encoder and decoder | yes | per-utterance models |
| TSE (both formats) | yes | streaming models (default 1 thread) |

How the accelerator is used:

- On Android the accelerator is XNNPACK. Its `intra_op_num_threads` is set to the session's thread count, and ONNX
  Runtime's own pool is reduced to 1 thread.
- On iOS it is CoreML, with `ModelFormat=MLProgram` and `MLComputeUnits=ALL`.
- If the provider is missing or session creation throws, the session is created on the CPU and a warning is logged:
  "`<provider>` unavailable for `<file>`; using CPU".
- A provider that accepts a session may still leave unsupported nodes to the CPU. Diagnostics shows the provider that
  was registered for the session.
- For speech recognition, Diagnostics shows only the encoder's provider. A fallback of the decoder is visible only in
  the log.
- *Settings → Processing → CPU only* turns accelerators off.

Common session options:

- `ORT_ENABLE_ALL` graph optimisation, sequential execution and 1 inter-op thread.
- Intra-op threads come from the stored settings `InferenceThreads` (per-utterance models; 0 means min(4, cores)) and
  `StreamingModelThreads` (default 1; 0 means the same as `InferenceThreads`). Neither is shown in the UI.
- Worker spinning is off by default (`session.intra_op.allow_spinning=0`, setting `InferenceThreadSpinning`).

### 3.1 Voice activity: `silero-vad`

Files: `model`. No options. The runtime loads every `VoiceActivity` entry with this adapter and does not check the
`format` value.

| Direction | Name | Type | Shape checked | Notes |
|---|---|---|---|---|
| in | `input` | float32 | [any, any]; a static width must be 576 | 64 context samples (the tail of the previous input, as in the official `OnnxWrapper`) followed by 512 new samples at 16 kHz (32 ms) |
| in | `state` | float32 | [2, any, 128] | Recurrent state, carried from one chunk to the next |
| in, optional | `sr` | int64 | not checked | Scalar 16000 when the input exists |
| out | `output` | float32 | not checked; read as [1, 1] | Speech probability |
| out | `stateN` | float32 | not checked; read as [2, 1, 128] | Next state |

### 3.2 Speaker encoder: `ge2e-onnx`

Files: `model`.

| Direction | Name | Type | Shape checked | Notes |
|---|---|---|---|---|
| in | `options.input` (default `mels`) | float32 | [any, any, `n_mels`] | Partials × frames × mel bins. Power mel, not log: 16 kHz, `n_fft` 400, hop 160, Slaney filters, Hann window, centred with zero padding |
| out | `options.output` (default `embedding`) | float32 | [any, `embedding_dim`] | One embedding per partial |

Options, with defaults: `embedding_dim` (**required**), `input` `mels`, `output` `embedding`, `sample_rate` 16000,
`n_fft` 400, `hop_length` 160, `n_mels` 40, `partial_frames` 160, `partials_per_second` 1.3, `min_coverage` 0.75,
`normalize_dbfs` −30, `min_speech_seconds` 0.8, and `verification` with `default_accept`, `min_accept`, `max_accept`,
`enroll_min_consistency` and `single_speaker_min_window`.

Processing is a port of Resemblyzer's `embed_utterance`:

- Quiet input is raised to `normalize_dbfs` (never lowered; 0 disables this).
- Partials are 160 frames long and start every round(16000 / 1.3 / 160) = 77 frames.
- The last partial is dropped when real audio covers less than 75 % of it, unless it is the only partial.
- The embedding is the mean of the partials, L2-normalised.

The bundled entry's verification thresholds are marked **PROVISIONAL**: `default_accept` 0.75, accept range
0.68–0.86, `enroll_min_consistency` 0.70 and `single_speaker_min_window` 0.65. They have not been calibrated on real
voices; see [EVALUATION_PLAN.md](EVALUATION_PLAN.md) §2.

An owner profile records the encoder's id, version, pinned SHA-256 and dimension. If any of them changes, the profile
becomes incompatible and the owner must enroll again.

### 3.3 Speaker encoder: `fbank-speaker-onnx` (no model shipped)

Files: `model`.

| Direction | Name | Type | Shape checked | Notes |
|---|---|---|---|---|
| in | `options.input` (default `feats`) | float32 | [any, any, `num_bins`] | Fed as [1, frames, `num_bins`]: Kaldi fbank with 25 ms / 10 ms frames and optional per-utterance mean subtraction |
| out | `options.output` (default `embs`) | float32 | [any, `embedding_dim`] | |

Options, with defaults: `embedding_dim` (**required**), `num_bins` 80, `input_scale` 32768, `subtract_mean` true,
`window` `povey` (or `hamming`, `hanning`), `preemph` 0.97, `remove_dc_offset` true, `low_freq` 20, `high_freq` 0,
`sample_rate` 16000, `window_seconds` 1.5, `min_speech_seconds` 0.8, and `verification` as in §3.2.

`input_scale` 32768 is the int16 range that WeSpeaker-style exports expect; 3D-Speaker-style exports expect 1.

### 3.4 Speech recognition: `bonikpos-whisper-onnx/1`

Files: `config`, `encoder`, `decoder` and `tokens`. All four are required.

The config is `<prefix>-config.json`, written by the export script. When the model loads:

- `format` must be `bonikpos-whisper-onnx/1`.
- `features.sample_rate`, `n_fft` and `hop_length` must be 16000, 400 and 160.
- These values are read:
  - `features.n_mels` (80 or 128) and `features.n_frames` (= 2 × `n_audio_ctx`).
  - `dims.n_audio_ctx_exported`, `dims.n_text_layer`, `dims.n_text_state`, `dims.n_text_ctx` and `dims.n_vocab`.
  - `special_tokens.eot`, `sot`, `no_speech`, `no_timestamps`, `transcribe` and `timestamp_begin`.
  - `languages` (code → token id), `suppress_tokens` and `suppress_at_begin`.
  - `model_id`, `license`, `source` and `quantization`.

The tokens file is `<prefix>-tokens.txt`, with one line per token in the form `<id> <base64 of the token's bytes>`. It
must have exactly `n_vocab` lines.

| Graph | Direction | Name | Type | Shape checked |
|---|---|---|---|---|
| encoder | in | `mel` | float32 | [1, `n_mels`, `n_frames`] |
| encoder | out | `cross_k`, `cross_v` | float32 | [`n_text_layer`, 1, `n_audio_ctx`, `n_text_state`] |
| decoder | in | `tokens` | int64 | [1, any] |
| decoder | in | `past_k`, `past_v` | float32 | [`n_text_layer`, 1, any, `n_text_state`] (0 past positions at the first step) |
| decoder | in | `cross_k`, `cross_v` | float32 | [`n_text_layer`, 1, `n_audio_ctx`, `n_text_state`] |
| decoder | out | `logits` | float32 | [1, `n_vocab`] (last position only) |
| decoder | out | `present_k`, `present_v` | float32 | [`n_text_layer`, 1, any, `n_text_state`] |

Decoding in `WhisperOnnxRecognizer` mirrors openai-whisper:

- Decoding is greedy with a self-attention KV cache. The encoder computes the cross-attention keys and values once per
  utterance.
- The start-of-transcript step gives the language logits and the no-speech probability. The decoder is then prompted
  with `<|lang|><|transcribe|><|notimestamps|>`.
- Timestamps, non-speech tokens and task tokens are suppressed throughout. Blank and end-of-text are suppressed at the
  first step.
- The language is Bangla, English, or chosen automatically between the two. In automatic mode, when the choice is
  uncertain, the other language can be decoded too as an alternative, from the same encoder output.
- At most min(max tokens, `n_text_ctx` / 2) tokens are decoded, and decoding stops after 8 identical tokens.
- The text is discarded when the no-speech probability is above 0.6 and the average log-probability is below −1.

Audio longer than `n_frames` × 160 samples is truncated and flagged. With `--n-audio-ctx` 1500 (the default) the
window is 30 s. With 400 it is 8 s, which equals the pipeline's default maximum utterance length (8000 ms). The test
fixture's 150 gives 3 s.

Use a multilingual checkpoint, because the app recognises Bangla. Only the multilingual vocabulary (51,865 tokens) has
been exercised.

### 3.5 Integrating a target-speaker extraction model

No trained TSE model exists. [TSE_MODEL_AUDIT.md](TSE_MODEL_AUDIT.md) reviews published models and gives the pGTCRN
training plan. Without a model, the search screen shows "Owner-voice extraction not installed - other voices are NOT
removed".

Requirements common to both formats:

- 16 kHz only. A `sample_rate` other than 16000 is rejected.
- `required_encoder_id` (**required**) must equal the `id` of the installed speaker encoder, which today is
  `ge2e-resemblyzer`. Otherwise the TSE model is refused as `IncompatibleContract`.
- `embedding_dim` (**required**) must match the embedding input.
- The owner's profile must contain an embedding from that encoder with that dimension. Without one, the extractor
  passes audio through unchanged and reports that re-enrollment is needed. Unprocessed audio is never presented as
  extracted.
- An error while running switches that extractor off: audio passes through and the status is "failed".
- Files: `model`.
- The accelerator is allowed, and the streaming thread setting applies (default 1).
- State tensors need static shapes. They are carried from each call to the next in preallocated buffers.

**`streaming-tse-onnx/1`** (time domain, one chunk per call):

| Direction | Name (option, default) | Type | Shape checked |
|---|---|---|---|
| in | `audio_input` (`mixture`) | float32 | [1, any]; a static width must equal `chunk_samples` |
| in | `embedding_input` (`spk_emb`) | float32 | [1, `embedding_dim`] |
| in / out | `states[i].input` / `states[i].output` | float32 | The graph's static shape, or `states[i].shape`, which is required when the graph declares a dynamic axis |
| out | `audio_output` (`enhanced`) | float32 | [1, `chunk_samples`] |

Options:

| Option | Default | Notes |
|---|---|---|
| `chunk_samples` | **required** | Must be > 0 |
| `embedding_dim` | **required** | Must be > 0 |
| `required_encoder_id` | **required** | |
| `audio_input`, `embedding_input`, `audio_output` | as in the table above | Tensor names |
| `states` | none | Array of `{"input", "output", "shape"?}` |
| `algorithmic_latency_samples` | 0 | Reported in the status |
| `sample_rate` | 16000 | No other value is accepted |

Output is delayed by 0 samples when the pipeline's block size is a multiple of `chunk_samples`. Otherwise it is
delayed by `chunk_samples − 1` samples. The status reports this delay plus the declared
`algorithmic_latency_samples`.

**`streaming-tse-stft-onnx/1`** (one STFT frame per call, for example pGTCRN):

| Direction | Name (option, default) | Type | Shape checked |
|---|---|---|---|
| in | `spec_input` (`mix`) | float32 | [1, `n_fft`/2 + 1, 1, 2]: real and imaginary part of one frame |
| in | `embedding_input` (`spk_emb`) | float32 | [1, `embedding_dim`] |
| in / out | states | float32 | Static shapes; each output has the shape of its input |
| out | `spec_output` (`enh`) | float32 | [1, `n_fft`/2 + 1, 1, 2] |

Options:

| Option | Default | Notes |
|---|---|---|
| `embedding_dim` | **required** | |
| `required_encoder_id` | **required** | |
| `n_fft` | 512 | |
| `hop` | 256 | Must equal `n_fft` / 2 |
| `window` | `sqrt_hann_periodic` | The only supported window |
| `sample_rate` | 16000 | No other value is accepted |
| `spec_input`, `spec_output`, `embedding_input` | as in the table above | Tensor names |
| `states` | every other input `X` paired with output `X_out` | Array of `{"input", "output"}` |

The adapter computes the STFT (periodic square-root Hann window), then the inverse FFT and overlap-add. Its delay is
`n_fft − hop` (256 samples = 16 ms) plus a constant alignment of `hop − gcd(block, hop)` samples. For the pipeline's
20 ms (320-sample) frames the total is 448 samples (28 ms).

To register a trained pGTCRN, use the standard tooling (no trained checkpoint exists, so this has not been run):

```bash
python tools/models/tse/export_pgtcrn_onnx.py --export models/tse/pgtcrn-owner.onnx --checkpoint <trained checkpoint> \
    --channels <C> --emb-dim 256 --register models/models.json --license "<verified licence>" --source "<data and recipe>"
```

The script refuses `--register` without `--checkpoint`, `--license` and `--source`, and refuses smoke-test
checkpoints. It registers format `streaming-tse-stft-onnx/1` with `required_encoder_id` taken from `--encoder-id`
(default `ge2e-resemblyzer`). For other models, use `tools/models/register_model.py`; its docstring has a
`streaming-tse-onnx/1` example.

## 4. Adding a model

### 4.1 Export a Whisper model

The tools need the Python packages they import: numpy, onnx, onnxruntime, torch, openai-whisper, soundfile, and
transformers for `--hf-model`. No requirements file pins their versions.

```bash
cd tools/models
python export_whisper_onnx.py --model base --n-audio-ctx 400 --quantize int8 \
    --out-dir ../../models/asr --prefix whisper-base-ctx400-int8 --register ../../models/models.json
```

| Option | Meaning |
|---|---|
| `--model <name>` | Official OpenAI weights through openai-whisper, licensed MIT. The script downloads from openaipublic.azureedge.net and checks the SHA-256 embedded in the URL. **That host was blocked in this environment** ([PHASE_REPORTS.md](PHASE_REPORTS.md) lists the blocked hosts). |
| `--checkpoint file.pt` | An OpenAI-format checkpoint already on disk. |
| `--hf-model <hub id or dir>` | A Hugging Face Whisper fine-tune, for example a Bangla model. The script does not verify the licence; the config records "CHECK THE MODEL CARD". `selftest_whisper_hf_conversion.py` checks the parameter mapping on a small random model and downloads nothing. |
| `--random-preset test-tiny\|shape-tiny\|shape-base\|shape-small` | Random weights, for tests and timing only. `--register` marks the result as a test fixture. |
| `--out-dir`, `--prefix` | Required. Output folder and file-name prefix. |
| `--n-audio-ctx N` | Audio context = frames / 2. The default 0 keeps the model's 1500 (30 s). A smaller value is faster for short queries but **must be re-evaluated for accuracy**. |
| `--quantize none\|int8` | `int8` applies ONNX Runtime dynamic quantisation (QInt8 weights of MatMul and Gemm). It writes `<prefix>-encoder.int8.onnx` and `<prefix>-decoder.int8.onnx`, which are the graphs the config names. The float graphs beside them are intermediate files. |
| `--opset` (17), `--seed` (20261009), `--max-tokens` (24) | Export settings. `--max-tokens` limits decoding during validation. |
| `--fixture-wav <16 kHz mono wav>` | Validation audio. Also writes `<prefix>-reference.json`. |
| `--register <models.json>` | Adds the export to that manifest (§4.2) if validation passed. Paths are stored relative to the manifest. |

The export writes:

- `<prefix>-encoder.onnx` and `<prefix>-decoder.onnx`.
- `<prefix>-tokens.txt`.
- `<prefix>-config.json`, containing the dimensions, special tokens, languages, suppress lists, feature parameters,
  provenance, the SHA-256 of the graphs and tokens, and the validation results.
- ONNX metadata entries `bonik.*` in each graph.

Validation runs on every export:

1. The encoder is compared with PyTorch. The comparison uses `--fixture-wav`, or a 2 s 220 Hz tone if none is given.
2. The KV-cached decoder is compared with the full-sequence PyTorch decoder, teacher-forced over 16 tokens (4 prompt
   tokens and 12 random tokens).
3. Greedy decoding must give the same tokens in ONNX and in PyTorch. This check runs only for float exports with the
   full audio context.

For float exports the tolerance is a maximum absolute difference of 1e-3 × max(1, output scale). **For int8 exports
the differences are reported but not enforced.** int8 accuracy must therefore come from the ASR evaluation
([EVALUATION_PLAN.md](EVALUATION_PLAN.md) §3).

When validation fails, the script prints `VALIDATION FAILED`, exits with code 1 and registers nothing.

No real checkpoint has been exported in this repository. Which model size, window and quantisation to use is open; the
comparison procedure is in [EVALUATION_PLAN.md](EVALUATION_PLAN.md) §3, and host compute numbers are in §6.

The bundled models were produced as follows:

- GE2E with `python export_ge2e_onnx.py --out ../../models/speaker/ge2e-resemblyzer-0.1.4.onnx`. The script fails
  unless the maximum absolute difference from PyTorch is ≤ 1e-4 over four input shapes and the utterance cosine to
  Resemblyzer is ≥ 0.9999.
- Silero VAD, copied from the installed `silero-vad` wheel by `make_reference_fixtures.py`, which also regenerates the
  parity references in `tests/fixtures/reference/`.

### 4.2 Register a model with checksums

`export_whisper_onnx.py --register` does this automatically after validation passes. It uses the prefix as `id` and
the model id, such as `whisper-base`, as `version`. To register by hand:

```bash
cd tools/models
python register_model.py --manifest ../../models/models.json --id whisper-base-ctx400-int8 --version whisper-base \
    --role SpeechRecognition --format bonikpos-whisper-onnx/1 --license MIT \
    --source "OpenAI Whisper base, export_whisper_onnx.py --n-audio-ctx 400 --quantize int8" \
    --file config=asr/whisper-base-ctx400-int8-config.json \
    --file encoder=asr/whisper-base-ctx400-int8-encoder.int8.onnx \
    --file decoder=asr/whisper-base-ctx400-int8-decoder.int8.onnx \
    --file tokens=asr/whisper-base-ctx400-int8-tokens.txt
```

`register_model.py` hashes every listed file (paths are relative to the manifest), replaces any entry with the same
`id` and sorts entries by role. `--options '<json>'` adds adapter options and `--test-fixture` marks a fixture. The
resulting entry looks like this. It is illustrative; no such export exists here.

```json
{
  "id": "whisper-base-ctx400-int8",
  "version": "whisper-base",
  "role": "SpeechRecognition",
  "format": "bonikpos-whisper-onnx/1",
  "files": {
    "config": "asr/whisper-base-ctx400-int8-config.json",
    "encoder": "asr/whisper-base-ctx400-int8-encoder.int8.onnx",
    "decoder": "asr/whisper-base-ctx400-int8-decoder.int8.onnx",
    "tokens": "asr/whisper-base-ctx400-int8-tokens.txt"
  },
  "license": "MIT",
  "source": "OpenAI Whisper base, export_whisper_onnx.py --n-audio-ctx 400 --quantize int8",
  "sha256": {
    "asr/whisper-base-ctx400-int8-config.json": "<64 hex digits>",
    "asr/whisper-base-ctx400-int8-encoder.int8.onnx": "<64 hex digits>",
    "asr/whisper-base-ctx400-int8-decoder.int8.onnx": "<64 hex digits>",
    "asr/whisper-base-ctx400-int8-tokens.txt": "<64 hex digits>"
  }
}
```

### 4.3 Bundle a model (Debug and Release builds)

1. Put the files under `models/`: `models/asr/` for ASR, `models/tse/` for TSE and `models/speaker-extra/` for extra
   encoders. All three folders are git-ignored, so keep the exported artefacts and their hashes somewhere durable.
2. Register the model in `models/models.json`.
3. Remove files that should not ship. Every `*.onnx`, `*.json` and `*.txt` below `models/` is packaged. That includes
   the intermediate float graphs of an int8 export and any `-reference.json` file.
4. Rebuild. The first launch extracts and verifies the files. Diagnostics → *Models* should then show the role as
   *Ready*.

If `models/models.json` lists a model whose files are absent at build time, the build has no copy of them and the app
shows that role as not installed. The package grows by the size of the model. For example, the base-dimension exports
in §6 are 88 MB (encoder) + 186 MB (decoder) in float with an 8 s window, and 25 + 149 MB in int8.

### 4.4 Side-load a model (Debug builds only)

Build the side-load folder on the computer first, for example by exporting with
`--out-dir <staging>/models/asr --register <staging>/models/models.json`:

```text
models/                        side-load root on the device
  models.json                  its own bonikpos-models/1 manifest; paths are relative to this folder
  asr/whisper-base-ctx400-int8-config.json
  asr/whisper-base-ctx400-int8-encoder.int8.onnx
  asr/whisper-base-ctx400-int8-decoder.int8.onnx
  asr/whisper-base-ctx400-int8-tokens.txt
```

Then copy the folder to the device:

- **Android**: copy it to `/sdcard/Android/data/com.bonikpos.voicesearch/files/models/` with `adb`
  ([DEPLOYMENT_ANDROID.md](DEPLOYMENT_ANDROID.md) §7).
- **iOS**: copy it to the app's `Documents/models` with the Files app or Finder
  ([DEPLOYMENT_IOS.md](DEPLOYMENT_IOS.md) §6).

Then open Diagnostics → *Reload models*.

The side-load manifest only needs the models it adds. Bundled models still serve the other roles. A side-loaded entry
that verifies takes precedence over the bundled entry for the same role. Side-loading a different speaker encoder
means the owner must enroll again (§3.2).

## 5. Parity measured by the tests

The integration tests compare each C# front-end and adapter with the upstream Python implementation. The values below
are the measured differences, as recorded in the test sources and in [TEST_RESULTS.md](TEST_RESULTS.md).

| Component (test) | Reference | Measured |
|---|---|---|
| Slaney mel filter banks, 40 and 80 bins (`FeatureParityTests`) | `librosa.filters.mel`, whisper `mel_filters.npz` | ≤ 1.9e-9 absolute; identical filter support |
| GE2E power-mel front-end (`FeatureParityTests`) | `librosa.feature.melspectrogram` | 1.9e-6 dB max (1.7e-7 of the peak; 3.8e-7 relative) |
| Kaldi fbank, 80 bins (`FeatureParityTests`) | kaldi-native-fbank | median 3.8e-6, p99 5e-5, max 8.8e-4 (log energies; the largest errors are in weak low-frequency bins) |
| Silero VAD streaming probabilities, 3 clips (`ModelParityTests`) | silero-vad `OnnxWrapper` | bit-identical (0.0) |
| GE2E utterance embeddings, 7 clips (`ModelParityTests`) | Resemblyzer `embed_utterance` (PyTorch) | cosine 1.0000000; elements ≤ 3.1e-7; pairwise cosines within 1.6e-7 |
| Whisper log-mel, random-weight fixture (`WhisperFixtureTests`) | `whisper.audio.log_mel_spectrogram` | 3.6e-7 |
| Whisper greedy decoding, random-weight fixture | onnxruntime run in Python | tokens identical; log-probabilities 2.2e-6; language logits 2.6e-6 |
| Whisper KV-cached decoder, teacher forcing | full-sequence PyTorch decoder | top-5 token ids identical; logits 7.6e-6 (the export's own validation measured 5.2e-6) |
| pGTCRN STFT adapter, random-weight fixture (`PgtcrnStftAdapterTests`) | Python streaming STFT → ONNX Runtime → inverse STFT | 3.3e-7 for 256-, 320- and 160-sample blocks |
| TSE plumbing graph, arithmetic (`TsePlumbingFixtureTests`) | onnxruntime run in Python | 1.2e-7 direct, 1.7e-6 through the adapter ([TEST_RESULTS.md](TEST_RESULTS.md)) |

> **What this does and does not show.** These tests verify that the C# feature front-ends, the tensor plumbing and the
> decoding logic reproduce the reference implementations. They say **nothing about recognition accuracy**: the
> Whisper fixture has random weights. They say nothing about **owner-verification accuracy** either, because the
> clips are synthetic TTS voices. Nor do they measure **extraction quality**, because the pGTCRN weights are untrained.

## 6. Whisper compute on the development host

Source: `eval/results/asr-bench-host.md`, generated 2026-10-09 09:06 UTC.

- Host: Ubuntu 24.04.5 x86-64 VM with 4 logical CPUs, .NET 10.0.12, ONNX Runtime 1.31.0 CPU execution provider,
  using the app's session options. **This is not a phone.**
- Exports: the random-weight compute-shape exports from §1.2. They do the same arithmetic per step as trained
  checkpoints of the same shape.
- Each row: 7 runs after a discarded warm-up, on 3 s of synthetic audio with 12 teacher-forced tokens.
- *Total* = log-mel + encoder + start-of-transcript step + prompt step + 12 token steps. Peak RSS is for the whole
  benchmark process.

| Export (random weights) | Quant | Window | Threads | Encoder p50 ms | Per token p50 ms | Total p50 ms | Peak RSS MB | Encoder / decoder MB |
|---|---|---|---:|---:|---:|---:|---:|---|
| shape-tiny | none | 30 s | 1 / 2 / 4 | 665 / 407 / 274 | 33.3 / 21.5 / 19.8 | 1150 / 744 / 583 | 550–554 | 36 / 108 |
| shape-base | none | 30 s | 1 / 2 / 4 | 1650 / 949 / 598 | 72.1 / 43.4 / 32.6 | 2722 / 1602 / 1073 | 821–825 | 91 / 186 |
| shape-base-ctx400 | none | 8 s | 1 / 2 / 4 | 259 / 187 / 135 | 34.8 / 24.8 / 21.7 | 777 / 559 / 455 | 588–596 | 88 / 186 |
| shape-base-ctx400-int8 | int8 | 8 s | 1 / 2 / 4 | 143 / 102 / 90 | 17.1 / 15.9 / 17.1 | 433 / 357 / 358 | 353–354 | 25 / 149 |

For the base dimensions with an 8 s window, int8 dynamic quantisation had these effects on this host:

- About **halved the per-token decoder time on one thread**, from 34.8 to 17.1 ms. The saving was smaller with more
  threads: −36 % on 2 threads and −21 % on 4.
- Cut the encoder time by 33–45 %.
- Lowered peak RSS by about 40 %, from 588–596 MB to 353–354 MB.

Its effect on accuracy has not been measured. Interpretation and the phone procedure are in
[BENCHMARKS.md](BENCHMARKS.md) §2.2 and §5. No phone numbers exist yet.

To reproduce, build the evaluation tool in Release and pass the exported configs. The parameters below match the report
header.

```bash
dotnet build tools/BonikPOS.VoiceSearch.Evaluation/BonikPOS.VoiceSearch.Evaluation.csproj -c Release
dotnet tools/BonikPOS.VoiceSearch.Evaluation/bin/Release/net10.0/BonikPOS.VoiceSearch.Evaluation.dll asr-bench \
    --configs <dir>/shape-tiny-config.json,<dir>/shape-base-config.json,<dir>/shape-base-ctx400-config.json,<dir>/shape-base-ctx400-int8-config.json \
    --threads 1,2,4 --repetitions 7 --tokens 12 --seconds 3 --out asr-bench.json --md asr-bench.md
```

Compute figures for the untrained pGTCRN graphs are in `eval/results/tse-bench-host.md` ([BENCHMARKS.md](BENCHMARKS.md)
§2.3).

## 7. Open items

- Export a real, multilingual Whisper checkpoint and measure its accuracy for each candidate: model size,
  `--n-audio-ctx` and float vs int8 ([EVALUATION_PLAN.md](EVALUATION_PLAN.md) §3).
- Obtain or train a licensed TSE model ([TSE_MODEL_AUDIT.md](TSE_MODEL_AUDIT.md)).
- Calibrate the provisional GE2E thresholds on consented real voices.
- On phones, verify XNNPACK and CoreML acceptance, timings and memory for every model. Nothing has run on a device
  yet.
