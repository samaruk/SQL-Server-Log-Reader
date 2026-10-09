### Pipeline benchmark: host x64 real-time: ablation - optimized default but 4-thread streaming models

- MEASURED on: Ubuntu 24.04.5 LTS; X64; .NET 10.0.12 (4 logical CPUs), 2026-10-09 08:58 UTC
- Options: 3 repetitions, real-time replay, warm-up on, verification before recognition
- Note: Clips are replayed through FileAudioCapture in real time (10 ms callbacks); the platform microphone's own input latency is not included (see docs/BENCHMARKS.md).
- Note: End-of-speech-to-result is measured from the moment the end-pointer detected the end of speech, which is 500 ms of silence after the last word.
- Note: ONNX Runtime threads: per-utterance models auto (min(4, cores)), streaming models same; worker spinning off.
- Note: Host load average (1/5/15 min) at start 1.26 1.82 1.82, at end 1.20 1.71 1.78.

#### CpuOnly

| Model role | Model | Execution provider | Init ms |
|---|---|---|---:|
| VoiceActivity | silero-vad@6.2.3 | CPU | 29 |
| SpeakerEncoder | ge2e-resemblyzer | CPU | 21 |
| TargetSpeakerExtraction | tse-benchmark | CPU | 96 |
| SpeechRecognition | whisper-random-shape-base | CPU | 838 |

Warm-up 325 ms; wall time 52.4 s; frames captured 2615, dropped 0, discontinuities 0, recognition-queue drops 0.
Process CPU 43 % of one core (11 % of all cores); peak managed memory 35 MB; peak working set 394 MB.

| Metric | Count | p50 ms | p95 ms | Max ms | Mean ms |
|---|---:|---:|---:|---:|---:|
| asr.total_ms | 9 | 273.1 | 352.4 | 394.1 | 275.0 |
| audio.capture_to_processed_ms | 2615 | 4.0 | 8.2 | 24.5 | 4.5 |
| pipeline.end_of_speech_to_outcome_ms | 15 | 275.9 | 379.2 | 458.5 | 219.1 |
| pipeline.frame_processing_ms | 2615 | 4.0 | 8.2 | 24.5 | 4.5 |
| speaker.verify_ms | 15 | 57.8 | 69.7 | 74.3 | 53.7 |
| tse.chunk_ms | 2615 | 3.6 | 7.3 | 24.0 | 4.0 |

| Clip | Rep | Outcome | Speech ms | Verify ms | ASR ms (enc / dec, tokens) | Search ms | End-of-speech -> outcome ms |
|---|---:|---|---:|---:|---|---:|---:|
| synthetic-voiceA-2.wav | 1 | nothing-recognized | 2432 | - | - (114 / 141, 8) | - | 345 |
| synthetic-en-napa.wav | 1 | nothing-recognized | 2560 | - | - (72 / 136, 8) | - | 276 |
| synthetic-en-seclo.wav | 1 | nothing-recognized | 2016 | - | - (86 / 150, 8) | - | 272 |
| synthetic-voiceB-2.wav | 1 | ignored:NotOwner | 2528 | 68 | - | - | 68 |
| synthetic-overlap-A2-B2.wav | 1 | ignored:NotOwner | 2528 | 74 | - | - | 74 |
| synthetic-voiceA-2.wav | 2 | nothing-recognized | 2432 | - | - (174 / 202, 8) | - | 459 |
| synthetic-en-napa.wav | 2 | nothing-recognized | 2560 | - | - (90 / 160, 8) | - | 333 |
| synthetic-en-seclo.wav | 2 | nothing-recognized | 2016 | - | - (99 / 170, 8) | - | 304 |
| synthetic-voiceB-2.wav | 2 | ignored:NotOwner | 2528 | 55 | - | - | 56 |
| synthetic-overlap-A2-B2.wav | 2 | ignored:NotOwner | 2528 | 46 | - | - | 46 |
| synthetic-voiceA-2.wav | 3 | nothing-recognized | 2432 | - | - (69 / 139, 8) | - | 277 |
| synthetic-en-napa.wav | 3 | nothing-recognized | 2560 | - | - (104 / 161, 8) | - | 341 |
| synthetic-en-seclo.wav | 3 | nothing-recognized | 2016 | - | - (129 / 150, 8) | - | 319 |
| synthetic-voiceB-2.wav | 3 | ignored:NotOwner | 2528 | 59 | - | - | 59 |
| synthetic-overlap-A2-B2.wav | 3 | ignored:NotOwner | 2528 | 58 | - | - | 58 |
