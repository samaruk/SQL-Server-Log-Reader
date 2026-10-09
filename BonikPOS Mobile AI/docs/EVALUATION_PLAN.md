# Evaluation plan

How each part of the owner-voice medicine search must be evaluated before it is trusted in a pharmacy, what has
already been measured, and what has not. The rule for every report: **measured numbers come from a run whose
command, data hash and date are recorded; targets are labelled as targets; nothing is estimated from a demo.**

| Component | Measured so far (all in this repository) | Not measured yet | Blocking data / artefact |
|---|---|---|---|
| C# feature front-ends and model plumbing | Parity with the Python references (integration tests; numbers in [TEST_RESULTS.md](TEST_RESULTS.md)) | — | — |
| Owner verification (GE2E, provisional thresholds) | Only plumbing on synthetic TTS clips (one voice accepted, another rejected) | FAR / FRR / EER on real voices | Consented recordings of owners and other speakers in shops |
| Speech recognition (Whisper ONNX) | Decoding parity on a random-weight fixture; compute cost of random-weight exports with published dimensions | Any accuracy (WER, medicine-name accuracy) | A real checkpoint export (download was blocked here) and recorded medicine queries |
| Target-speaker extraction | Adapter parity (3.3e-7) and compute cost of untrained pGTCRN graphs | Any extraction quality | Trained weights (see [TSE_MODEL_AUDIT.md](TSE_MODEL_AUDIT.md)) and overlapping-speech test sets |
| Medicine search | Top-1 / top-5 / false matches on developer-authored and synthetic 20k-product sets ([SEARCH_EVALUATION.md](SEARCH_EVALUATION.md)) | Accuracy on real recogniser output and the real catalog | Real catalog snapshot + labelled field queries |
| On-device performance | Host (x86-64 VM) pipeline, ASR and TSE benchmarks ([BENCHMARKS.md](BENCHMARKS.md)) | Every Android / iPhone number | Physical devices |
| End-to-end task success | — | Everything | A pilot in at least one pharmacy |

## 1. Data collection and consent (applies to every audio set)

- Recordings are made only with written, informed consent of every recorded person (owner, staff, customers), stating
  the purpose (evaluating this app), the retention period, who can access them and how to withdraw. Minors are not
  recorded.
- Evaluation builds record nothing by default. Audio for evaluation is captured with a separate, explicitly configured
  tool or recorder, never by the production app's search path (the app keeps no raw recordings; see
  [PRIVACY.md](PRIVACY.md)).
- Files are stored encrypted, access-controlled, outside the source repository; only labels and metrics are committed.
- Each recording gets a speaker pseudonym, device model, microphone distance, room/shop id and noise condition.
- Splits are by **speaker** (and by shop where possible): no speaker appears in both calibration/tuning data and test
  data. Test sets are frozen and hashed before any tuning; a test set that was looked at while tuning is retired.

### Proposed audio case format (`bonikpos-audio-eval/1`, JSON Lines) — not implemented in tooling yet

```json
{"id": "s012-u004", "audio": "s012/u004.wav", "speaker": "s012", "role": "owner|other", "shop": "shop-03",
 "device": "Pixel 7", "distance_cm": 60, "noise": "street|fan|quiet|music", "overlap": "none|partial|full",
 "other_speakers": ["s031"], "transcript": "নাপা এক্সট্রা পাঁচশো", "lang": "bn|en|banglish|mixed",
 "expect": {"products": ["P-102938"], "decisions": ["Confident", "NeedsConfirmation"]}}
```

`expect` uses the same semantics as `bonikpos-search-eval/1` ([SEARCH_EVALUATION.md](SEARCH_EVALUATION.md)):
any listed product id is correct, an empty list marks a negative case.

## 2. Owner voice verification

Model: GE2E (Resemblyzer 0.1.4) embeddings, cosine similarity to the enrolled centroid. The thresholds in
`models/models.json` are marked **PROVISIONAL** (`default_accept` 0.75, accept range 0.68–0.86, enrollment
consistency ≥ 0.70); they were not calibrated on real data.

- **Data:** ≥ 30 owners (balanced gender and age, Bangla and English speakers), each with an enrollment session and
  ≥ 40 test utterances recorded on later days; ≥ 100 impostor speakers (staff/customer roles) including same-gender
  and family members where consented; shop noise and phone-on-counter distances.
