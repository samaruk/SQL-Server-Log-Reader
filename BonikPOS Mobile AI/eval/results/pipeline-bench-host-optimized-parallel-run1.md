### Pipeline benchmark: host x64 real-time, run 1: OPTIMIZED + opt-in parallel verification

- MEASURED on: Ubuntu 24.04.5 LTS; X64; .NET 10.0.12 (4 logical CPUs), 2026-10-09 08:56 UTC
- Options: 3 repetitions, real-time replay, warm-up on, verification parallel with recognition
- Note: Clips are replayed through FileAudioCapture in real time (10 ms callbacks); the platform microphone's own input latency is not included (see docs/BENCHMARKS.md).
- Note: End-of-speech-to-result is measured from the moment the end-pointer detected the end of speech, which is 500 ms of silence after the last word.
- Note: ONNX Runtime threads: per-utterance models auto (min(4, cores)), streaming models 1; worker spinning off.
- Note: Host load average (1/5/15 min) at start 2.40 2.20 1.93, at end 1.08 1.87 1.84.

#### CpuOnly

| Model role | Model | Execution provider | Init ms |
|---|---|---|---:|
| VoiceActivity | silero-vad@6.2.3 | CPU | 54 |
| SpeakerEncoder | ge2e-resemblyzer | CPU | 30 |
| TargetSpeakerExtraction | tse-benchmark | CPU | 218 |
| SpeechRecognition | whisper-random-shape-base | CPU | 994 |

Warm-up 343 ms; wall time 52.1 s; frames captured 2598, dropped 0, discontinuities 0, recognition-queue drops 0.
Process CPU 40 % of one core (10 % of all cores); peak managed memory 40 MB; peak working set 395 MB.

| Metric | Count | p50 ms | p95 ms | Max ms | Mean ms |
|---|---:|---:|---:|---:|---:|
| asr.total_ms | 9 | 276.6 | 292.2 | 293.6 | 277.1 |
| audio.capture_to_processed_ms | 2598 | 3.5 | 7.2 | 20.5 | 4.0 |
| pipeline.end_of_speech_to_outcome_ms | 15 | 266.7 | 291.3 | 293.7 | 197.6 |
| pipeline.frame_processing_ms | 2598 | 3.4 | 7.2 | 20.5 | 4.0 |
| speaker.verify_ms | 15 | 68.9 | 82.1 | 91.2 | 64.3 |
| tse.chunk_ms | 2598 | 3.2 | 6.5 | 19.8 | 3.6 |

| Clip | Rep | Outcome | Speech ms | Verify ms | ASR ms (enc / dec, tokens) | Search ms | End-of-speech -> outcome ms |
|---|---:|---|---:|---:|---|---:|---:|
| synthetic-voiceA-2.wav | 1 | nothing-recognized | 2432 | - | - (96 / 162, 8) | - | 284 |
| synthetic-en-napa.wav | 1 | nothing-recognized | 2560 | - | - (98 / 160, 8) | - | 272 |
| synthetic-en-seclo.wav | 1 | nothing-recognized | 2016 | - | - (94 / 151, 8) | - | 267 |
| synthetic-voiceB-2.wav | 1 | ignored:NotOwner | 2528 | 78 | - | - | 80 |
| synthetic-overlap-A2-B2.wav | 1 | ignored:NotOwner | 2528 | 72 | - | - | 73 |
| synthetic-voiceA-2.wav | 2 | nothing-recognized | 2432 | - | - (109 / 140, 8) | - | 263 |
| synthetic-en-napa.wav | 2 | nothing-recognized | 2560 | - | - (116 / 161, 8) | - | 290 |
| synthetic-en-seclo.wav | 2 | nothing-recognized | 2016 | - | - (107 / 175, 8) | - | 294 |
| synthetic-voiceB-2.wav | 2 | ignored:NotOwner | 2528 | 69 | - | - | 69 |
| synthetic-overlap-A2-B2.wav | 2 | ignored:NotOwner | 2528 | 90 | - | - | 91 |
| synthetic-voiceA-2.wav | 3 | nothing-recognized | 2432 | - | - (121 / 139, 8) | - | 274 |
| synthetic-en-napa.wav | 3 | nothing-recognized | 2560 | - | - (106 / 164, 8) | - | 282 |
| synthetic-en-seclo.wav | 3 | nothing-recognized | 2016 | - | - (100 / 162, 8) | - | 277 |
| synthetic-voiceB-2.wav | 3 | ignored:NotOwner | 2528 | 71 | - | - | 71 |
| synthetic-overlap-A2-B2.wav | 3 | ignored:NotOwner | 2528 | 77 | - | - | 78 |
