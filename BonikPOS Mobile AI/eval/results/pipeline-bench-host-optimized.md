### Pipeline benchmark: host x64 real-time: OPTIMIZED (parallel verification, warm-up, no spinning, 1-thread streaming models, cancellable ASR)

- MEASURED on: Ubuntu 24.04.5 LTS; X64; .NET 10.0.12 (4 logical CPUs), 2026-10-09 08:35 UTC
- Options: 3 repetitions, real-time replay, warm-up on, verification parallel with recognition
- Note: Clips are replayed through FileAudioCapture in real time (10 ms callbacks); the platform microphone's own input latency is not included (see docs/BENCHMARKS.md).
- Note: End-of-speech-to-result is measured from the moment the end-pointer detected the end of speech, which is 500 ms of silence after the last word.
- Note: ONNX Runtime threads: per-utterance models auto (min(4, cores)), streaming models 1; worker spinning off.
- Note: Host load average (1/5/15 min) at start 6.70 4.03 2.48, at end 2.76 3.38 2.35.

#### CpuOnly

| Model role | Model | Execution provider | Init ms |
|---|---|---|---:|
| VoiceActivity | silero-vad@6.2.3 | CPU | 27 |
| SpeakerEncoder | ge2e-resemblyzer | CPU | 21 |
| TargetSpeakerExtraction | tse-benchmark | CPU | 121 |
| SpeechRecognition | whisper-random-shape-base | CPU | 920 |

Warm-up 343 ms; wall time 51.9 s; frames captured 2591, dropped 0, discontinuities 0, recognition-queue drops 0.
Process CPU 38 % of one core (10 % of all cores); peak managed memory 20 MB; peak working set 378 MB.

| Metric | Count | p50 ms | p95 ms | Max ms | Mean ms |
|---|---:|---:|---:|---:|---:|
| asr.total_ms | 9 | 262.8 | 287.2 | 287.9 | 257.6 |
| audio.capture_to_processed_ms | 2591 | 3.3 | 6.6 | 19.3 | 3.7 |
| pipeline.end_of_speech_to_outcome_ms | 15 | 236.8 | 289.0 | 291.4 | 185.6 |
| pipeline.frame_processing_ms | 2591 | 3.2 | 6.6 | 19.2 | 3.6 |
| speaker.verify_ms | 15 | 72.5 | 84.6 | 87.7 | 65.4 |
| tse.chunk_ms | 2591 | 3.0 | 6.0 | 18.5 | 3.3 |

| Clip | Rep | Outcome | Speech ms | Verify ms | ASR ms (enc / dec, tokens) | Search ms | End-of-speech -> outcome ms |
|---|---:|---|---:|---:|---|---:|---:|
| synthetic-voiceA-2.wav | 1 | nothing-recognized | 2464 | - | - (108 / 151, 8) | - | 291 |
| synthetic-en-napa.wav | 1 | nothing-recognized | 2560 | - | - (88 / 166, 8) | - | 267 |
| synthetic-en-seclo.wav | 1 | nothing-recognized | 2016 | - | - (112 / 159, 8) | - | 285 |
| synthetic-voiceB-2.wav | 1 | ignored:NotOwner | 2592 | 75 | - | - | 78 |
| synthetic-overlap-A2-B2.wav | 1 | ignored:NotOwner | 2528 | 73 | - | - | 73 |
| synthetic-voiceA-2.wav | 2 | nothing-recognized | 2464 | - | - (97 / 142, 8) | - | 251 |
| synthetic-en-napa.wav | 2 | nothing-recognized | 2560 | - | - (99 / 171, 8) | - | 288 |
| synthetic-en-seclo.wav | 2 | nothing-recognized | 2016 | - | - (100 / 151, 8) | - | 263 |
| synthetic-voiceB-2.wav | 2 | ignored:NotOwner | 2592 | 83 | - | - | 83 |
| synthetic-overlap-A2-B2.wav | 2 | ignored:NotOwner | 2528 | 66 | - | - | 66 |
| synthetic-voiceA-2.wav | 3 | nothing-recognized | 2464 | - | - (104 / 139, 8) | - | 257 |
| synthetic-en-napa.wav | 3 | nothing-recognized | 2560 | - | - (98 / 125, 8) | - | 237 |
| synthetic-en-seclo.wav | 3 | nothing-recognized | 2016 | - | - (62 / 114, 8) | - | 186 |
| synthetic-voiceB-2.wav | 3 | ignored:NotOwner | 2592 | 88 | - | - | 88 |
| synthetic-overlap-A2-B2.wav | 3 | ignored:NotOwner | 2528 | 70 | - | - | 70 |
