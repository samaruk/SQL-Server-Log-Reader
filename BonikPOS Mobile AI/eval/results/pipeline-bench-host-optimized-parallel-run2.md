### Pipeline benchmark: host x64 real-time, run 2: OPTIMIZED + opt-in parallel verification

- MEASURED on: Ubuntu 24.04.5 LTS; X64; .NET 10.0.12 (4 logical CPUs), 2026-10-09 09:00 UTC
- Options: 3 repetitions, real-time replay, warm-up on, verification parallel with recognition
- Note: Clips are replayed through FileAudioCapture in real time (10 ms callbacks); the platform microphone's own input latency is not included (see docs/BENCHMARKS.md).
- Note: End-of-speech-to-result is measured from the moment the end-pointer detected the end of speech, which is 500 ms of silence after the last word.
- Note: ONNX Runtime threads: per-utterance models auto (min(4, cores)), streaming models 1; worker spinning off.
- Note: Host load average (1/5/15 min) at start 2.57 2.53 2.10, at end 1.41 2.21 2.02.

#### CpuOnly

| Model role | Model | Execution provider | Init ms |
|---|---|---|---:|
| VoiceActivity | silero-vad@6.2.3 | CPU | 34 |
| SpeakerEncoder | ge2e-resemblyzer | CPU | 30 |
| TargetSpeakerExtraction | tse-benchmark | CPU | 174 |
| SpeechRecognition | whisper-random-shape-base | CPU | 980 |

Warm-up 328 ms; wall time 52.0 s; frames captured 2596, dropped 0, discontinuities 0, recognition-queue drops 0.
Process CPU 39 % of one core (10 % of all cores); peak managed memory 22 MB; peak working set 383 MB.

| Metric | Count | p50 ms | p95 ms | Max ms | Mean ms |
|---|---:|---:|---:|---:|---:|
| asr.total_ms | 9 | 286.3 | 324.9 | 329.3 | 281.7 |
| audio.capture_to_processed_ms | 2596 | 3.2 | 6.4 | 14.4 | 3.6 |
| pipeline.end_of_speech_to_outcome_ms | 15 | 251.9 | 325.5 | 329.4 | 195.7 |
| pipeline.frame_processing_ms | 2596 | 3.2 | 6.3 | 14.3 | 3.6 |
| speaker.verify_ms | 15 | 65.5 | 86.2 | 92.7 | 63.8 |
| tse.chunk_ms | 2596 | 2.9 | 5.8 | 14.3 | 3.2 |

| Clip | Rep | Outcome | Speech ms | Verify ms | ASR ms (enc / dec, tokens) | Search ms | End-of-speech -> outcome ms |
|---|---:|---|---:|---:|---|---:|---:|
| synthetic-voiceA-2.wav | 1 | nothing-recognized | 2432 | - | - (119 / 172, 8) | - | 324 |
| synthetic-en-napa.wav | 1 | nothing-recognized | 2560 | - | - (117 / 156, 8) | - | 288 |
| synthetic-en-seclo.wav | 1 | nothing-recognized | 2016 | - | - (105 / 179, 8) | - | 329 |
| synthetic-voiceB-2.wav | 1 | ignored:NotOwner | 2528 | 80 | - | - | 88 |
| synthetic-overlap-A2-B2.wav | 1 | ignored:NotOwner | 2528 | 70 | - | - | 71 |
| synthetic-voiceA-2.wav | 2 | nothing-recognized | 2432 | - | - (106 / 154, 8) | - | 271 |
| synthetic-en-napa.wav | 2 | nothing-recognized | 2560 | - | - (115 / 130, 8) | - | 258 |
| synthetic-en-seclo.wav | 2 | nothing-recognized | 2016 | - | - (92 / 149, 8) | - | 252 |
| synthetic-voiceB-2.wav | 2 | ignored:NotOwner | 2528 | 57 | - | - | 57 |
| synthetic-overlap-A2-B2.wav | 2 | ignored:NotOwner | 2528 | 65 | - | - | 66 |
| synthetic-voiceA-2.wav | 3 | nothing-recognized | 2432 | - | - (126 / 167, 8) | - | 305 |
| synthetic-en-napa.wav | 3 | nothing-recognized | 2560 | - | - (114 / 151, 8) | - | 286 |
| synthetic-en-seclo.wav | 3 | nothing-recognized | 2016 | - | - (74 / 142, 8) | - | 228 |
| synthetic-voiceB-2.wav | 3 | ignored:NotOwner | 2528 | 48 | - | - | 48 |
| synthetic-overlap-A2-B2.wav | 3 | ignored:NotOwner | 2528 | 62 | - | - | 63 |
