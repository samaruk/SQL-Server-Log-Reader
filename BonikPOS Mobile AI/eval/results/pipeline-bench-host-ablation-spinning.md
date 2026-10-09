### Pipeline benchmark: host x64 real-time: ablation - optimized default but ORT spinning on

- MEASURED on: Ubuntu 24.04.5 LTS; X64; .NET 10.0.12 (4 logical CPUs), 2026-10-09 08:57 UTC
- Options: 3 repetitions, real-time replay, warm-up on, verification before recognition
- Note: Clips are replayed through FileAudioCapture in real time (10 ms callbacks); the platform microphone's own input latency is not included (see docs/BENCHMARKS.md).
- Note: End-of-speech-to-result is measured from the moment the end-pointer detected the end of speech, which is 500 ms of silence after the last word.
- Note: ONNX Runtime threads: per-utterance models auto (min(4, cores)), streaming models 1; worker spinning on.
- Note: Host load average (1/5/15 min) at start 1.08 1.87 1.84, at end 1.26 1.82 1.82.

#### CpuOnly

| Model role | Model | Execution provider | Init ms |
|---|---|---|---:|
| VoiceActivity | silero-vad@6.2.3 | CPU | 34 |
| SpeakerEncoder | ge2e-resemblyzer | CPU | 26 |
| TargetSpeakerExtraction | tse-benchmark | CPU | 103 |
| SpeechRecognition | whisper-random-shape-base | CPU | 895 |

Warm-up 378 ms; wall time 53.1 s; frames captured 2646, dropped 0, discontinuities 0, recognition-queue drops 0.
Process CPU 53 % of one core (13 % of all cores); peak managed memory 35 MB; peak working set 392 MB.

| Metric | Count | p50 ms | p95 ms | Max ms | Mean ms |
|---|---:|---:|---:|---:|---:|
| asr.total_ms | 9 | 321.0 | 404.7 | 412.3 | 339.9 |
| audio.capture_to_processed_ms | 2646 | 3.3 | 6.7 | 23.6 | 3.8 |
| pipeline.end_of_speech_to_outcome_ms | 15 | 344.4 | 469.5 | 487.3 | 265.1 |
| pipeline.frame_processing_ms | 2646 | 3.2 | 6.6 | 23.5 | 3.7 |
| speaker.verify_ms | 15 | 56.5 | 82.0 | 83.7 | 60.5 |
| tse.chunk_ms | 2646 | 3.0 | 5.9 | 23.0 | 3.3 |

| Clip | Rep | Outcome | Speech ms | Verify ms | ASR ms (enc / dec, tokens) | Search ms | End-of-speech -> outcome ms |
|---|---:|---|---:|---:|---|---:|---:|
| synthetic-voiceA-2.wav | 1 | nothing-recognized | 2432 | - | - (136 / 167, 8) | - | 407 |
| synthetic-en-napa.wav | 1 | nothing-recognized | 2560 | - | - (101 / 270, 8) | - | 462 |
| synthetic-en-seclo.wav | 1 | nothing-recognized | 2016 | - | - (148 / 149, 8) | - | 329 |
| synthetic-voiceB-2.wav | 1 | ignored:NotOwner | 2528 | 54 | - | - | 54 |
| synthetic-overlap-A2-B2.wav | 1 | ignored:NotOwner | 2528 | 50 | - | - | 50 |
| synthetic-voiceA-2.wav | 2 | nothing-recognized | 2432 | - | - (146 / 213, 8) | - | 457 |
| synthetic-en-napa.wav | 2 | nothing-recognized | 2560 | - | - (126 / 182, 8) | - | 377 |
| synthetic-en-seclo.wav | 2 | nothing-recognized | 2016 | - | - (150 / 212, 8) | - | 487 |
| synthetic-voiceB-2.wav | 2 | ignored:NotOwner | 2528 | 73 | - | - | 73 |
| synthetic-overlap-A2-B2.wav | 2 | ignored:NotOwner | 2528 | 51 | - | - | 51 |
| synthetic-voiceA-2.wav | 3 | nothing-recognized | 2432 | - | - (100 / 211, 8) | - | 407 |
| synthetic-en-napa.wav | 3 | nothing-recognized | 2560 | - | - (127 / 162, 8) | - | 355 |
| synthetic-en-seclo.wav | 3 | nothing-recognized | 2016 | - | - (116 / 167, 8) | - | 344 |
| synthetic-voiceB-2.wav | 3 | ignored:NotOwner | 2528 | 56 | - | - | 57 |
| synthetic-overlap-A2-B2.wav | 3 | ignored:NotOwner | 2528 | 65 | - | - | 65 |
