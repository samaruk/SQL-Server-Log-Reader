### Pipeline benchmark: host x64 real-time, run 1: BASELINE (previous scheduling: no warm-up, ORT spinning, 4-thread streaming models)

- MEASURED on: Ubuntu 24.04.5 LTS; X64; .NET 10.0.12 (4 logical CPUs), 2026-10-09 08:54 UTC
- Options: 3 repetitions, real-time replay, warm-up off, verification before recognition
- Note: Clips are replayed through FileAudioCapture in real time (10 ms callbacks); the platform microphone's own input latency is not included (see docs/BENCHMARKS.md).
- Note: End-of-speech-to-result is measured from the moment the end-pointer detected the end of speech, which is 500 ms of silence after the last word.
- Note: ONNX Runtime threads: per-utterance models auto (min(4, cores)), streaming models same; worker spinning on.
- Note: Host load average (1/5/15 min) at start 0.54 1.32 1.62, at end 5.42 2.56 2.03.

#### CpuOnly

| Model role | Model | Execution provider | Init ms |
|---|---|---|---:|
| VoiceActivity | silero-vad@6.2.3 | CPU | 36 |
| SpeakerEncoder | ge2e-resemblyzer | CPU | 24 |
| TargetSpeakerExtraction | tse-benchmark | CPU | 196 |
| SpeechRecognition | whisper-random-shape-base | CPU | 791 |

Warm-up 0 ms; wall time 56.5 s; frames captured 2752, dropped 0, discontinuities 0, recognition-queue drops 0.
Process CPU 373 % of one core (93 % of all cores); peak managed memory 25 MB; peak working set 385 MB.

| Metric | Count | p50 ms | p95 ms | Max ms | Mean ms |
|---|---:|---:|---:|---:|---:|
| asr.total_ms | 9 | 533.8 | 655.4 | 669.6 | 557.9 |
| audio.capture_to_processed_ms | 2752 | 5.3 | 15.1 | 34.0 | 6.7 |
| pipeline.end_of_speech_to_outcome_ms | 15 | 666.0 | 744.1 | 799.5 | 485.8 |
| pipeline.frame_processing_ms | 2752 | 4.8 | 15.0 | 34.0 | 6.5 |
| speaker.verify_ms | 15 | 153.7 | 204.3 | 215.1 | 150.0 |
| tse.chunk_ms | 2752 | 3.2 | 13.2 | 33.4 | 5.1 |

| Clip | Rep | Outcome | Speech ms | Verify ms | ASR ms (enc / dec, tokens) | Search ms | End-of-speech -> outcome ms |
|---|---:|---|---:|---:|---|---:|---:|
| synthetic-voiceA-2.wav | 1 | nothing-recognized | 2432 | - | - (231 / 217, 8) | - | 720 |
| synthetic-en-napa.wav | 1 | nothing-recognized | 2560 | - | - (266 / 205, 8) | - | 670 |
| synthetic-en-seclo.wav | 1 | nothing-recognized | 2016 | - | - (240 / 287, 8) | - | 671 |
| synthetic-voiceB-2.wav | 1 | ignored:NotOwner | 2528 | 177 | - | - | 180 |
| synthetic-overlap-A2-B2.wav | 1 | ignored:NotOwner | 2528 | 215 | - | - | 215 |
| synthetic-voiceA-2.wav | 2 | nothing-recognized | 2432 | - | - (313 / 168, 8) | - | 666 |
| synthetic-en-napa.wav | 2 | nothing-recognized | 2560 | - | - (316 / 194, 8) | - | 665 |
| synthetic-en-seclo.wav | 2 | nothing-recognized | 2016 | - | - (298 / 287, 8) | - | 701 |
| synthetic-voiceB-2.wav | 2 | ignored:NotOwner | 2528 | 155 | - | - | 156 |
| synthetic-overlap-A2-B2.wav | 2 | ignored:NotOwner | 2528 | 149 | - | - | 149 |
| synthetic-voiceA-2.wav | 3 | nothing-recognized | 2432 | - | - (185 / 323, 8) | - | 710 |
| synthetic-en-napa.wav | 3 | nothing-recognized | 2560 | - | - (339 / 166, 8) | - | 693 |
| synthetic-en-seclo.wav | 3 | nothing-recognized | 2016 | - | - (397 / 215, 8) | - | 799 |
| synthetic-voiceB-2.wav | 3 | ignored:NotOwner | 2528 | 186 | - | - | 187 |
| synthetic-overlap-A2-B2.wav | 3 | ignored:NotOwner | 2528 | 102 | - | - | 105 |
