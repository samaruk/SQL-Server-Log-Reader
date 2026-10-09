### FINAL held-out: after changes, synthetic-20k TEST (seed 11, phrase set C; thresholds chosen on DEV)

- Dataset: `synthetic-20k-test.cases.jsonl` (sha256 `f51299e722c329d3…`), 2600 cases: 2000 positive, 600 negative
- Catalog: Local product index (synthetic-catalog-20k.json); 20000 products (sha256 `539870d42230de34…`); index build 1576 ms
- Run: 2026-10-09 07:53 UTC on Ubuntu 24.04.5 LTS; X64; 4 logical CPUs; .NET 10.0.12
- Thresholds: min candidate 0.55, confident 0.90, ambiguity margin 0.06, min confirm 0.75, min fuzzy confirm 0.84, min recognition confidence 0.45

| Metric | Value |
|---|---|
| Top-1 accuracy (positives) | 98.9 % (1978/2000) |
| Top-5 accuracy (positives) | 99.7 % (1993/2000) |
| False matches (all cases) | 0.4 % (10/2600) |
| — on positive cases | 0.2 % (4/2000) |
| — on negative cases | 1.0 % (6/600) |
| False matches with decision Confident | 0.0 % (0/2600) |
| Wrong product offered (false match or all-wrong Ambiguous), all cases | 0.8 % (22/2600) |
| — on negative cases | 3.0 % (18/600) |
| Correct top-1 but answered LowConfidence/NoMatch (positives) | 2.8 % (56/2000) |
| Negatives safely handled (NoMatch/LowConfidence) | 97.0 % (582/600) |
| Negatives answered NoMatch | 1.2 % (7/600) |
| Positives answered NoMatch (missed) | 0.0 % (0/2000) |
| Decision agreement (cases with expected decision) | 100.0 % (101/101) |
| Decisions | Confident 1131, NeedsConfirmation 411, Ambiguous 407, LowConfidence 644, NoMatch 7 |
| Matcher latency p50 / p95 / max | 2.21 / 4.57 / 58.71 ms |

| Category | Cases | Top-1 | Top-5 | False matches | Wrong offered | Negatives safe |
|---|---:|---:|---:|---:|---:|---:|
| asr-split | 191 | 97 % (186/191) | 99 % (189/191) | 1 | 1 | – |
| bangla-script | 303 | 99 % (301/303) | 100 % (302/303) | 0 | 0 | – |
| banglish-respelling | 243 | 99 % (240/243) | 100 % (242/243) | 2 | 2 | – |
| brand-form | 240 | 100 % (240/240) | 100 % (240/240) | 0 | 0 | – |
| brand-only | 183 | 100 % (183/183) | 100 % (183/183) | 0 | 0 | – |
| brand-strength | 507 | 100 % (507/507) | 100 % (507/507) | 0 | 0 | – |
| generic-strength | 112 | 100 % (112/112) | 100 % (112/112) | 0 | 0 | – |
| neg-form-mismatch | 145 | – | – | 0 | 0 | 100 % (145/145) |
| neg-not-medicine | 30 | – | – | 0 | 6 | 80 % (24/30) |
| neg-strength-mismatch | 236 | – | – | 0 | 0 | 100 % (236/236) |
| neg-unknown-brand | 189 | – | – | 6 | 12 | 94 % (177/189) |
| typo | 221 | 95 % (209/221) | 99 % (218/221) | 1 | 1 | – |

Failures (40; wrong or missing top-1, false matches, unexpected decisions):

