# Target-speaker extraction model audit

This document answers the request *"Audit the available target-speaker extraction models for this project. Verify
model weights, license, architecture, streaming support, model inputs and outputs, speaker conditioning, ONNX export
compatibility, and Android/iOS CPU performance. Compare suitable candidates and recommend one based on measured
evidence. Do not invent download URLs or claim a model works until verified. Implement an adapter for the selected
model and document any missing artifacts."* It also covers the product-spec item *"evaluate a target-speaker-conditioned
ASR alternative if a suitable implementation is available"* (section 4).

Audit date: 2026-10-09. Every measured number was produced on the development host (x86-64 cloud VM, 4 vCPU).
**Nothing was measured on an Android phone or an iPhone, and no extraction quality was measured for any model**: no
speaker-conditioned third-party weights were obtained, and our own model (pGTCRN) has not been trained.

**Tags.** **[V]** verified in this session by inspecting a downloaded artefact or running code (evidence named).
**[S]** stated by the source (README, model card, paper page, package metadata), not independently verified.
**[U]** unknown / could not be verified here.

**Evidence conventions.** URLs written as links (`https://…`) were fetched; each appears in
[evidence/tse-fetch-log.tsv](evidence/tse-fetch-log.tsv) with HTTP status, size and SHA-256. Web addresses written in
`code font` without a scheme (e.g. `huggingface.co/…`) were only *linked from* a downloaded file (named) and were
**not fetched**. `research/tse2/…` and `research/raw/…` are the audit session's scratch folders; they are **not in
this repository**. `research/raw/` has no fetch log, so its files are cited by file name only. Commits are from
blob-less shallow clones (file lists only; section 9).

Licence statements describe licence texts and source statements. They are not legal advice; a legal review is a
missing artefact (section 8).

## 1. Summary

- **No candidate met every requirement at once**: a commercially usable licence for both code and weights,
  published weights we could verify, streaming/causal operation, phone size, and extraction conditioned on an
  enrollment embedding (section 3).
- **GTCRN (MIT, commit 502ebfa) and UL-UNAS (MIT) are the only candidates whose weights and streaming ONNX graphs
  were downloaded and run here [V].** Both are speaker-agnostic denoisers: their graphs have no speaker input, so they
  cannot remove a competing talker.
