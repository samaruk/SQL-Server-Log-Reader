### Pipeline benchmark: host x64 real-time, run 1: OPTIMIZED DEFAULT (warm-up, no spinning, 1-thread streaming models, cancellable ASR; verification before recognition)

- MEASURED on: Ubuntu 24.04.5 LTS; X64; .NET 10.0.12 (4 logical CPUs), 2026-10-09 08:55 UTC
- Options: 3 repetitions, real-time replay, warm-up on, verification before recognition
- Note: Clips are replayed through FileAudioCapture in real time (10 ms callbacks); the platform microphone's own input latency is not included (see docs/BENCHMARKS.md).
- Note: End-of-speech-to-result is measured from the moment the end-pointer detected the end of speech, which is 500 ms of silence after the last word.
- Note: ONNX Runtime threads: per-utterance models auto (min(4, cores)), streaming models 1; worker spinning off.
- Note: Host load average (1/5/15 min) at start 5.42 2.56 2.03, at end 2.40 2.20 1.93.

#### CpuOnly

| Model role | Model | Execution provider | Init ms |
|---|---|---|---:|
| VoiceActivity | silero-vad@6.2.3 | CPU | 27 |
| SpeakerEncoder | ge2e-resemblyzer | CPU | 27 |
| TargetSpeakerExtraction | tse-benchmark | CPU | 152 |
| SpeechRecognition | whisper-random-shape-base | CPU | 895 |

Warm-up 353 ms; wall time 52.3 s; frames captured 2610, dropped 0, discontinuities 0, recognition-queue drops 0.
Process CPU 38 % of one core (10 % of all cores); peak managed memory 42 MB; peak working set 395 MB.

| Metric | Count | p50 ms | p95 ms | Max ms | Mean ms |
|---|---:|---:|---:|---:|---:|
| asr.total_ms | 9 | 252.0 | 287.7 | 290.6 | 252.9 |
| audio.capture_to_processed_ms | 2610 | 3.6 | 7.3 | 29.7 | 4.2 |
| pipeline.end_of_speech_to_outcome_ms | 15 | 250.7 | 363.5 | 378.4 | 211.8 |
| pipeline.frame_processing_ms | 2610 | 3.6 | 7.2 | 29.6 | 4.1 |
| speaker.verify_ms | 15 | 62.5 | 77.4 | 78.4 | 59.4 |
| tse.chunk_ms | 2610 | 3.3 | 6.6 | 29.1 | 3.7 |

| Clip | Rep | Outcome | Speech ms | Verify ms | ASR ms (enc / dec, tokens) | Search ms | End-of-speech -> outcome ms |
|---|---:|---|---:|---:|---|---:|---:|
| synthetic-voiceA-2.wav | 1 | nothing-recognized | 2432 | - | - (111 / 162, 8) | - | 378 |
| synthetic-en-napa.wav | 1 | nothing-recognized | 2560 | - | - (83 / 143, 8) | - | 308 |
| synthetic-en-seclo.wav | 1 | nothing-recognized | 2016 | - | - (70 / 140, 8) | - | 251 |
| synthetic-voiceB-2.wav | 1 | ignored:NotOwner | 2528 | 75 | - | - | 75 |
| synthetic-overlap-A2-B2.wav | 1 | ignored:NotOwner | 2528 | 77 | - | - | 77 |
| synthetic-voiceA-2.wav | 2 | nothing-recognized | 2432 | - | - (71 / 137, 8) | - | 286 |
| synthetic-en-napa.wav | 2 | nothing-recognized | 2560 | - | - (92 / 152, 8) | - | 331 |
| synthetic-en-seclo.wav | 2 | nothing-recognized | 2016 | - | - (94 / 148, 8) | - | 283 |
| synthetic-voiceB-2.wav | 2 | ignored:NotOwner | 2528 | 56 | - | - | 56 |
| synthetic-overlap-A2-B2.wav | 2 | ignored:NotOwner | 2528 | 65 | - | - | 65 |
| synthetic-voiceA-2.wav | 3 | nothing-recognized | 2432 | - | - (100 / 161, 8) | - | 342 |
| synthetic-en-napa.wav | 3 | nothing-recognized | 2560 | - | - (94 / 168, 8) | - | 357 |
| synthetic-en-seclo.wav | 3 | nothing-recognized | 2016 | - | - (68 / 139, 8) | - | 244 |
| synthetic-voiceB-2.wav | 3 | ignored:NotOwner | 2528 | 62 | - | - | 62 |
| synthetic-overlap-A2-B2.wav | 3 | ignored:NotOwner | 2528 | 62 | - | - | 63 |