- `syn-00004` "phicliat 5" → Ambiguous; correct at rank 2; top: SYN-006531 Ficlit 5 mg Tablet [0.92]
- `syn-00050` "ডীকি ১৮০" → LowConfidence; correct product not ranked; top: SYN-002166 Dico 100 mg + 200 mg + 200 mcg Tablet [0.74]
- `syn-00187` "clhoc five hundred mg" → NeedsConfirmation; FALSE MATCH; top: SYN-014614 Cloko 500 mg + 200 IU Tablet [0.89]
- `syn-00312` "nakhin 20" → LowConfidence; correct product not ranked; top: SYN-013333 Nacaon 5 mg Tablet [0.70]
- `syn-00335` "vubaita 100 mcg" → LowConfidence; correct at rank 2; top: SYN-007007 Bubaina 100 mcg/puff Inhaler [0.78]
- `syn-00700` "fagel 5" → NeedsConfirmation; FALSE MATCH; top: SYN-011886 Phagil 10 mg/5 ml Syrup [0.91]
- `syn-00721` "zazvora fifty" → Ambiguous; correct at rank 2; top: SYN-006220 Zozabora 50 mg Tablet [0.87]
- `syn-00773` "boguhuor 0.9" → Ambiguous; correct at rank 2; top: SYN-012182 Bogahouor 0.9% Infusion [0.88]
- `syn-00798` "me hom 0.9" → LowConfidence; correct at rank 9; top: SYN-003687 Homal 1% Cream [0.73]
- `syn-00895` "ki soon twenty" → LowConfidence; correct at rank 4; top: SYN-009636 Shona 200 mg/5 ml Powder for Suspension [0.73]
- `syn-00931` "do pre eighty" → NeedsConfirmation; FALSE MATCH; top: SYN-010720 Prebeeix 80 mg Tablet [0.83]
- `syn-01049` "ki clouan five hundred mg" → LowConfidence; correct at rank 2; top: SYN-005895 Clouvaan 500 mg Tablet [0.81]
- `syn-01101` "torhox 20" → LowConfidence; correct product not ranked; top: SYN-009968 Storeex 20 mg Capsule [0.77]
- `syn-01131` "ফাইশোএন সাড়ে সাতশো" → LowConfidence; correct at rank 2; top: SYN-011973 Phaishoen 500 mg Tablet [0.75]
- `syn-01154` "phepeee 400 milligram" → LowConfidence; correct product not ranked; top: SYN-006584 Pheepe 40 mg Tablet [0.72]
- `syn-01288` "ficte forty" → LowConfidence; correct at rank 3; top: SYN-004302 Faicata 200 mg/5 ml Suspension [0.64]
- `syn-01455` "do nira fifty" → LowConfidence; correct at rank 2; top: SYN-005597 Nile 50 mg Tablet [0.70]
- `syn-01490` "zifoux 500" → Ambiguous; correct at rank 2; top: SYN-010048 Jofoex 500 mg Tablet [0.87]
- `syn-01515` "shaiiflak 50" → Ambiguous; correct at rank 2; top: SYN-010098 Ciflac 50 mcg Tablet [0.92]
- `syn-01594` "plebira 50" → NeedsConfirmation; FALSE MATCH; top: SYN-007820 Flevira 50 mg Tablet [0.90]
- `syn-01665` "braod 20" → LowConfidence; correct at rank 2; top: SYN-007336 Varaid 20 mg/ml Injection [0.82]
- `syn-01987` "socelen 10" → LowConfidence; correct at rank 3; top: SYN-001522 Shisloon 10 mg Tablet [0.79]
- `syn-02008` "tomar nam ki" → Ambiguous; wrong products offered as a choice; top: SYN-015099 Namiil 0.5 mg Tablet [0.79]
- `syn-02025` "put it in my account" → Ambiguous; wrong products offered as a choice; top: SYN-015226 Putira 1% Cream [0.77]
- `syn-02029` "Phaibrona" → Ambiguous; wrong products offered as a choice; top: SYN-016604 Phaibraon 500 mg Tablet [0.86]
- `syn-02077` "Babraol" → Ambiguous; wrong products offered as a choice; top: SYN-013884 Bobrool 25 mg Tablet [0.86]
- `syn-02087` "Preespa" → Ambiguous; wrong products offered as a choice; top: SYN-007998 Prespi 40 mg Tablet [0.93]
- `syn-02134` "did the delivery come" → Ambiguous; wrong products offered as a choice; top: SYN-010058 Didaga 50 mg Capsule [0.77]
- `syn-02159` "write it on the slip" → Ambiguous; wrong products offered as a choice; top: SYN-005610 Slipir 25 mg Tablet [0.78]
- `syn-02173` "Flukoac 250" → NeedsConfirmation; FALSE MATCH; top: SYN-016794 Flokouc 250 mg Tablet [0.87]
- `syn-02177` "ma ke phone dao" → Ambiguous; wrong products offered as a choice; top: SYN-004214 Makex 150 mg Capsule [0.85]
- `syn-02180` "Plasluin 500" → NeedsConfirmation; FALSE MATCH; top: SYN-007690 Pluslin 500 mg Tablet [0.88]
- `syn-02189` "Pouspeeen" → Ambiguous; wrong products offered as a choice; top: SYN-005384 Fouspeen 500 mg Tablet [0.90]
- `syn-02215` "Dibrem 20" → NeedsConfirmation; FALSE MATCH; top: SYN-004995 Daibreum 20 mg Tablet [0.86]
- `syn-02232` "Shoflel" → NeedsConfirmation; FALSE MATCH; top: SYN-019613 Sifliil 600 mg + 400 IU Tablet [0.85]
- `syn-02293` "Trotrais 250" → NeedsConfirmation; FALSE MATCH; top: SYN-000756 Tritras 250 mg Capsule [0.88]
- `syn-02309` "how far is the hospital" → Ambiguous; wrong products offered as a choice; top: SYN-000253 Faror 100 mg + 200 mg + 200 mcg Tablet [0.78]
- `syn-02361` "Zestuon" → Ambiguous; wrong products offered as a choice; top: SYN-001856 Jeesteen 40 mg Tablet [0.86]
- `syn-02439` "Feeflaex" → NeedsConfirmation; FALSE MATCH; top: SYN-009339 Feepluex 100 mg + 200 mg + 200 mcg Tablet [0.86]
- `syn-02545` "Vaclid" → Ambiguous; wrong products offered as a choice; top: SYN-018390 Veclaid 5 mg Tablet [0.86]
