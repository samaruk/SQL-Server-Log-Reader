### Pipeline benchmark: host x64 real-time, run 2: BASELINE (previous scheduling: no warm-up, ORT spinning, 4-thread streaming models)

- MEASURED on: Ubuntu 24.04.5 LTS; X64; .NET 10.0.12 (4 logical CPUs), 2026-10-09 08:59 UTC
- Options: 3 repetitions, real-time replay, warm-up off, verification before recognition
- Note: Clips are replayed through FileAudioCapture in real time (10 ms callbacks); the platform microphone's own input latency is not included (see docs/BENCHMARKS.md).
- Note: End-of-speech-to-result is measured from the moment the end-pointer detected the end of speech, which is 500 ms of silence after the last word.
- Note: ONNX Runtime threads: per-utterance models auto (min(4, cores)), streaming models same; worker spinning on.
- Note: Host load average (1/5/15 min) at start 1.20 1.71 1.78, at end 5.99 2.96 2.21.

#### CpuOnly

| Model role | Model | Execution provider | Init ms |
|---|---|---|---:|
| VoiceActivity | silero-vad@6.2.3 | CPU | 35 |
| SpeakerEncoder | ge2e-resemblyzer | CPU | 39 |
| TargetSpeakerExtraction | tse-benchmark | CPU | 151 |
| SpeechRecognition | whisper-random-shape-base | CPU | 971 |

Warm-up 0 ms; wall time 57.5 s; frames captured 2761, dropped 0, discontinuities 0, recognition-queue drops 0.
Process CPU 354 % of one core (88 % of all cores); peak managed memory 25 MB; peak working set 385 MB.

| Metric | Count | p50 ms | p95 ms | Max ms | Mean ms |
|---|---:|---:|---:|---:|---:|
| asr.total_ms | 9 | 596.6 | 881.5 | 964.4 | 637.4 |
| audio.capture_to_processed_ms | 2761 | 4.0 | 16.6 | 49.4 | 6.8 |
| pipeline.end_of_speech_to_outcome_ms | 15 | 697.0 | 934.2 | 1202.1 | 552.7 |
| pipeline.frame_processing_ms | 2761 | 3.8 | 16.1 | 42.7 | 6.4 |
| speaker.verify_ms | 15 | 165.9 | 253.2 | 307.5 | 169.0 |
| tse.chunk_ms | 2761 | 3.2 | 13.5 | 38.9 | 4.9 |

| Clip | Rep | Outcome | Speech ms | Verify ms | ASR ms (enc / dec, tokens) | Search ms | End-of-speech -> outcome ms |
|---|---:|---|---:|---:|---|---:|---:|
| synthetic-voiceA-2.wav | 1 | nothing-recognized | 2432 | - | - (518 / 345, 8) | - | 1202 |
| synthetic-en-napa.wav | 1 | nothing-recognized | 2560 | - | - (360 / 164, 8) | - | 729 |
| synthetic-en-seclo.wav | 1 | nothing-recognized | 2016 | - | - (396 / 333, 8) | - | 819 |
| synthetic-voiceB-2.wav | 1 | ignored:NotOwner | 2528 | 200 | - | - | 201 |
| synthetic-overlap-A2-B2.wav | 1 | ignored:NotOwner | 2528 | 133 | - | - | 133 |
| synthetic-voiceA-2.wav | 2 | nothing-recognized | 2432 | - | - (355 / 155, 8) | - | 784 |
| synthetic-en-napa.wav | 2 | nothing-recognized | 2560 | - | - (372 / 186, 8) | - | 786 |
| synthetic-en-seclo.wav | 2 | nothing-recognized | 2016 | - | - (315 / 242, 8) | - | 697 |
| synthetic-voiceB-2.wav | 2 | ignored:NotOwner | 2528 | 308 | - | - | 308 |
| synthetic-overlap-A2-B2.wav | 2 | ignored:NotOwner | 2528 | 115 | - | - | 115 |
| synthetic-voiceA-2.wav | 3 | nothing-recognized | 2432 | - | - (395 / 181, 8) | - | 761 |
| synthetic-en-napa.wav | 3 | nothing-recognized | 2560 | - | - (266 / 184, 8) | - | 660 |
| synthetic-en-seclo.wav | 3 | nothing-recognized | 2016 | - | - (234 / 346, 8) | - | 700 |
| synthetic-voiceB-2.wav | 3 | ignored:NotOwner | 2528 | 206 | - | - | 206 |
| synthetic-overlap-A2-B2.wav | 3 | ignored:NotOwner | 2528 | 191 | - | - | 191 |
