### Pipeline benchmark: host x64 real-time, run 2: OPTIMIZED DEFAULT (warm-up, no spinning, 1-thread streaming models, cancellable ASR; verification before recognition)

- MEASURED on: Ubuntu 24.04.5 LTS; X64; .NET 10.0.12 (4 logical CPUs), 2026-10-09 09:00 UTC
- Options: 3 repetitions, real-time replay, warm-up on, verification before recognition
- Note: Clips are replayed through FileAudioCapture in real time (10 ms callbacks); the platform microphone's own input latency is not included (see docs/BENCHMARKS.md).
- Note: End-of-speech-to-result is measured from the moment the end-pointer detected the end of speech, which is 500 ms of silence after the last word.
- Note: ONNX Runtime threads: per-utterance models auto (min(4, cores)), streaming models 1; worker spinning off.
- Note: Host load average (1/5/15 min) at start 5.99 2.96 2.21, at end 2.57 2.53 2.10.

#### CpuOnly

| Model role | Model | Execution provider | Init ms |
|---|---|---|---:|
| VoiceActivity | silero-vad@6.2.3 | CPU | 38 |
| SpeakerEncoder | ge2e-resemblyzer | CPU | 27 |
| TargetSpeakerExtraction | tse-benchmark | CPU | 146 |
| SpeechRecognition | whisper-random-shape-base | CPU | 986 |

Warm-up 354 ms; wall time 52.1 s; frames captured 2600, dropped 0, discontinuities 0, recognition-queue drops 0.
Process CPU 37 % of one core (9 % of all cores); peak managed memory 18 MB; peak working set 378 MB.

| Metric | Count | p50 ms | p95 ms | Max ms | Mean ms |
|---|---:|---:|---:|---:|---:|
| asr.total_ms | 9 | 236.1 | 272.4 | 281.2 | 239.9 |
| audio.capture_to_processed_ms | 2600 | 3.3 | 6.8 | 19.0 | 3.8 |
| pipeline.end_of_speech_to_outcome_ms | 15 | 259.1 | 325.1 | 353.2 | 197.6 |
| pipeline.frame_processing_ms | 2600 | 3.3 | 6.7 | 18.9 | 3.7 |
| speaker.verify_ms | 15 | 56.1 | 69.7 | 77.1 | 53.0 |
| tse.chunk_ms | 2600 | 3.0 | 6.2 | 18.4 | 3.4 |

| Clip | Rep | Outcome | Speech ms | Verify ms | ASR ms (enc / dec, tokens) | Search ms | End-of-speech -> outcome ms |
|---|---:|---|---:|---:|---|---:|---:|
| synthetic-voiceA-2.wav | 1 | nothing-recognized | 2432 | - | - (79 / 175, 8) | - | 353 |
| synthetic-en-napa.wav | 1 | nothing-recognized | 2560 | - | - (73 / 131, 8) | - | 269 |
| synthetic-en-seclo.wav | 1 | nothing-recognized | 2016 | - | - (88 / 130, 8) | - | 259 |
| synthetic-voiceB-2.wav | 1 | ignored:NotOwner | 2528 | 63 | - | - | 64 |
| synthetic-overlap-A2-B2.wav | 1 | ignored:NotOwner | 2528 | 66 | - | - | 67 |
| synthetic-voiceA-2.wav | 2 | nothing-recognized | 2432 | - | - (84 / 150, 8) | - | 302 |
| synthetic-en-napa.wav | 2 | nothing-recognized | 2560 | - | - (78 / 147, 8) | - | 288 |
| synthetic-en-seclo.wav | 2 | nothing-recognized | 2016 | - | - (69 / 146, 8) | - | 252 |
| synthetic-voiceB-2.wav | 2 | ignored:NotOwner | 2528 | 54 | - | - | 55 |
| synthetic-overlap-A2-B2.wav | 2 | ignored:NotOwner | 2528 | 77 | - | - | 77 |
| synthetic-voiceA-2.wav | 3 | nothing-recognized | 2432 | - | - (75 / 129, 8) | - | 279 |
| synthetic-en-napa.wav | 3 | nothing-recognized | 2560 | - | - (83 / 157, 8) | - | 313 |
| synthetic-en-seclo.wav | 3 | nothing-recognized | 2016 | - | - (84 / 135, 8) | - | 264 |
| synthetic-voiceB-2.wav | 3 | ignored:NotOwner | 2528 | 64 | - | - | 64 |
| synthetic-overlap-A2-B2.wav | 3 | ignored:NotOwner | 2528 | 57 | - | - | 58 |
