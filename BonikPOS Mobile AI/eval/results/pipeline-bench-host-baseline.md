### Pipeline benchmark: host x64 real-time: BASELINE (previous scheduling: sequential verification, no warm-up, ORT spinning, 4-thread streaming models)

- MEASURED on: Ubuntu 24.04.5 LTS; X64; .NET 10.0.12 (4 logical CPUs), 2026-10-09 08:34 UTC
- Options: 3 repetitions, real-time replay, warm-up off, verification before recognition
- Note: Clips are replayed through FileAudioCapture in real time (10 ms callbacks); the platform microphone's own input latency is not included (see docs/BENCHMARKS.md).
- Note: End-of-speech-to-result is measured from the moment the end-pointer detected the end of speech, which is 500 ms of silence after the last word.
- Note: ONNX Runtime threads: per-utterance models auto (min(4, cores)), streaming models same; worker spinning on.
- Note: Host load average (1/5/15 min) at start 3.99 3.07 2.09, at end 6.70 4.03 2.48.

#### CpuOnly

| Model role | Model | Execution provider | Init ms |
|---|---|---|---:|
| VoiceActivity | silero-vad@6.2.3 | CPU | 30 |
| SpeakerEncoder | ge2e-resemblyzer | CPU | 28 |
| TargetSpeakerExtraction | tse-benchmark | CPU | 165 |
| SpeechRecognition | whisper-random-shape-base | CPU | 1143 |

Warm-up 0 ms; wall time 56.6 s; frames captured 2755, dropped 0, discontinuities 0, recognition-queue drops 0.
Process CPU 372 % of one core (93 % of all cores); peak managed memory 19 MB; peak working set 383 MB.

| Metric | Count | p50 ms | p95 ms | Max ms | Mean ms |
|---|---:|---:|---:|---:|---:|
| asr.total_ms | 9 | 559.9 | 653.2 | 665.1 | 575.3 |
| audio.capture_to_processed_ms | 2755 | 3.9 | 16.1 | 39.9 | 6.4 |
| pipeline.end_of_speech_to_outcome_ms | 15 | 643.6 | 755.9 | 773.9 | 493.0 |
| pipeline.frame_processing_ms | 2755 | 3.8 | 15.8 | 39.9 | 6.2 |
| speaker.verify_ms | 15 | 140.7 | 214.0 | 214.2 | 147.0 |
| tse.chunk_ms | 2755 | 3.2 | 12.7 | 38.0 | 4.9 |

| Clip | Rep | Outcome | Speech ms | Verify ms | ASR ms (enc / dec, tokens) | Search ms | End-of-speech -> outcome ms |
|---|---:|---|---:|---:|---|---:|---:|
| synthetic-voiceA-2.wav | 1 | nothing-recognized | 2464 | - | - (199 / 359, 8) | - | 703 |
| synthetic-en-napa.wav | 1 | nothing-recognized | 2560 | - | - (384 / 160, 8) | - | 774 |
| synthetic-en-seclo.wav | 1 | nothing-recognized | 2016 | - | - (370 / 274, 8) | - | 740 |
| synthetic-voiceB-2.wav | 1 | ignored:NotOwner | 2592 | 141 | - | - | 141 |
| synthetic-overlap-A2-B2.wav | 1 | ignored:NotOwner | 2528 | 214 | - | - | 215 |
| synthetic-voiceA-2.wav | 2 | nothing-recognized | 2464 | - | - (296 / 177, 8) | - | 736 |
| synthetic-en-napa.wav | 2 | nothing-recognized | 2560 | - | - (243 / 252, 8) | - | 639 |
| synthetic-en-seclo.wav | 2 | nothing-recognized | 2016 | - | - (292 / 306, 8) | - | 728 |
| synthetic-voiceB-2.wav | 2 | ignored:NotOwner | 2592 | 162 | - | - | 162 |
| synthetic-overlap-A2-B2.wav | 2 | ignored:NotOwner | 2528 | 193 | - | - | 193 |
| synthetic-voiceA-2.wav | 3 | nothing-recognized | 2464 | - | - (227 / 273, 8) | - | 664 |
| synthetic-en-napa.wav | 3 | nothing-recognized | 2560 | - | - (274 / 237, 8) | - | 644 |
| synthetic-en-seclo.wav | 3 | nothing-recognized | 2016 | - | - (408 / 183, 8) | - | 748 |
| synthetic-voiceB-2.wav | 3 | ignored:NotOwner | 2592 | 150 | - | - | 150 |
| synthetic-overlap-A2-B2.wav | 3 | ignored:NotOwner | 2528 | 158 | - | - | 159 |
