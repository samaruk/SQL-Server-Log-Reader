### After changes, demo-authored (used to find bugs; optimistic)

- Dataset: `demo-authored.cases.jsonl` (sha256 `03df8859a73ca120…`), 246 cases: 213 positive, 33 negative
- Catalog: DEMO catalog (illustrative test data - connect the BonikPOS catalog for real use); 55 products; index build 65 ms
- Run: 2026-10-09 07:53 UTC on Ubuntu 24.04.5 LTS; X64; 4 logical CPUs; .NET 10.0.12
- Thresholds: min candidate 0.55, confident 0.90, ambiguity margin 0.06, min confirm 0.75, min fuzzy confirm 0.84, min recognition confidence 0.45

| Metric | Value |
|---|---|
| Top-1 accuracy (positives) | 100.0 % (213/213) |
| Top-5 accuracy (positives) | 100.0 % (213/213) |
| False matches (all cases) | 0.4 % (1/246) |
| — on positive cases | 0.0 % (0/213) |
| — on negative cases | 3.0 % (1/33) |
| False matches with decision Confident | 0.0 % (0/246) |
| Wrong product offered (false match or all-wrong Ambiguous), all cases | 0.4 % (1/246) |
| — on negative cases | 3.0 % (1/33) |
| Correct top-1 but answered LowConfidence/NoMatch (positives) | 3.8 % (8/213) |
| Negatives safely handled (NoMatch/LowConfidence) | 97.0 % (32/33) |
| Negatives answered NoMatch | 63.6 % (21/33) |
| Positives answered NoMatch (missed) | 0.0 % (0/213) |
| Decision agreement (cases with expected decision) | 100.0 % (8/8) |
| Decisions | Confident 147, NeedsConfirmation 41, Ambiguous 18, LowConfidence 19, NoMatch 21 |
| Matcher latency p50 / p95 / max | 0.55 / 1.69 / 58.99 ms |

| Category | Cases | Top-1 | Top-5 | False matches | Wrong offered | Negatives safe |
|---|---:|---:|---:|---:|---:|---:|
| ambiguous | 5 | 100 % (5/5) | 100 % (5/5) | 0 | 0 | – |
| asr-nbest | 6 | 100 % (6/6) | 100 % (6/6) | 0 | 0 | – |
| asr-split | 21 | 100 % (21/21) | 100 % (21/21) | 0 | 0 | – |
| banglish | 14 | 100 % (14/14) | 100 % (14/14) | 0 | 0 | – |
| bn-number-words | 15 | 100 % (15/15) | 100 % (15/15) | 0 | 0 | – |
| bn-script | 31 | 100 % (31/31) | 100 % (31/31) | 0 | 0 | – |
| bn-transliteration | 17 | 100 % (17/17) | 100 % (17/17) | 0 | 0 | – |
| cap-low-asr-confidence | 1 | 100 % (1/1) | 100 % (1/1) | 0 | 0 | – |
| cap-speaker-uncertain | 2 | 100 % (2/2) | 100 % (2/2) | 0 | 0 | – |
| en-exact | 17 | 100 % (17/17) | 100 % (17/17) | 0 | 0 | – |
| en-form | 11 | 100 % (11/11) | 100 % (11/11) | 0 | 0 | – |
| en-number-words | 10 | 100 % (10/10) | 100 % (10/10) | 0 | 0 | – |
| en-strength | 21 | 100 % (21/21) | 100 % (21/21) | 0 | 0 | – |
| en-strength-form | 3 | 100 % (3/3) | 100 % (3/3) | 0 | 0 | – |
| generic | 26 | 100 % (26/26) | 100 % (26/26) | 0 | 0 | – |
| neg-form-mismatch | 4 | – | – | 0 | 0 | 100 % (4/4) |
| neg-gibberish | 2 | – | – | 0 | 0 | 100 % (2/2) |
| neg-not-medicine | 8 | – | – | 0 | 0 | 100 % (8/8) |
| neg-strength-mismatch | 7 | – | – | 0 | 0 | 100 % (7/7) |
| neg-unknown-brand | 12 | – | – | 1 | 1 | 92 % (11/12) |
| quantity | 6 | 100 % (6/6) | 100 % (6/6) | 0 | 0 | – |
| typo | 27 | 100 % (27/27) | 100 % (27/27) | 0 | 0 | – |

Failures (1; wrong or missing top-1, false matches, unexpected decisions):

- `demo-229` "nexium 20" → NeedsConfirmation; FALSE MATCH; top: DEMO-0017 Nexum 20 mg Tablet [0.92]