- **Every speaker-conditioned extraction project failed at least one gate**: no licence file (SEF-PNet, D-LGTSE, three
  VoiceFilter re-implementations; SpeakerBeam's saved LICENSE is a "404: Not Found" page; none found for X-TF-GridNet),
  non-commercial (USEF-TSE, CC BY-NC 4.0), copyleft (SpEx, GPL-3.0 for code and models), no published weights (WeSep
  v0.1), or weights only on hosts this session did not download from (ESPnet TD-SpeakerBeam, ClearerVoice SpEx+,
  D-LGTSE, USEF-TSE). Every speaker-conditioned recipe configuration that states causality (WeSep, ESPnet, SEF-PNet,
  D-LGTSE) is non-causal, and ClearerVoice labels its SpEx+ model non-causal.
- **OS features do not solve owner-voice extraction**: Android's `NoiseSuppressor` and Apple's Voice Isolation are not
  conditioned on an enrolled speaker, and Apple's mode is chosen by the user in Control Center, not by the app (section 5).
- **Target-speaker-conditioned ASR is not a fit**: DiCoW / TS-ASR-Whisper is built on Whisper large-v3-turbo, is
  conditioned on diarization output rather than an enrollment embedding, and its documented diarizer is CC BY-NC;
  VoiceFilter-Lite has no published code or weights (section 4). Neither was integrated.
- **Recommendation: train pGTCRN in-house** — GTCRN plus FiLM conditioning on the 256-dim GE2E embedding from the
  speaker encoder the app already bundles — starting at 32 channels, on data with documented commercial rights. The
  streaming C# adapter for it is implemented and matches the Python reference to 3.3e-7; it adds 28 ms algorithmic
  delay at the pipeline's 20 ms frames; the untrained graphs need 1.0–1.8 ms (p50, including the adapter) per 16 ms
  frame on one thread of the host. **Until trained weights pass [EVALUATION_PLAN.md](EVALUATION_PLAN.md) §4, the app reports target-speaker
  extraction as unavailable**: owner verification still rejects other speakers' utterances after the fact, but
  overlapping speech is not removed.

## 2. Requirements used for the audit

| # | Requirement | Pass condition |
|---|---|---|
| R1 | Code licence | Licence text found and read; permissive (e.g. MIT, Apache-2.0); no non-commercial or copyleft terms on anything shipped in the app |
| R2 | Weights and training-data licence | Weights licence stated (or the weights are covered by the repository licence) and training-data terms compatible with commercial use |
| R3 | Weights availability | Weights published, downloaded here, hash recorded, and loaded or run in this session |
| R4 | Architecture, rate, I/O | 16 kHz mono (the pipeline rate); I/O expressible as a stateful per-chunk or per-STFT-frame ONNX graph |
| R5 | Streaming | Causal, frame by frame with explicit state; algorithmic delay of tens of milliseconds |
| R6 | Speaker conditioning | Extraction conditioned on the owner's enrollment (embedding or enrollment audio). Denoisers, blind separation and personal VAD do not remove overlapping speech |
| R7 | ONNX / mobile | ONNX graph with standard operators and static state shapes (what `streaming-tse-stft-onnx/1` and `streaming-tse-onnx/1` accept) |
| R8 | CPU cost | Real time with margin on phone CPUs. Only the x86-64 host was available; phone cost is unmeasured |

## 3. Candidates

### 3.1 Licence and artefacts (R1–R3)

| # | Candidate | Kind | Code licence | Weights published / obtained here | Weights and data licence |
|---|---|---|---|---|---|
| 1 | GTCRN (Xiaobin-Rong/gtcrn @502ebfa) | Denoiser | MIT [V] | In the git repo; 4 checkpoint files (3 distinct) and 2 streaming ONNX files downloaded, blob hashes match the commit [V] | Covered by the repo's MIT licence, no separate weights licence [V]; trained on DNS3 / VCTK-DEMAND [S]; data terms [U] |
| 2 | UL-UNAS (Xiaobin-Rong/ul-unas @00f7c70) | Denoiser | MIT [V] | DNS3 checkpoint and streaming ONNX downloaded [V] | As GTCRN; data terms [U] |
| 3 | DeepFilterNet (Rikorose @d375b2d) | Denoiser (48 kHz) | MIT or Apache-2.0 [V] | Model archives listed in the git tree [V]; not downloaded [U] | Not stated for the model files [U] |
| 4 | DPDFNet (ceva-ip @9bd9844) | Denoiser | Apache-2.0 [V] | Not in repo or wheel [V]; Hugging Face only [S]; not downloaded [U] | [U] |
| 5 | sudo_rm_rf (etzinis @cd00f2e) | Blind separation | LICENSE file: MIT; README: University of Illinois licence [V] (inconsistent) | zenodo, linked [S]; not downloaded [U] | [U]; WSJ0-2mix / WHAMR! [S] |
| 6 | sherpa-onnx (k2-fsa @f28c1d1) | Runtime with denoisers | Apache [S] | Re-packages the upstream GTCRN ONNX [V]; release assets not downloaded [U] | As upstream |
| 7 | VoiceFilter re-implementations (maum-ai/voicefilter @7db2930, funcwj/voice-filter @9daa53b, deciding/Voicefilter @dda34da) | TSE | No LICENSE file in any of the three trees [V] | No VoiceFilter weights in the trees [V] | [U] |
| 8 | Personal VAD (wq2012/personal_vad @4f4f46e) | Personal VAD | Apache-2.0 [V] | Hugging Face only [S]; not downloaded [U] | Model card: apache-2.0 [S]; trained on MLS [S] |
| 9 | FireRedChat (FireRedTeam @9d8c4b9) | Voice agent with personal VAD | Apache-2.0, plus "academic purposes only" disclaimer [V] | Hugging Face [S]; not downloaded [U] | [U] |
| 10 | WeSep (wenet-e2e/wesep @dc11cb1) | TSE toolkit | Apache-2.0, except the USEF module: CC BY-NC 4.0 [V] | None ("coming soon") [S] | — |
| 11 | ESPnet `tse1` recipes, TD-SpeakerBeam (espnet @bf81520) | TSE | Apache-2.0 [V] | Hugging Face, linked [S]; not downloaded [U] | [U]; LibriMix / WSJ0-2mix [S] |
| 12 | SEF-PNet (isHuangZiling @e9635ae) | TSE | None in the tree [V] | None in the tree [V] | — |
| 13 | D-LGTSE (isHuangZiling @899f53e) | TSE | None in the tree [V] | Hugging Face, linked [S]; not downloaded [U] | [U]; WHAM! [S] |
| 14 | UDSEF-PNet (isHuangZiling) | TSE | [U] (clone empty) | [U] | [U] |
| 15 | ClearerVoice-Studio / `clearvoice` 0.1.2 | SE, separation, TSE toolkit | Apache-2.0 [V] | Fetched at run time from Hugging Face by the package [V]; not downloaded [U] | [U] |
| 16 | SpEx (`research/raw/spex_master_*`) | TSE | GPL-3.0 for "code and models" [V] | [U] | GPL-3.0 [V] |
| 17 | SpEx+ (`research/raw/spexplus_master_*`) | TSE | MIT [V] | [U] | [U] |
| 18 | SpeakerBeam, Asteroid-based (`research/raw/speakerbeam_*`) | TSE | Saved LICENSE reads "404: Not Found" [V], so [U] | None mentioned [S] | — |
| 19 | USEF-TSE (`research/raw/usef_*`) | TSE | CC BY-NC 4.0 [V] | Hugging Face, linked [S]; not downloaded [U] | CC BY-NC 4.0 [V] |
| 20 | X-TF-GridNet (`research/raw/xtfgridnet_*`) | TSE | No licence text in the evidence [U] | In its GitHub repo, linked [S]; not downloaded [U] | [U] |
| 21 | SpeechBrain 1.1.1 / Asteroid 0.7.0 / pyannote.audio 4.0.7 (PyPI wheels) | Toolkits | Apache-2.0 / MIT / MIT [V] | Wheels contain no weights [V] | [U] |
| 22 | **pGTCRN (this repository)** | TSE | Our code; GTCRN's MIT notice kept in `tools/models/tse/LICENSE-GTCRN` [V] | **No trained weights**; random-weight exports only [V] | Set by our training data |

### 3.2 Technical fit (R4–R8)

| # | Candidate | Architecture / rate | Streaming | Model I/O | Speaker conditioning | ONNX | Size | Fit |
|---|---|---|---|---|---|---|---|---|
| 1 | GTCRN | STFT 512 / hop 256, periodic sqrt-Hann, 16 kHz; ERB bands, grouped temporal convolutions, grouped dual-path GRU, complex ratio mask [V] | Yes: one frame per call, conv/tra/inter caches [V] | `mix` [1,257,1,2], `conv_cache` [2,1,16,16,33], `tra_cache` [2,3,1,1,16], `inter_cache` [2,1,33,16] → `enh` [1,257,1,2] + caches [V] | None [V] | Yes, opset 11, 18 ScatterND nodes [V] | 535,190 B; 23,669 trainable parameters, 48,245 with the fixed ERB matrices [V] | Denoiser only |
| 2 | UL-UNAS | 257-bin STFT frames [V]; n_fft 512, hop 256, 16 kHz and a plain Hann window in the README example [S] | Yes: per-frame caches [V] | `mix` [1,257,1,2], `conv_cache` [1,5358], `tfa_cache` [1,402], `inter_cache` [1,1056] → `enh` + caches [V] | None [V] | Yes, opset 11 [V] | 788,967 B [V] | Denoiser only |
| 3 | DeepFilterNet | Deep filtering, 48 kHz full band [S] | Real-time tools [S] | [U] | None [S] | Archives in the tree [V]; not inspected [U] | [U] | Denoiser, 48 kHz |
| 4 | DPDFNet | DeepFilterNet2 + dual-path RNN; 8 / 16 / 48 kHz [S] | Yes: 20 ms window, 10 ms hop [S] | [U] | None [S] | ONNX and TFLite on Hugging Face [S] | 16 kHz models: 2.31–3.54 M parameters, 8.3–13.9 MB ONNX [S] | Denoiser only |
| 5 | sudo_rm_rf | Time-domain U-ConvBlocks; 8 kHz experiments [S] | A causal model file is in the tree [V]; the pretrained files listed in the README are "Improved" and "Group Comm" variants, none labelled causal [S] | Mixture → sources [S] | None (blind separation) [S] | [U] | [U] | No |
| 6 | sherpa-onnx | Runtime: streaming GTCRN and DPDFNet denoisers [V] | Yes [V] | As GTCRN / DPDFNet | None; no TSE component in its 6,623-file tree [V] | — | — | No TSE |
| 7 | VoiceFilter re-impl. | CNN + bi-LSTM + FC spectrogram mask conditioned on a d-vector [S] | No: bidirectional LSTM [S] | Spectrogram + d-vector → mask [S] | d-vector [S] | [U] | [U] | No |
| 8 | Personal VAD | 4-layer conformer, left context 31, right context 0, FiLM on d-vector and cosine score [V] | Yes [V] | Features + 256-dim speaker embedding + cosine score → 3 classes per frame [S] | 256-dim d-vector [S] | TFLite FP32 / INT8 [S]; ONNX [U] | 1,091,267 parameters; INT8 TFLite 1,397.80 KB [S] | Gates frames; does not remove overlap |
| 9 | FireRedChat pVAD | [U] (code in the `agents` submodule, not inspected) | [U] | [U] | Personalised [S] | [U] | [U] | Gates frames only |
| 10 | WeSep | BSRNN, SpEx+, DPCCN, TFGridNet [S]; BSRNN recipes at 16 kHz, window 512, stride 128 [V] | `causal` option exists; recipes set `False` [V] | Mixture + enrollment cue → speech [S] | Enrollment audio through a WeSpeaker ECAPA-TDNN encoder and/or USEF [V] | Planned [S] | [U] | No weights |
| 11 | ESPnet TD-SpeakerBeam | Conv encoder (256 ch, kernel 32, stride 16) + TCN extractor [V]; 16 kHz LibriMix and 8 kHz WSJ0-2mix recipes (file and data-set names) [S] | `causal` option exists; recipes set `False` [V] | Mixture + enrollment audio → target [V] | Enrollment utterance through the shared encoder, or an external 256-dim embedding (`use_spk_emb`) [V] | [U] | [U] | Non-causal recipes; weights unverified |
| 12 | SEF-PNet | STFT 256 / 64 sqrt-Hann at 8 kHz, conv + TCN [V] | Config `causal: False` [V] | Mixture + enrollment speech [S] | No speaker encoder; enrollment speech fused directly [S] | [U] | [U] | No |
| 13 | D-LGTSE | GTCRN stage-1 denoiser + TSE backbone; 8 kHz [S] | Results labelled offline [S]; config `causal: False` [V] | [U] | Enrollment speech [S] | [U] | [U] | No |
| 14 | UDSEF-PNet | No data [V] | [U] | [U] | [U] | [U] | [U] | No evidence |
| 15 | ClearerVoice / ClearVoice | Audio-only TSE: SpEx+, non-causal, WSJ0-2mix [S]; the packaged TSE model `AV_MossFormer2_TSE_16K` needs face video [V] | Non-causal [S] | [U] | Reference speech (SpEx+) or face / lip / gesture video [S] | [U] | [U] | No |
| 16 | SpEx | Multi-scale time domain [S] | [U] | Mixture + reference utterance [S] | Reference utterance [S] | [U] | 10.79 M parameters [S] | No (GPL-3.0) |
| 17 | SpEx+ | Time domain [S] | [U] | Mixture + reference utterance [S] | Reference utterance [S] | [U] | 11.14 M parameters, 3.76 GMAC/s [S] | No (weights unknown) |
| 18 | SpeakerBeam (Asteroid) | TD-SpeakerBeam; Libri2Mix 8 kHz recipe [S] | [U] | Mixture + enrollment [S] | Enrollment utterance (adaptation layer) [S] | [U] | [U] | No |
| 19 | USEF-TSE | SepFormer / TFGridNet variants; config at 8 kHz [V] | [U] | Mixture + enrollment [S] | Embedding-free; enrollment fused directly [S] | [U] | [U] | No (CC BY-NC) |
| 20 | X-TF-GridNet | TF-GridNet + U²-Net speaker embedding + adaptive fusion [S] | [U] | Mixture + enrollment [S] | Embedding from enrollment [S] | [U] | 7.79 M parameters, 68.32 GMAC/s [S] | No |
| 21 | SpeechBrain / Asteroid / pyannote | — | — | — | No TSE code in the SpeechBrain and Asteroid wheels (keyword search) [V]; pyannote's separation pipeline is diarization-driven (PixIT) [S] | — | — | No |
| 22 | **pGTCRN** | GTCRN + FiLM after the encoder, on a 256-dim embedding; channel width 16 / 32 / 48 [V] | Yes; streaming equals offline within 2.1e-6 [V] | `mix` [1,257,1,2], `spk_emb` [1,256], `conv_cache` [2,1,C,16,33], `tra_cache` [2,3,1,1,C], `inter_cache` [2,1,33,C] → `enh` + caches [V] | The bundled GE2E encoder `ge2e-resemblyzer` 0.1.4 [V] | Opset 17, no ScatterND [V] | 336,455 / 576,005 / 941,494 B; 31,893 / 95,253 / 190,101 parameters [V] | Fits R4–R7; untrained |

### 3.3 Evidence per candidate

1. **GTCRN.** Shallow clone at `502ebfab64da7c4a9af78dcb9c6ceef1ebb01c73` (35 files, `research/tse2/gtcrn_git_tree_502ebfa.txt`).
   All 35 files were downloaded from the repository's `main` branch on raw.githubusercontent.com (fetch log) and match
   the blob hashes of that tree (`research/tse2/gtcrn_inventory.tsv`) [V]. Licence
   <https://raw.githubusercontent.com/Xiaobin-Rong/gtcrn/main/LICENSE>: MIT, "Copyright (c) 2024 Rong Xiaobin"
   (sha256 `c467165e5860…`). Streaming ONNX
   <https://raw.githubusercontent.com/Xiaobin-Rong/gtcrn/main/stream/onnx_models/gtcrn_simple.onnx> (535,190 B, sha256
   `b4718df6228e7bdf1a8a435cf98f838636eb2fd331acabf86ba87c5192ebcb87`); checkpoint
   <https://raw.githubusercontent.com/Xiaobin-Rong/gtcrn/main/stream/onnx_models/model_trained_on_dns3.tar> (737,323 B,
   sha256 `250fa2820ea9947704a62dda8e642ba773a067719c5e4294ad95aeab23f06442`). The repository holds two different files
   named `model_trained_on_dns3.tar`; the one under `checkpoints/` (579,819 B, sha256 `a630d992cf79…`) shares 1
   byte-identical tensor with `gtcrn_simple.onnx`, the one under `stream/onnx_models/` shares 99
   (`research/tse2/tools/onnx_vs_ckpt.py`) [V], so the parity check in 6.1 uses the latter. The README states
   48.2 K parameters including the ERB module (23.7 K in the paper), 33.0 MMAC/s, RTF 0.07 on an i5-12400, quality
   tables, and training on DNS3 and VCTK-DEMAND [S].
