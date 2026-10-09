### After changes, synthetic-20k DEV (thresholds tuned on this split)

- Dataset: `synthetic-20k-dev.cases.jsonl` (sha256 `008167bd913491cf…`), 2600 cases: 2000 positive, 600 negative
- Catalog: Local product index (synthetic-catalog-20k.json); 20000 products (sha256 `539870d42230de34…`); index build 1461 ms
- Run: 2026-10-09 07:53 UTC on Ubuntu 24.04.5 LTS; X64; 4 logical CPUs; .NET 10.0.12
- Thresholds: min candidate 0.55, confident 0.90, ambiguity margin 0.06, min confirm 0.75, min fuzzy confirm 0.84, min recognition confidence 0.45

| Metric | Value |
|---|---|
| Top-1 accuracy (positives) | 99.5 % (1989/2000) |
| Top-5 accuracy (positives) | 99.7 % (1994/2000) |
| False matches (all cases) | 0.3 % (8/2600) |
| — on positive cases | 0.1 % (2/2000) |
| — on negative cases | 1.0 % (6/600) |
| False matches with decision Confident | 0.0 % (0/2600) |
| Wrong product offered (false match or all-wrong Ambiguous), all cases | 0.7 % (17/2600) |
| — on negative cases | 2.3 % (14/600) |
| Correct top-1 but answered LowConfidence/NoMatch (positives) | 2.3 % (46/2000) |
| Negatives safely handled (NoMatch/LowConfidence) | 97.7 % (586/600) |
| Negatives answered NoMatch | 4.7 % (28/600) |
| Positives answered NoMatch (missed) | 0.1 % (2/2000) |
| Decision agreement (cases with expected decision) | 100.0 % (98/98) |
| Decisions | Confident 1157, NeedsConfirmation 430, Ambiguous 374, LowConfidence 609, NoMatch 30 |
| Matcher latency p50 / p95 / max | 2.31 / 5.41 / 81.89 ms |

| Category | Cases | Top-1 | Top-5 | False matches | Wrong offered | Negatives safe |
|---|---:|---:|---:|---:|---:|---:|
| asr-split | 222 | 99 % (220/222) | 99 % (220/222) | 1 | 2 | – |
| bangla-script | 318 | 99 % (314/318) | 100 % (317/318) | 1 | 1 | – |
| banglish-respelling | 265 | 98 % (261/265) | 99 % (262/265) | 0 | 0 | – |
| brand-form | 213 | 100 % (213/213) | 100 % (213/213) | 0 | 0 | – |
| brand-only | 174 | 100 % (174/174) | 100 % (174/174) | 0 | 0 | – |
| brand-strength | 466 | 100 % (466/466) | 100 % (466/466) | 0 | 0 | – |
| generic-strength | 111 | 100 % (111/111) | 100 % (111/111) | 0 | 0 | – |
| neg-form-mismatch | 150 | – | – | 0 | 0 | 100 % (150/150) |
| neg-not-medicine | 30 | – | – | 0 | 1 | 97 % (29/30) |
| neg-strength-mismatch | 246 | – | – | 0 | 0 | 100 % (246/246) |
| neg-unknown-brand | 174 | – | – | 6 | 13 | 93 % (161/174) |
| typo | 231 | 100 % (230/231) | 100 % (231/231) | 0 | 0 | – |

Failures (25; wrong or missing top-1, false matches, unexpected decisions):

- `syn-00178` "khohine 0.1" → LowConfidence; correct product not ranked; top: SYN-011797 Cosine 300 mg Tablet [0.65]
- `syn-00619` "do guk five" → Ambiguous; wrong products offered as a choice; top: SYN-016975 Gukane 125 mg/5 ml Powder for Suspension [0.85]
- `syn-00837` "জিরো চার" → NoMatch; correct product not ranked; top: (none)
- `syn-00951` "mosuol 100 mcg" → LowConfidence; correct at rank 3; top: SYN-008184 Moseeol 600 mg + 400 IU Tablet [0.69]
- `syn-01030` "স্পোসী বিশ" → Ambiguous; correct at rank 3; top: SYN-001689 Speshi 20 mg Capsule [0.91]
- `syn-01094` "do hir five hundred" → NeedsConfirmation; FALSE MATCH; top: SYN-007052 Hiraiin 500 mg Tablet [0.84]
- `syn-01175` "phahpak 500" → LowConfidence; correct at rank 3; top: SYN-009355 Foheepaac 200 mg + 0.2 mg Capsule [0.73]
- `syn-01177` "ফাজে ১০০" → LowConfidence; correct at rank 2; top: SYN-000427 Baje 100 mg + 200 mg + 200 mcg Tablet [0.82]
- `syn-01287` "ডৌজীড একশো সিরাপ" → NeedsConfirmation; FALSE MATCH; top: SYN-014047 Dojiid 100 mg/5 ml Powder for Suspension [0.88]
- `syn-01617` "khile 80 milligram" → NoMatch; correct product not ranked; top: (none)
- `syn-01639` "khetaik 30 mg" → LowConfidence; correct product not ranked; top: SYN-014676 Cutiac 500 mg Tablet [0.58]
- `syn-02027` "Stepreeta 500" → NeedsConfirmation; FALSE MATCH; top: SYN-017501 Steeprut 500 mg Capsule [0.88]
- `syn-02043` "Rogaiax" → NeedsConfirmation; FALSE MATCH; top: SYN-014497 Rugaiix 1% Cream [0.87]
- `syn-02048` "close the door" → Ambiguous; wrong products offered as a choice; top: SYN-016694 Clocee 250 mg Tablet [0.89]
- `syn-02058` "Phocloix" → Ambiguous; wrong products offered as a choice; top: SYN-001976 Phiclouix 250 mg Chewable Tablet [0.89]
- `syn-02077` "Pleeleax" → NeedsConfirmation; FALSE MATCH; top: SYN-008606 Plouleeax 0.3% Eye Drops [0.89]
- `syn-02148` "Prodad" → Ambiguous; wrong products offered as a choice; top: SYN-006862 Predeed 50 mg Capsule [0.85]
- `syn-02247` "Foslocoon" → NeedsConfirmation; FALSE MATCH; top: SYN-010174 Foslucoin 40 mg Injection [0.89]
- `syn-02278` "Fluzuix" → Ambiguous; wrong products offered as a choice; top: SYN-016743 Flizoix 50 mcg Tablet [0.89]
- `syn-02281` "Nijaid" → Ambiguous; wrong products offered as a choice; top: SYN-013682 Nichaid 500 mg + 125 mg Tablet [0.88]
- `syn-02291` "Prebroax 20" → NeedsConfirmation; FALSE MATCH; top: SYN-006790 Probreeax 20 mg/2 ml Injection [0.90]
- `syn-02365` "Trecliex" → Ambiguous; wrong products offered as a choice; top: SYN-012919 Traclaix 150 mg Tablet [0.85]
- `syn-02376` "Flagaon 500" → NeedsConfirmation; FALSE MATCH; top: SYN-000104 Fligaiin 500 mg Tablet [0.87]
- `syn-02531` "Nispoen" → Ambiguous; wrong products offered as a choice; top: SYN-012097 Nispouna 250 mg Tablet [0.87]
- `syn-02583` "Laclat" → Ambiguous; wrong products offered as a choice; top: SYN-004491 Liclit 20 mg Tablet [0.85]