- **Metrics:** false-accept rate (another voice accepted as owner), false-reject rate (owner ignored), EER and a DET
  curve; FAR/FRR at the shipped threshold; per condition (noise, distance, utterance length, overlap); calibration
  of the `Uncertain` band (how often uncertain owners are later confirmed).
- **Procedure:** enrol through the app's enrollment flow (same consistency checks), score every test utterance with
  the same `OwnerVoiceVerifier`, sweep thresholds on a calibration split, report the chosen threshold on the frozen
  test split.
- **Targets (to be agreed with the product owner; not measured):** FAR ≤ 1 % at the shipped threshold with FRR ≤ 5 %
  on single-speaker owner speech; any change of threshold re-runs the test split.

## 3. Speech recognition (Bangla / English / Banglish medicine queries)

No accuracy number exists: the official Whisper checkpoints could not be downloaded in the development environment,
so only random-weight exports were used (for decoding parity and compute cost).

- **Data:** ≥ 2,000 recorded medicine queries from ≥ 50 speakers: brand only, brand + strength, brand + strength +
  form, generic names, Banglish pronunciations, Bangla numerals, conversational wrappers ("ভাই, নাপা আছে?").
  Transcribed by two annotators; disagreements adjudicated.
- **Metrics:** WER and CER per language; **medicine-name accuracy** (brand/generic token correct after
  normalisation); strength accuracy; language-identification accuracy (bn/en) and how often the second-language
  alternative contains the right medicine; no-speech handling; latency from the pipeline benchmark on target phones.
- **Comparison:** each candidate export (model size, `--n-audio-ctx`, float vs int8) on the same frozen set;
  the smallest model that meets the end-to-end target (section 6) on the slowest supported phone wins.

## 4. Target-speaker extraction

No trained model exists ([TSE_MODEL_AUDIT.md](TSE_MODEL_AUDIT.md)); the app reports extraction as unavailable and
never claims other voices were removed.

- **Data:** (a) simulated mixtures from held-out speakers of a commercially licensed corpus (owner + interfering
  talker + shop noise, SIR −5…+10 dB), with the enrollment taken from different utterances; (b) real overlapped
  recordings from the consented set (section 1) with close-talk references where possible.
- **Metrics:** SI-SDR improvement on (a); downstream WER and medicine-name accuracy with and without extraction on
  (b) and on single-speaker owner speech; **target-speaker confusion** (output follows the wrong talker) and
  **over-suppression** (owner speech attenuated) rates; streaming latency (448 samples = 28 ms for 20 ms frames,
  measured by the adapter) and compute on phones.
- **Gates before enabling by default (targets, not measured):** no significant degradation on single-speaker owner
  speech (WER within +1 % absolute), a clear improvement on overlapped speech, confusion rate low enough that
  verification + confirmation still prevent wrong results, and real-time on the slowest supported phone with margin.

## 5. Medicine search

Implemented and run: `tools/BonikPOS.VoiceSearch.Evaluation search`, format `bonikpos-search-eval/1`; results and
limitations in [SEARCH_EVALUATION.md](SEARCH_EVALUATION.md). Still required:

- A snapshot of the real ~20,000-product BonikPOS catalog (hash recorded in each report).
- Labelled queries from section 3's recordings, run **through the real recogniser output** (not the reference
  transcript), plus typed queries from owners.
- Metrics as today: top-1, top-5, false matches (Confident or NeedsConfirmation with a wrong top-1), confident false
  matches (must be 0), wrong-offered rate on negatives, correct-but-low-confidence rate.

## 6. End-to-end (pilot)

- **Setting:** at least one pharmacy, owner enrolled, real catalog, app in normal use for ≥ 2 weeks with the owner's
  consent; customers informed by a notice; no audio retained by the app.
- **Logged (numbers only, on device, exported by the owner):** outcome per voice query (found & confirmed, corrected
  by typing, no match, ignored as not-owner, nothing recognised), decision type, end-of-speech-to-result latency,
  frames dropped. The Diagnostics screen already records these metrics locally; an export with explicit owner action
  would be needed.
- **Primary metrics:** task success rate (correct product reached without typing), confident-wrong rate (target 0),
  owner-ignored rate, other-voice-accepted incidents (owner reported), median and p95 time to result.

## 7. On-device performance

Procedure and current host results: [BENCHMARKS.md](BENCHMARKS.md). Every phone result must state device, OS,
app build, models (ids and hashes), accelerator setting, battery state, and whether the run was real-time.