2. **UL-UNAS.** Clone `00f7c700da43d38347f30a6ccebd86fcbc798e07` (16 files). Licence
   <https://raw.githubusercontent.com/Xiaobin-Rong/ul-unas/main/LICENSE>: MIT, "Copyright (c) 2026 Xiaobin Rong" [V].
   Checkpoint <https://raw.githubusercontent.com/Xiaobin-Rong/ul-unas/main/checkpoints/model_trained_on_dns3.tar>
   (2,284,926 B, sha256 `9a0656bf8e88…`; structure listed without unpickling, `research/tse2/tools/ckpt_inspect.py`)
   and <https://raw.githubusercontent.com/Xiaobin-Rong/ul-unas/main/ulunas_onnx/onnx_models/ulunas_stream_simple.onnx>
   (788,967 B, sha256 `f2e804d54d6a…`; I/O read from the file, timed in 6.4) [V]. The ONNX README
   (`ulunas_onnx/README.md`) runs the STFT with `torch.hann_window(512)` [S]; our STFT adapter accepts only the
   square-root Hann window (`StftStreamingTseModel`), so UL-UNAS would need a window option before it could run there.
3. **DeepFilterNet.** Clone `d375b2d8309e0935d165700c91da9de862a99c31` (168 files) lists `models/DeepFilterNet3_onnx.tar.gz`
   and other model archives [V]; they were not downloaded. <https://raw.githubusercontent.com/Rikorose/DeepFilterNet/main/LICENSE>:
   "Licensed under either of Apache License, Version 2.0 … or MIT license … at your option" [V]; the README says
   "All code in this repository is dual-licensed" and describes 48 kHz full-band enhancement [S]. The PyPI wheels
   `deepfilternet-0.5.6-py3-none-any.whl` and `DeepFilterLib-0.5.6-cp310-cp310-manylinux_2_28_x86_64.whl`
   (files.pythonhosted.org, fetch log) contain no weights [V].
4. **DPDFNet.** Clone `9bd9844a227bb6aa57e55588d8d0e961fcff1c46` (46 files, no weights). Licence
   <https://raw.githubusercontent.com/ceva-ip/DPDFNet/main/LICENSE>: Apache-2.0 [V]. README
   <https://raw.githubusercontent.com/ceva-ip/DPDFNet/main/README.md>: "Model files are not bundled in this repository",
   download from `huggingface.co/Ceva-IP/DPDFNet` (linked from `research/tse2/dl_dpdfnet/README.md`, not fetched);
   model table and streaming API [S]. `dpdfnet-0.6.0-py3-none-any.whl`: Apache-2.0, no weights [V]. The sherpa docs
   (<https://raw.githubusercontent.com/k2-fsa/sherpa/master/docs/source/onnx/speech-enhancement/dpdfnet.rst>) call it
   causal and streaming-friendly [S].
5. **sudo_rm_rf.** Clone `cd00f2e21f7ad6281360cdf24ade36f84b0fbad6` (78 files) includes
   `sudo_rm_rf/dnn/models/causal_improved_sudormrf_v3.py` [V]. <https://raw.githubusercontent.com/etzinis/sudo_rm_rf/master/LICENSE>
   is MIT ("Copyright (c) 2021 Efthymios Tzinis"), while the README's "Copyright and license" section is the
   University of Illinois Open Source License [V]. Weights: `zenodo.org/record/6299852/…` (linked from the README and
   `pretrained_models/download_pretrained_models.sh`, not fetched).
6. **sherpa-onnx.** Clone `f28c1d1840ea23cf8fd31fb2936371982522b22c` (6,623 files) contains streaming GTCRN and DPDFNet
   denoisers (`sherpa-onnx/csrc/online-speech-denoiser-gtcrn-impl.h`, `online-speech-denoiser-dpdfnet-impl.h`); a
   keyword search of the tree for target-speaker components found none [V].
   <https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/master/scripts/gtcrn/run.sh> downloads the upstream
   `stream/onnx_models/gtcrn_simple.onnx` and `add_meta_data.py` only adds metadata (n_fft 512, hop 256, window
   `hann_sqrt`) [V]. Models are in the `speech-enhancement-models` GitHub release (linked from
   `research/tse2/dl_sherpa_docs/speech-enhancement_models.rst`, not fetched). PyPI 1.13.8 licence field: "Apache
   licensed, as found in the LICENSE file" [S].
7. **VoiceFilter re-implementations.** Trees maum-ai/voicefilter `7db2930dacd94f33d44380bf07001fc7cab80e55` (25 files),
   funcwj/voice-filter `9daa53b74c4aa821ce3e4376b298b119b168f263` (19), deciding/Voicefilter
   `dda34da9d1cfca48102b2d1b4274bfd76e5a2e1c` (43): no LICENSE file and no VoiceFilter weights; deciding's tree has an
   `embedder.pt` (speaker encoder, not downloaded) [V]. `research/raw/mindslab_voicefilter_README.md` (by its file
   name, the README of mindslab-ai/voicefilter, which Google's VoiceFilter page links) says "Apache License 2.0",
   gives the d-vector embedder only as a Google Drive link (not fetched) and marks `utils/hparams.py` "No License
   specified" [S]; `research/raw/mindslab_voicefilter_LICENSE` contains "404: Not Found" [V]. Google's page
   (`research/raw/gsid_master_publications_VoiceFilter_index.html`) describes CNN + bi-LSTM + fully connected layers
   and says the original system "cannot be open sourced" [S].
8. **Personal VAD.** Clone `4f4f46e60d4fb250c1161cb64c8161da53568e05`; licence
   <https://raw.githubusercontent.com/wq2012/personal_vad/main/LICENSE>: Apache-2.0 [V]. Config
   <https://raw.githubusercontent.com/wq2012/personal_vad/main/pretrained_models/pvad2_e3_conformer_film_dvector_cos/model_config.json>:
   `backbone: conformer`, `conformer_right_context: 0`, `speaker_embedding_dim: 256`,
   `conditioning_mode: film_dvector_cos` [V]. The git tree's `pretrained_models/` holds only READMEs, configs and
   metrics; weights are on `huggingface.co/wq2012/…` (linked from the package description,
   `research/tse2/whl_personal-vad/METADATA.txt`; not fetched); `personal_vad-0.1.0-py3-none-any.whl` has no
   weights [V]. The model card (`pvad2_e3_README.md`) states `license: apache-2.0`, 1,091,267 parameters and
   training on Multilingual LibriSpeech [S].
9. **FireRedChat.** Clone `9d8c4b99fbd27380f6bf44f9b2f78ae615229a55` (23 files); licence
   <https://raw.githubusercontent.com/FireRedTeam/FireRedChat/main/LICENSE>: Apache-2.0 [V]. README: pVAD "for improved
   barge-in experience"; "The content provided is for academic purposes only" [V text]. The agent code is a submodule
   (`github.com/fireredchat-submodules/agents`, linked from `.gitmodules`, not fetched); models at
   `huggingface.co/FireRedTeam` (linked, not fetched).
10. **WeSep.** Clone `dc11cb18ee8636064a7851f38b9348143c343cdc` (289 files). Licence
    <https://raw.githubusercontent.com/wenet-e2e/wesep/master/LICENSE>: Apache-2.0;
    <https://raw.githubusercontent.com/wenet-e2e/wesep/master/THIRD_PARTY_NOTICES.md>: `wesep/modules/speaker/usef.py`
    "is excluded from WeSep's Apache License 2.0 grant and remains subject to the upstream non-commercial license" [V].
    README: "Official pretrained models and automatic download | Coming soon", "ONNX, TorchScript, and C++ deployment |
    To be updated", "production deployment is not yet supported" [S]. Configs `tse_bsrnn_spk.yaml` and
    `tse_bsrnn_spk_online.yaml`: `causal: False`, speaker encoder `ECAPA_TDNN_GLOB_c512` from WeSpeaker; the VoxCeleb1
    online-mixing config enables `usef` [V]. WeSpeaker's model licence "follows the license of it's corresponding
    dataset" (VoxCeleb: CC BY 4.0) [S, `research/tse2/dl_data_licenses/wespeaker_pretrained.md`].
11. **ESPnet TSE recipes.** Clone `bf81520330a1e198a32011ff873f65dc0f46f14f`: `tse1` recipes for librimix, mini_an4,
    wsj0_2mix; the only extractor is `espnet2/enh/extractor/td_speakerbeam_extractor.py` [V]. Licence
    <https://raw.githubusercontent.com/espnet/espnet/master/LICENSE>: Apache-2.0 [V]. Configs
    <https://raw.githubusercontent.com/espnet/espnet/master/egs2/librimix/tse1/conf/tuning/train_enh_tse_td_speakerbeam_16k.yaml>
    and <https://raw.githubusercontent.com/espnet/espnet/master/egs2/wsj0_2mix/tse1/conf/tuning/train_enh_tse_td_speakerbeam.yaml>:
    `causal: False`, `use_spk_emb: false` [V]. Recipe READMEs link `huggingface.co/espnet/Wangyou_Zhang_librimix_train_enh_tse_td_speakerbeam_raw`
    and the WSJ0-2mix counterpart (not fetched) and report SI-SNR 10.74 dB on LibriMix `test_min_16k` [S].
12. **SEF-PNet.** Clone `e9635ae29504e0ac9090e4946c2ebdd232502f4d` (42 files): no LICENSE, no weights [V]. Config
    <https://raw.githubusercontent.com/isHuangZiling/SEF-PNet/main/nnet/conf_unet_tse_32ms.py>: `fs = 8000`,
    `"causal": False` [V]; README: Libri2Mix, speaker-encoder-free [S].
13. **D-LGTSE.** Clone `899f53ee0505152dcc7ce7bff10d20711ef327cc` (56 files): no LICENSE; `nnet/gtcrn.py` (GTCRN adapted)
    [V]. <https://raw.githubusercontent.com/isHuangZiling/D-LGTSE/main/readme.md>: weights at
    `huggingface.co/zilinghuang/D-LGTSE` (not fetched), WHAM! 8 kHz, results labelled "Offline" [S].
14. **UDSEF-PNet.** The shallow-clone directory `research/tse2/gitclone_isHuangZiling_UDSEF-PNet/` is empty; nothing
    was fetched [V]. Everything else is [U].
15. **ClearerVoice-Studio.** `research/raw/clearervoice_LICENSE`: Apache-2.0 [V]. `research/raw/cv_tse_README.md`:
    the audio-only TSE model is SpEx+ "(Non-causal)", trained on WSJ0-2mix, checkpoint
    `huggingface.co/alibabasglab/log_wsj0-2mix_speech_SpEx-plus_2spk/` (not fetched); the other TSE models are
    audio-visual (face, lip or gesture video) [S]. `clearvoice-0.1.2-py3-none-any.whl` (fetch log): licence expression
    Apache-2.0, no weights; its only TSE network is `AV_MossFormer2_TSE_16K` with an `s3fd` face detector; models are
    downloaded at run time from `huggingface.co/alibabasglab/<name>` (string in `clearvoice/networks.py`) [V].
16. **SpEx.** `research/raw/spex_master_LICENSE`: GNU GPL v3; README: "The code and models in this repository are
    licensed under the GNU General Public License Version 3" [V].
17. **SpEx+.** `research/raw/spexplus_master_LICENSE`: MIT, "Copyright (c) 2021 Meng Ge" [V]; the README is one heading
    [V]. Parameter and MAC figures are from the X-TF-GridNet README [S].
18. **SpeakerBeam (Asteroid-based).** `research/raw/speakerbeam_README.md`: Interspeech 2021 tutorial implementation,
    Libri2Mix `wav8k/min` recipe, train your own model [S]; `research/raw/speakerbeam_LICENSE` contains
    "404: Not Found" [V].
19. **USEF-TSE.** `research/raw/usef_main_LICENSE`: Creative Commons Attribution-NonCommercial 4.0 [V]; README links
    checkpoints at `huggingface.co/ZBang/USEF-TSE` (not fetched); `research/raw/usef_config_config-USEF-TFGridNet.yaml`:
    `sample_rate: 8000` [V].
20. **X-TF-GridNet.** `research/raw/xtfgridnet_main_README.md`: 7.79 M parameters, 68.32 GMAC/s, a WHAMR! model in the
    project's `exp` folder (linked, not fetched) [S]; no licence text in the evidence [U].
21. **SpeechBrain, Asteroid, pyannote.audio.** Wheels from files.pythonhosted.org (fetch log): licence metadata
    Apache-2.0 / MIT / MIT (pyannote: "Copyright (c) 2020 CNRS"); no weight files; a search for target-speaker,
    SpeakerBeam, VoiceFilter, SpEx and speaker-extraction code found none in SpeechBrain or Asteroid; pyannote's
    `SpeechSeparation` pipeline cites "PixIT: Joint Training of Speaker Diarization and Speech Separation" [V].

## 4. Target-speaker-conditioned ASR (alternative to extraction)

A target-speaker ASR model would replace the app's recogniser rather than clean its input. Evidence is in
`research/raw/` (no fetch log).

| Candidate | Code licence | Weights | Conditioning | Architecture / size | Streaming | Mobile | Verdict |
|---|---|---|---|---|---|---|---|
| DiCoW / TS-ASR-Whisper (BUT) | Apache-2.0: `dicow_main_LICENSE`, `tsasr_main_LICENSE` [V] | "DiCoW Model Weights: Released under CC BY 4.0" [S]; on Hugging Face (`huggingface.co/collections/BUT-FIT/dicow`, linked from `tsasr_main_README.md`, not fetched) [U]; the documented diarizer DiariZen is "CC BY-NC 4.0 – free for research and non-commercial use only" [S] | Frame-level diarization probabilities turned into silence / target / non-target / overlap masks injected into each encoder layer; SE-DiCoW adds a self-enrollment segment chosen where the diarizer finds the target most active [S]. Not an enrollment-embedding model | Whisper large-v3-turbo (export uses `openai/whisper-large-v3-turbo`) [S]; Whisper turbo has 809 M parameters (`whisper_README.md`) [S] | Not described [U] | Not measured. For scale: a random-weight export with Whisper base dimensions (74 M parameters [S]) needed 1,668 ms p50 for one 30 s encoder window on one host thread (`eval/results/asr-bench-host.md`) [V] | Not suitable: non-commercial diarizer dependency, size, and conditioning that does not use our enrollment |
| VoiceFilter-Lite (Google) | — | None published: the publication page (`gsid_master_publications_VoiceFilter-Lite_index.html`) links only the paper, slides and blog; the speaker-id repository README (`gsid_master_README.md`) describes it as "audio samples and supplementary materials", with code only for SidLingvo and DiarizationLM [V] | Keeps "only the speech signals from a target user" [S] | "tiny", streaming, 8-bit quantisable, runs in real time (abstract) [S] | Yes [S] | [U] | Not available |

Other ASR evidence in `research/raw/` (MMS, Omnilingual ASR, IndicConformer, Vistaar, NeMo featured models, Vosk,
the Whisper model card) contains no target-speaker or enrollment conditioning (keyword search, 0 matches) [V].
Keeping extraction in front of an unchanged recogniser also keeps the owner-verification step and the ASR choice
independent of each other.

## 5. OS-level voice processing

| Feature | What the documentation says | Owner-specific? | App-controllable? |
|---|---|---|---|
| Android `NoiseSuppressor` (<https://developer.android.com/reference/android/media/audiofx/NoiseSuppressor>) | "removes background noise from the captured signal. The component of the signal considered as noise can be either stationary (car/airplane engine, AC system) or non-stationary (other peoples conversations, car horn) for more advanced implementations"; `isAvailable()` "Checks if the device implements noise suppression" [V] | No: the documented API has no way to name the voice to keep [V] | Yes, where the device implements it; behaviour is device-dependent [V] |
| Android `MediaRecorder.AudioSource` (<https://developer.android.com/reference/android/media/MediaRecorder.AudioSource>) | `VOICE_COMMUNICATION` "tuned for voice communications such as VoIP"; `VOICE_RECOGNITION` "tuned for voice recognition"; `UNPROCESSED` "tuned for unprocessed (raw) sound if available" [V] | No | Yes (the app selects `VOICE_RECOGNITION`, `MIC` or `UNPROCESSED`: `AndroidMicrophoneCapture.cs`) |
| Apple `AVCaptureDevice.MicrophoneMode.voiceIsolation` (<https://developer.apple.com/tutorials/data/documentation/avfoundation/avcapturedevice/microphonemode/voiceisolation.json>), iOS 15+ | "A mode that processes microphone audio to isolate the voice and attenuate other signals" [V] | No: no speaker input [V] | No: `preferredMicrophoneMode` is "The microphone mode that the user selects in Control Center" (read-only); `activeMicrophoneMode` can differ when the audio route does not support the mode; the app can only open the system UI (`showSystemUserInterface(.microphoneModes)`) [V] |
| Apple `AVAudioIONode.setVoiceProcessingEnabled(_:)` (<https://developer.apple.com/tutorials/data/documentation/avfaudio/avaudioionode/setvoiceprocessingenabled(_:).json>) | "Enables or disables voice processing on the I/O node" [V]; nothing about speakers | No | Yes |

None of these keeps the enrolled owner and removes a competing talker. Whether Voice Isolation engages for this app's
capture configuration (`AVAudioSessionMode.Measurement` or `Default`, `IosMicrophoneCapture.cs`) and how any of these
features treat overlapping talkers were not tested [U]. Two earlier Apple requests (`…/microphonemode-swift.enum.json`
forms) returned 404 (fetch log); the URLs cited above returned 200.

## 6. Measured results (development host only)

### 6.1 Our GTCRN re-implementation reproduces the upstream model

`tools/models/tse/pgtcrn.py` re-implements GTCRN (derived from the MIT-licensed upstream at commit 502ebfa; the licence
text is kept next to it). `python tools/models/tse/export_pgtcrn_onnx.py --verify-upstream <gtcrn checkout>` loads the
upstream checkpoint (sha256 `250fa282…`) into `PGTCRN(channels=16, emb_dim=0)` with `torch.load(weights_only=True)` and
strict key matching, then compares on the first 4 s of the upstream `stream/test_wavs/mix.wav` [V; run in this
session, console report not saved to a file]:

| Comparison | Max abs difference |
|---|---:|
| Offline PyTorch vs streaming PyTorch (frame by frame) | 6.2e-6 |
| Streaming PyTorch vs upstream `gtcrn_simple.onnx` (sha256 `b4718df6…`) in ONNX Runtime | 9.9e-6 |
| Largest output magnitude, for scale | 30.66 |

The re-implementation has 23,669 trainable parameters (48,245 with the fixed ERB matrices) [V], matching the README's
23.7 K / 48.2 K [S]. GTCRN's front end is STFT n_fft 512, hop 256, periodic square-root Hann window, 16 kHz
(upstream `infer.py`; reproduced) [V].

### 6.2 pGTCRN graphs (random weights)

pGTCRN = GTCRN + FiLM (`x · (1 + γ(e)) + β(e)`, zero-initialised for training) applied after the encoder, conditioned
on a 256-dim GE2E embedding; the channel width is a parameter. Exports with `export_pgtcrn_onnx.py --export`, opset 17,
**random weights** (labelled in the ONNX metadata `bonik.weights = RANDOM-UNTRAINED seed=…`):

| Channels | Trainable parameters (FiLM) | ONNX bytes | SHA-256 | Seed | Location |
|---:|---:|---:|---|---:|---|
| 16 | 31,893 (8,224) | 336,455 | `fd26f859302fe5605b5b6e671d4c3eb16908a7339d18b9af63aa44a80bce2f89` | 1234 | `tests/fixtures/tse/pgtcrn-c16-e256-random.onnx` |
| 32 | 95,253 (16,448) | 576,005 | `5d65e4910f84d5a799de2d086f93d3e1788c09d86f541c5fa587f18aaf94df11` | 99 | Scratch only (hash in `eval/results/tse-bench-host.json`) |
| 48 | 190,101 (24,672) | 941,494 | `e526e65cd2972fa5c9976faee8d8281d120995bff722056b1808e9b853d583ba` | 99 | Scratch only |

- Streaming vs offline (PyTorch) and ONNX Runtime vs PyTorch: ≤ 2.1e-6 for these exports (the c48 export report:
  9.5e-7 and 1.8e-6) [V]. The export script fails if either exceeds 1e-4.
- Operators: Add, BatchNormalization, Cast, Concat, Constant, ConstantOfShape, Conv, ConvTranspose, Div, Expand, GRU,
  Gather, Gemm, Identity, LayerNormalization, MatMul, Mul, PRelu, Pad, Pow, ReduceMean, Reshape, Shape, Sigmoid, Slice,
  Sqrt, Squeeze, Sub, Tanh, Transpose, Unsqueeze. No ScatterND (the upstream graph has 18) and no If/Loop [V]. Support
  of every operator by the XNNPACK (Android) and CoreML (iOS) execution providers was not checked [U].
- Parameter counts were recomputed from `pgtcrn.py` in this session; byte sizes and hashes are from the files [V].
- No accuracy or quality statement is possible: the weights are random.

### 6.3 C# adapter: parity and latency

`StftStreamingTargetSpeakerExtractor` (`streaming-tse-stft-onnx/1`) does the STFT in C#, runs the model once per
256-sample hop with the owner embedding and recurrent state, and overlap-adds the inverse FFT.

- **Parity [V]:** fixture `tests/fixtures/tse/` (README.json): 2 s of `synthetic-overlap-A2-B2.wav`, a random unit
  embedding (seed 1234), and the expected output computed in Python (streaming STFT → ONNX Runtime 1.31 frame by frame
  → inverse FFT and overlap-add). `PgtcrnStftAdapterTests.Adapter_ReproducesThePythonStreamingReference`: max
  difference 3.3e-7 for 256-, 320- and 160-sample blocks (fixture README; [TEST_RESULTS.md](TEST_RESULTS.md) §4);
  the test fails above 1e-5.
- **Latency [V]:** overlap-add delay n_fft − hop = 256 samples, plus a constant alignment delay of
  hop − gcd(block, hop) so that every call returns as many samples as it received:

  | Block size | Overlap-add delay | Alignment delay | Total |
  |---|---:|---:|---:|
  | 256 samples (16 ms) | 256 | 0 | 256 samples = 16 ms |
  | 320 samples (20 ms, the pipeline's frames) | 256 | 192 | 448 samples = 28 ms |
  | 160 samples (10 ms) | 256 | 224 | 480 samples = 30 ms |

  The 448 is asserted by `Adapter_UsesTheSmallestConstantDelay_AndStaysContinuousWhenTheBlockSizeChanges`; the 480 is
  reported by the benchmark (`latency 480 samples` in `eval/results/tse-bench-host.json`).
- **No false claims [V]:** without an embedding from the required encoder the extractor copies audio unchanged and
  reports `NeedsReEnrollment` (`Adapter_PassesAudioThroughUnchanged_WithoutACompatibleTarget`); after an ONNX Runtime
  error it switches to pass-through and reports `Failed`; with no TSE model installed the
  `UnavailableTargetSpeakerExtractor` passes audio through and the UI says "Owner-voice extraction not installed - other
  voices are NOT removed" (`UserMessages.cs`).

### 6.4 Host compute (`eval/results/tse-bench-host.md`, generated 2026-10-09 09:32 UTC)

Quoted unchanged from the file (`tse-bench`, `tools/BonikPOS.VoiceSearch.Evaluation/Tse/TseBenchmark.cs`). This run
replaces an earlier one in which the C# adapter rows labelled "2 threads" had in fact run on one thread (the adapter
loads streaming models with `OnnxSessionPreferences.ForStreaming()`, which forced one thread); the benchmark now passes
the thread count as `StreamingThreads` too, so every row ran with the thread count shown.

> Measured on Ubuntu 24.04.5 LTS; X64; 4 logical CPUs; .NET 10.0.12, ONNX Runtime 1.31.0.0, CPU execution provider, 20 s of synthetic input per row (after warm-up). One frame = 256 new samples = 16 ms of audio; real-time factor = mean time per frame / 16 ms.

| Model | SHA-256 | Size | Mode | Threads | Frames | Mean ms | p50 ms | p95 ms | Max ms | RTF |
|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|
| gtcrn_simple.onnx | `b4718df6228e` | 523 KB | model only (1 frame / call) | 1 | 1250 | 2.007 | 1.760 | 3.181 | 5.852 | 0.1255 |
| gtcrn_simple.onnx | `b4718df6228e` | 523 KB | model only (1 frame / call) | 2 | 1250 | 2.594 | 2.249 | 3.787 | 21.018 | 0.1621 |
| ulunas_stream_simple.onnx | `f2e804d54d6a` | 770 KB | model only (1 frame / call) | 1 | 1250 | 1.955 | 1.889 | 2.337 | 4.219 | 0.1222 |
| ulunas_stream_simple.onnx | `f2e804d54d6a` | 770 KB | model only (1 frame / call) | 2 | 1250 | 3.092 | 2.858 | 4.700 | 9.507 | 0.1933 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | model only (1 frame / call) | 1 | 1250 | 1.151 | 1.027 | 1.802 | 6.568 | 0.0719 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 1 | 1250 | 1.587 | 1.429 | 2.436 | 9.419 | 0.0992 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | model only (1 frame / call) | 2 | 1250 | 1.885 | 1.647 | 2.762 | 10.481 | 0.1178 |
| pgtcrn-c16-e256-random.onnx | `fd26f859302f` | 329 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 2 | 1250 | 2.330 | 2.023 | 3.513 | 5.797 | 0.1456 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | model only (1 frame / call) | 1 | 1250 | 1.593 | 1.330 | 2.337 | 5.935 | 0.0996 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 1 | 1250 | 1.714 | 1.524 | 2.664 | 9.673 | 0.1071 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | model only (1 frame / call) | 2 | 1250 | 2.305 | 2.089 | 3.551 | 15.810 | 0.1441 |
| pgtcrn-c32-e256-random.onnx | `5d65e4910f84` | 563 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 2 | 1250 | 2.476 | 2.243 | 3.737 | 6.823 | 0.1548 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | model only (1 frame / call) | 1 | 1250 | 1.771 | 1.567 | 2.733 | 14.801 | 0.1107 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 1 | 1250 | 1.959 | 1.752 | 3.247 | 5.650 | 0.1224 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | model only (1 frame / call) | 2 | 1250 | 3.221 | 2.871 | 4.598 | 12.881 | 0.2013 |
| pgtcrn-c48-e256-random.onnx | `e526e65cd297` | 919 KB | C# adapter (STFT+model+iSTFT, 10 ms pieces) | 2 | 1250 | 2.940 | 2.778 | 4.381 | 7.370 | 0.1838 |

What the table shows (all from the file above):

- On one thread every model needs 1.0–1.9 ms (p50) per 16 ms frame; real-time factor 0.07–0.13. The pGTCRN rows
  are 1.0–1.8 ms p50 including the C# adapter, p95 at most 3.3 ms.
- Two threads are slower than one in every row, model only and through the adapter (e.g. pGTCRN c32 adapter: mean
  2.476 vs 1.714 ms; upstream GTCRN: 2.594 vs 2.007 ms). These per-frame graphs are too small to gain from a second
  thread; the app runs streaming models single-threaded.
- Cost grows only mildly with width (model only, one thread, mean): c16 1.151 ms, c32 1.593 ms, c48 1.771 ms.
- The C# adapter (STFT, model, inverse FFT, buffering) adds 0.12–0.44 ms mean per frame over the model-only rows on
  one thread.
- Our c16 export ran faster than the upstream GTCRN graph (mean 1.151 vs 2.007 ms, one thread); the export
  differences are listed in 6.2; the cause was not isolated.

Caveats:

- **x86-64 VM numbers, not phone numbers.** `uptime` reported a 1-minute load average of 0.67 just before this run
  (not recorded in the file); ONNX Runtime worker spinning off (`OnnxSessionPreferences.AllowSpinning` default),
  graph optimisation `ORT_ENABLE_ALL`, sequential execution.
- Inputs are random spectra (model only) or a synthetic tone plus noise (adapter); weights are random. The graphs have
  no data-dependent control flow (no If/Loop), so trained weights of the same shape run the same operations;
  value-dependent CPU effects (e.g. denormals) were not measured.

### 6.5 Inside the full pipeline (host)

`pipeline-bench` replays the synthetic clips in real time through capture, extraction (c16 random graph), VAD,
verification and recognition (`eval/results/pipeline-bench-host-optimized-run1.md`, `…-run2.md`; default settings:
warm-up, no spinning, single-threaded streaming models):

| Run | Started (UTC) | Load average at start | Frames (20 ms) | `tse.chunk_ms` p50 / p95 / max |
|---|---|---|---:|---|
| Optimized run 1 | 2026-10-09 08:55 | 5.42 | 2,610 | 3.3 / 6.6 / 29.1 ms |
| Optimized run 2 | 2026-10-09 09:00 | 5.99 | 2,600 | 3.0 / 6.2 / 18.4 ms |

A 20 ms frame triggers one or two model frames (320 / 256 = 1.25 on average). Both runs dropped 0 frames.

### 6.6 Android and iOS

**Not measured** (no devices were available). The in-app Diagnostics benchmark records `tse.chunk_ms` whenever a TSE
model is active (`src/BonikPOS.VoiceSearch.Audio/Benchmark/PipelineBenchmark.cs`); the procedure, including the CPU vs
accelerator comparison, is [BENCHMARKS.md](BENCHMARKS.md) §5. On a device the runtime may try XNNPACK (Android) or
CoreML (iOS) for this model and falls back to CPU (`OnnxSessionFactory`). The app refuses manifest entries marked
`isTestFixture` outside tests (`OnnxVoiceModelRuntime`), so timing the random-weight graphs on a phone needs either
trained weights or a developer-only timing manifest.

## 7. Recommendation and implementation

### 7.1 Recommendation

**Train pGTCRN in-house, starting with 32 channels, conditioned on the bundled GE2E encoder, on data with documented
commercial rights; keep target-speaker extraction reported as unavailable until the trained model passes
[EVALUATION_PLAN.md](EVALUATION_PLAN.md) §4.** The evidence behind it:

1. GTCRN was the only candidate that combined a permissive licence text, weights we downloaded and ran, a per-frame
   streaming ONNX contract and phone-scale size (sections 3, 6.1). Its weights are speaker-agnostic, so it is used as
   the architecture, not as a drop-in model.
2. Our re-implementation reproduces the upstream streaming model (≤ 9.9e-6, 6.1), so the backbone is verified before
   any change.
3. Speaker conditioning adds 8,224–24,672 parameters (FiLM) and keeps streaming exact (≤ 2.1e-6) with standard
   operators (6.2).
4. It conditions on the encoder the app already ships for owner verification (`ge2e-resemblyzer` 0.1.4,
   `models/speaker/ge2e-resemblyzer-0.1.4.onnx`, sha256 `f8e45a3d…`; Apache-2.0 per `models/models.json` and
   `research/raw/resemblyzer_LICENSE`). The app's C# GE2E encoder is checked against Resemblyzer in PyTorch
   (`ModelParityTests.Ge2eUtteranceEmbeddings_MatchResemblyzer`, ≤ 1e-5 per element), which is what the training
   script uses. No second encoder has to be licensed, shipped or enrolled.
5. Host compute leaves margin (real-time factor 0.09–0.13 on one thread for c16–c48, 6.4) and the algorithmic delay
   at 20 ms frames is 28 ms (6.3).
6. Weights we train carry the terms of our own training data — the only route found to weights whose rights can be
   documented.

Why 32 channels: on the host c32 costs the same as c16 (6.4) with three times the parameters (95,253 vs 31,893) and
576,005 bytes; c48 costs more. How much capacity extraction needs is unknown until training, and phone cost is
unmeasured: the width is a training flag (`--channels`) and the adapter reads all shapes from the graph, so c16 remains
a drop-in fallback.

### 7.2 What is implemented

| Component | Files | Verified by |
|---|---|---|
| Contract: `ITargetSpeakerExtractor` (`SetTarget`, `Process`, `Reset`), status with availability (`Active`, `NotInstalled`, `NeedsReEnrollment`, `IncompatibleModel`, `Failed`, `Disabled`), chunk size and latency | `src/BonikPOS.VoiceSearch.Core/Extraction/TargetSpeakerContracts.cs` | Unit and integration tests |
| **Adapter for the selected model**, format `streaming-tse-stft-onnx/1`: validates the graph against the manifest; C# STFT (periodic sqrt-Hann, 50 % overlap); one model run per hop with state feedback; inverse FFT and overlap-add; preallocated buffers bound to ONNX Runtime once; constant alignment delay; pass-through when inactive | `src/BonikPOS.VoiceSearch.AI/Extraction/StftStreamingTargetSpeakerExtractor.cs` | `tests/BonikPOS.VoiceSearch.IntegrationTests/ReferenceParity/PgtcrnStftAdapterTests.cs` (6.3) |
| Adapter for waveform-chunk models, format `streaming-tse-onnx/1` | `src/BonikPOS.VoiceSearch.AI/Extraction/OnnxStreamingTargetSpeakerExtractor.cs` | `tests/BonikPOS.VoiceSearch.UnitTests/AI/StreamingTseAdapterTests.cs`, `…/ReferenceParity/TsePlumbingFixtureTests.cs` |
| No-model fallback (pass-through, never reported as extraction) | `src/BonikPOS.VoiceSearch.AI/Extraction/UnavailableTargetSpeakerExtractor.cs` | `PgtcrnStftAdapterTests`, UI messages |
| Model selection: dispatch by manifest `format`; a TSE model conditioned on another encoder than the installed one is rejected; test fixtures are refused outside tests | `src/BonikPOS.VoiceSearch.AI/Runtime/OnnxVoiceModelRuntime.cs` | `TsePlumbingFixtureTests`, `StreamingTseAdapterTests` |
| pGTCRN architecture, training recipe, verification / export / fixture / registration tool, GTCRN licence notice | `tools/models/tse/pgtcrn.py`, `train_pgtcrn.py`, `export_pgtcrn_onnx.py`, `LICENSE-GTCRN` | 6.1, 6.2 |
| Compute benchmark (`tse-bench`) | `tools/BonikPOS.VoiceSearch.Evaluation/Tse/TseBenchmark.cs` | `eval/results/tse-bench-host.md` / `.json` |
| Random-weight fixture and Python reference I/O | `tests/fixtures/tse/` (README.json marks the weights as random) | `PgtcrnStftAdapterTests.Fixture_IsDeclaredAsRandomWeightsInItsReadme` |

Manifest options of `streaming-tse-stft-onnx/1` (`StftStreamingTseModel`): required `embedding_dim` and
`required_encoder_id`; optional `n_fft` (512), `hop` (256; must be n_fft / 2), `window` (only
`sqrt_hann_periodic`), `sample_rate` (only 16000), `spec_input` (`mix`), `spec_output` (`enh`), `embedding_input`
(`spk_emb`), `states` (list of `{input, output}`; default: every other input `X` paired with output `X_out`). Tensor
shapes are read from the graph; state shapes must be static. Both TSE contracts are specified in
[MODELS.md](MODELS.md) §3.5.

### 7.3 Training, export and registration recipe (as implemented)

- **Training** (`train_pgtcrn.py`): speech, noise and RIR manifests in JSON Lines (`path`, `speaker`, `license`,
  `corpus`). Lines whose licence is not in the allow-list (`CC0-1.0`, `CC-BY-4.0`, `CC-BY-3.0`, `PUBLIC-DOMAIN`,
  `APACHE-2.0`, `MIT`, `OWN-CONSENTED`) stop the run unless `--allow-noncommercial-research` is given, in which case
  checkpoints are marked "NON-COMMERCIAL RESEARCH ONLY". Mixtures are made on the fly: target utterance of speaker S,
  enrollment d-vector (Resemblyzer, the encoder the app ships as ONNX) from another utterance of S; 1–2 interfering
  talkers at SIR −5…10 dB with random shifts; noise at SNR 0–25 dB (80 % of the non-clean mixtures); a room impulse
  response (50 %); 15 % target-absent mixtures (output must be silent) and 10 % clean owner speech (must not be
  damaged). Loss: SI-SNR on the waveform plus power-compressed (0.3) complex spectral MSE; output energy is penalised
  when the target is absent. Defaults: 32 channels, 4 s segments, batch 8, Adam 1e-3, 200,000 steps, a checkpoint
  every 5,000 steps that records `data_licences` and `usage`. `--smoke` runs 20 steps on the bundled synthetic clips to
  check the code (one such smoke run was made in this session; its checkpoint is not in the repository and, as the
  script itself prints, does not extract speech).
- **Export** (`export_pgtcrn_onnx.py --export <file> --channels 32 --checkpoint <ckpt>`): opset 17, checks streaming
  vs offline and ONNX Runtime vs PyTorch (fails above 1e-4), writes `bonik.*` metadata (weights provenance, n_fft, hop,
  window, rate). `--fixture-dir` with `--fixture-audio` writes a new C# parity fixture.
- **Registration** (`--register models/models.json --license <verified licence> --source <data and recipe>`, together
  with `--export`): refused without `--checkpoint`, `--license` and `--source`, refused for checkpoints flagged
  `smoke_test`; random weights are never registered. It writes a `TargetSpeakerExtraction` entry
  (`streaming-tse-stft-onnx/1`, `required_encoder_id` default `ge2e-resemblyzer`) with SHA-256 checksums through
  `tools/models/register_model.py`. No trained checkpoint exists, so registration has not been run; the full command
  is in [MODELS.md](MODELS.md) §3.5.

## 8. Missing artefacts and next steps

### 8.1 Missing artefacts

| Missing | Why it matters | Status |
|---|---|---|
| Trained pGTCRN weights | Nothing is extracted without them | None. Only random-weight exports and one 20-step smoke checkpoint on synthetic clips exist |
| Multi-speaker training speech, noise and RIRs with documented commercial rights | The weights inherit the data terms; the training script refuses unlisted licences | Not acquired; what the evidence says is in 8.2 |
| Legal review | Licence texts were read, not legally assessed: training data, the GE2E / Resemblyzer weights and their training data, distribution of the GTCRN MIT notice | Not done |
| Android / iOS timings (CPU and XNNPACK / CoreML) | R8 can only be decided on phones | Not measured; procedure in [BENCHMARKS.md](BENCHMARKS.md) §5 |
| Real-world evaluation set: consented pharmacy recordings with overlapping speakers and owner enrollment | Quality gates | Not collected; [EVALUATION_PLAN.md](EVALUATION_PLAN.md) §1 and §4 |
| Third-party weights on Hugging Face, zenodo, Google Drive, GitHub release assets, ModelScope, wenet.org.cn | Only needed to re-test those candidates | Not obtained; the fetch log contains no request to these hosts |

### 8.2 Training-data licences seen in the evidence

| Corpus | What the evidence says | Tag | Training-script allow-list |
|---|---|---|---|
| LibriMix generation scripts | MIT (<https://raw.githubusercontent.com/JorisCos/LibriMix/master/LICENSE>); mixtures are "derived from LibriSpeech signals (clean subset) and WHAM noise" | [V] / [S] | Scripts only; the mixtures inherit LibriSpeech and WHAM terms |
| LibriSpeech, WHAM! noise, WSJ0 (-2mix), WHAMR!, MUSAN, MLS | No licence text in the evidence; the two www.openslr.org pages requested (`/12/`, `/94/`) failed (curl exit code 56) | [U] | — |
| DNS Challenge data (<https://raw.githubusercontent.com/microsoft/DNS-Challenge/master/README.md>, ICASSP 2023 edition) | Provided "under the original terms that Microsoft received such datasets"; clean speech: LibriVox public domain, PTDB-TUG ODbL 1.0, VocalSet CC BY 4.0, CREMA-D ODC-DbCL 1.0, VoxCeleb2 CC BY 4.0, VCTK ODC-By 1.0, Edinburgh 56-speaker (licence link only); noise: AudioSet CC BY 4.0, Freesound CC0 subset, DEMAND CC BY-SA 3.0; RIRs: OpenSLR26/28 Apache 2.0. The README also describes personalised (enrollment-based) data | [S] | Public domain, CC BY 4.0, CC0 and Apache-2.0 match entries; ODbL, ODC-DbCL, ODC-By and CC BY-SA do not and would be refused until reviewed |
| DNS3 (GTCRN / UL-UNAS training data) | Not covered by the fetched README edition | [U] | — |
| WeSpeaker models | "follows the license of it's corresponding dataset" (VoxCeleb: CC BY 4.0) | [S] | n/a (not used by pGTCRN) |
| Bangla / English owner speech | No corpus evidence collected; the app's queries are Bangla, English and Banglish (`eval/search/`) | [U] | `OWN-CONSENTED` for in-house recordings |

### 8.3 Next steps

1. **Data and legal:** build the JSON Lines manifests from corpora whose licence text has been reviewed; decide in the
   legal review whether to extend the allow-list; add consented in-house shop recordings (`OWN-CONSENTED`) covering the
   owners' languages.
2. **Align conditioning with the app before training:** (a) the app's encoder raises quiet input to −30 dBFS
   (increase only; `Ge2eSpeakerEncoder`, `normalize_dbfs: -30` in `models/models.json`), while `train_pgtcrn.py`
   embeds the raw file with `embed_utterance`; (b) the app conditions on the owner's enrollment centroid
   (`OwnerProfileService.ExtractionTarget`), while training uses one other utterance. Use the same normalisation and
   centroids of several utterances, or show robustness to the difference.
3. **Train** c32 (`--smoke` first), keeping each checkpoint's `data_licences` and `usage` fields.
4. **Export and re-verify** with `export_pgtcrn_onnx.py --export … --checkpoint …`, regenerate the parity fixture
   (`--fixture-dir`) and run `PgtcrnStftAdapterTests` on it.
5. **Evaluate** per [EVALUATION_PLAN.md](EVALUATION_PLAN.md) §4: SI-SDR improvement on simulated overlapping
   mixtures of held-out speakers; WER and medicine-name accuracy with and without extraction on overlapped and on
   single-speaker owner speech (no degradation allowed on the latter); target-confusion and over-suppression rates;
   latency and phone compute.
6. **Time on devices** (c16 and c32, CPU and accelerator) on the slowest supported Android phone and iPhone
   ([BENCHMARKS.md](BENCHMARKS.md) §5).
7. **Register and ship** only after 5 and 6 pass (`--register … --license … --source …`). Until then the app keeps
   reporting extraction as unavailable.

### 8.4 Issues found during the audit (both fixed)

- `TseBenchmark` adapter rows: the `Threads` column did not take effect for the adapter, because streaming models are
  loaded with `ForStreaming()`. The benchmark now passes the thread count as `StreamingThreads`; the table in 6.4 and
  [BENCHMARKS.md](BENCHMARKS.md) §2.3 were re-measured.
- `OnnxVoiceModelRuntime` trusted the manifest: it refused `isTestFixture` entries but did not read the
  `bonik.weights` ONNX metadata, so a manifest entry that omitted the flag could have loaded a random-weight
  (`RANDOM-UNTRAINED`) or smoke-test graph and reported extraction as active. The runtime now refuses any TSE export
  whose `bonik.weights` is not `trained:<sha256>` (and any Whisper export whose config says `RANDOM WEIGHTS`), with or
  without the flag (integration test `UntrainedGraph_IsRefusedByTheApp_EvenWhenTheManifestDoesNotFlagIt`).

## 9. Evidence index

Network note: the fetch log below records only the requests that were made. Separately, on 2026-10-09 the
development container's proxy refused connections to huggingface.co, openaipublic.azureedge.net, dl.google.com and
www.openslr.org (CONNECT 403 in the proxy's relay-failure log, reproduced with `curl`), and github.com release pages
answered HTTP 403. Weights hosted on Hugging Face or GitHub releases ([U] above) could therefore not have been fetched
from this environment; that is why none was downloaded, not a judgement about the weights.


- **Fetch log:** [evidence/tse-fetch-log.tsv](evidence/tse-fetch-log.tsv), a copy of `research/tse2/fetch_log.tsv` with
  only the scratch path prefix shortened. 118 requests: 113 HTTP 200, 3 HTTP 404 (two Apple documentation URL forms
  and `Xiaobin-Rong/SEtrain/main/LICENSE`; the `master` branch copy returned 200), 2 failed connections
  (www.openslr.org, curl exit code 56). Columns: UTC time, URL, HTTP status, curl exit code, bytes, SHA-256, local path.
- **Shallow clones** (`research/tse2/tools/tree.sh`: blob-less, no checkout; file lists in `research/tse2/tree_*.txt`).
  The GitHub REST API was not available to this session (`research/tse2/api_gtcrn/tree.json` holds the access-denied
  message), so commit identity comes from these clones:

  | Repository | Commit | Files |
  |---|---|---:|
  | Xiaobin-Rong/gtcrn | `502ebfab64da7c4a9af78dcb9c6ceef1ebb01c73` | 35 |
  | Xiaobin-Rong/ul-unas | `00f7c700da43d38347f30a6ccebd86fcbc798e07` | 16 |
  | Rikorose/DeepFilterNet | `d375b2d8309e0935d165700c91da9de862a99c31` | 168 |
  | ceva-ip/DPDFNet | `9bd9844a227bb6aa57e55588d8d0e961fcff1c46` | 46 |
  | etzinis/sudo_rm_rf | `cd00f2e21f7ad6281360cdf24ade36f84b0fbad6` | 78 |
  | k2-fsa/sherpa-onnx | `f28c1d1840ea23cf8fd31fb2936371982522b22c` | 6,623 |
  | maum-ai/voicefilter | `7db2930dacd94f33d44380bf07001fc7cab80e55` | 25 |
  | funcwj/voice-filter | `9daa53b74c4aa821ce3e4376b298b119b168f263` | 19 |
  | deciding/Voicefilter | `dda34da9d1cfca48102b2d1b4274bfd76e5a2e1c` | 43 |
  | wq2012/personal_vad | `4f4f46e60d4fb250c1161cb64c8161da53568e05` | 102 |
  | FireRedTeam/FireRedChat | `9d8c4b99fbd27380f6bf44f9b2f78ae615229a55` | 23 |
  | wenet-e2e/wesep | `dc11cb18ee8636064a7851f38b9348143c343cdc` | 289 |
  | espnet/espnet | `bf81520330a1e198a32011ff873f65dc0f46f14f` | 11,193 |
  | isHuangZiling/SEF-PNet | `e9635ae29504e0ac9090e4946c2ebdd232502f4d` | 42 |
  | isHuangZiling/D-LGTSE | `899f53ee0505152dcc7ce7bff10d20711ef327cc` | 56 |
  | isHuangZiling/UDSEF-PNet | none (directory empty) | — |

- **Other `research/tse2/` artefacts:** `gtcrn_inventory.tsv` (blob-hash match of the GTCRN downloads),
  `pypi_json/` and `pypi_summary.txt` (PyPI metadata for 29 names; `wesep`, `voicefilter`, `voicefilter-lite`,
  `speakerbeam`, `usef-tse`, `gtcrn`, `ul-unas`, `personalized-speech-enhancement` and `target-speaker-extraction` are
  not on PyPI), `whl_*/` (wheels), `dl_*/` (licences, READMEs, configs, checkpoints, ONNX files), `tools/` (pure-Python
  ONNX / checkpoint / wheel inspection scripts used for the [V] statements).
- **`research/raw/`** (no fetch log; cited by file name): `dicow_main_README.md`, `dicow_main_LICENSE`,
  `tsasr_main_README.md`, `tsasr_main_LICENSE`, `whisper_README.md`, `gsid_master_README.md`,
  `gsid_master_publications_VoiceFilter-Lite_index.html`, `gsid_master_publications_VoiceFilter_index.html`,
  `mindslab_voicefilter_README.md`, `mindslab_voicefilter_LICENSE`, `clearervoice_LICENSE`, `cv_tse_README.md`,
  `spex_master_README.md`, `spex_master_LICENSE`, `spexplus_master_README.md`, `spexplus_master_LICENSE`,
  `speakerbeam_README.md`, `speakerbeam_LICENSE`, `usef_main_README.md`, `usef_main_LICENSE`,
  `usef_config_config-USEF-TFGridNet.yaml`, `xtfgridnet_main_README.md`, `resemblyzer_LICENSE`; keyword-searched only:
  `fairseq_mms_README.md`, `omni_main_README.md`, `indicconf_master_README.md`, `vistaar_master_README.md`,
  `nemo_featured_models.rst`, `vosk_models.md`, `whisper_model-card.md`.
- **Session outputs outside the repository:** the c48 export report (parameters, operators, parity, metadata) and the
  smoke-training summary and export report.
- **Repository files:** `eval/results/tse-bench-host.md` / `.json`, `eval/results/pipeline-bench-host-optimized-run1.md`,
  `…-run2.md`, `eval/results/asr-bench-host.md`, `tests/fixtures/tse/README.json`, `models/models.json`, and the code
  and tests listed in 7.2.
