### Baseline (before search changes), synthetic-20k TEST

- Dataset: `synthetic-20k-test.cases.jsonl` (sha256 `f51299e722c329d3…`), 2600 cases: 2000 positive, 600 negative
- Catalog: Local product index (synthetic-catalog-20k.json); 20000 products (sha256 `539870d42230de34…`); index build 926 ms
- Run: 2026-10-09 07:53 UTC on Ubuntu 24.04.5 LTS; X64; 4 logical CPUs; .NET 10.0.12
- Thresholds: min candidate 0.55, confident 0.90, ambiguity margin 0.06, min recognition confidence 0.45

| Metric | Value |
|---|---|
| Top-1 accuracy (positives) | 98.9 % (1977/2000) |
| Top-5 accuracy (positives) | 99.5 % (1989/2000) |
| False matches (all cases) | 1.5 % (38/2600) |
| — on positive cases | 0.3 % (5/2000) |
| — on negative cases | 5.5 % (33/600) |
| False matches with decision Confident | 0.2 % (5/2600) |
| Wrong product offered (false match or all-wrong Ambiguous), all cases | 6.8 % (178/2600) |
| — on negative cases | 28.7 % (172/600) |
| Correct top-1 but answered LowConfidence/NoMatch (positives) | 0.4 % (8/2000) |
| Negatives safely handled (NoMatch/LowConfidence) | 71.3 % (428/600) |
| Negatives answered NoMatch | 1.2 % (7/600) |
| Positives answered NoMatch (missed) | 0.1 % (1/2000) |
| Decision agreement (cases with expected decision) | 100.0 % (101/101) |
| Decisions | Confident 1197, NeedsConfirmation 411, Ambiguous 546, LowConfidence 438, NoMatch 8 |
| Matcher latency p50 / p95 / max | 2.16 / 6.64 / 59.70 ms |

| Category | Cases | Top-1 | Top-5 | False matches | Wrong offered | Negatives safe |
|---|---:|---:|---:|---:|---:|---:|
| asr-split | 191 | 97 % (186/191) | 99 % (189/191) | 1 | 1 | – |
| bangla-script | 303 | 99 % (300/303) | 99 % (301/303) | 0 | 0 | – |
| banglish-respelling | 243 | 98 % (238/243) | 99 % (240/243) | 2 | 2 | – |
| brand-form | 240 | 100 % (240/240) | 100 % (240/240) | 0 | 0 | – |
| brand-only | 183 | 100 % (183/183) | 100 % (183/183) | 0 | 0 | – |
| brand-strength | 507 | 100 % (507/507) | 100 % (507/507) | 0 | 0 | – |
| generic-strength | 112 | 100 % (112/112) | 100 % (112/112) | 0 | 0 | – |
| neg-form-mismatch | 145 | – | – | 8 | 14 | 90 % (131/145) |
| neg-not-medicine | 30 | – | – | 2 | 26 | 13 % (4/30) |
| neg-strength-mismatch | 236 | – | – | 5 | 15 | 94 % (221/236) |
| neg-unknown-brand | 189 | – | – | 18 | 117 | 38 % (72/189) |
| typo | 221 | 95 % (211/221) | 98 % (217/221) | 2 | 3 | – |

Failures (195; wrong or missing top-1, false matches, unexpected decisions) — first 60:

- `syn-00004` "phicliat 5" → Ambiguous; correct at rank 2; top: SYN-006531 Ficlit 5 mg Tablet [0.92]
- `syn-00027` "trimarel 100" → NoMatch; correct product not ranked; top: (none)
- `syn-00050` "ডীকি ১৮০" → LowConfidence; correct product not ranked; top: SYN-002166 Dico 100 mg + 200 mg + 200 mcg Tablet [0.74]
- `syn-00187` "clhoc five hundred mg" → NeedsConfirmation; FALSE MATCH; top: SYN-014614 Cloko 500 mg + 200 IU Tablet [0.89]
- `syn-00312` "nakhin 20" → LowConfidence; correct product not ranked; top: SYN-013333 Nacaon 5 mg Tablet [0.70]
- `syn-00335` "vubaita 100 mcg" → NeedsConfirmation; FALSE MATCH; top: SYN-007007 Bubaina 100 mcg/puff Inhaler [0.78]
- `syn-00700` "fagel 5" → NeedsConfirmation; FALSE MATCH; top: SYN-011886 Phagil 10 mg/5 ml Syrup [0.91]
- `syn-00721` "zazvora fifty" → Ambiguous; correct at rank 2; top: SYN-006220 Zozabora 50 mg Tablet [0.87]
- `syn-00731` "কীশীওন ১০০" → LowConfidence; correct product not ranked; top: SYN-004985 Kisoon 20 mg Tablet [0.77]
- `syn-00773` "boguhuor 0.9" → Ambiguous; correct at rank 2; top: SYN-012182 Bogahouor 0.9% Infusion [0.88]
- `syn-00798` "me hom 0.9" → LowConfidence; correct at rank 9; top: SYN-003687 Homal 1% Cream [0.73]
- `syn-00895` "ki soon twenty" → LowConfidence; correct at rank 4; top: SYN-009636 Shona 200 mg/5 ml Powder for Suspension [0.73]
- `syn-00931` "do pre eighty" → NeedsConfirmation; FALSE MATCH; top: SYN-010720 Prebeeix 80 mg Tablet [0.83]
- `syn-01049` "ki clouan five hundred mg" → Ambiguous; correct at rank 2; top: SYN-005895 Clouvaan 500 mg Tablet [0.81]
- `syn-01101` "torhox 20" → Ambiguous; wrong products offered as a choice; top: SYN-009968 Storeex 20 mg Capsule [0.77]
- `syn-01115` "bporo 10 milligram" → LowConfidence; correct product not ranked; top: SYN-012295 Vopora 75 mg/5 ml Syrup [0.62]
- `syn-01131` "ফাইশোএন সাড়ে সাতশো" → LowConfidence; correct at rank 2; top: SYN-011973 Phaishoen 500 mg Tablet [0.75]
- `syn-01154` "phepeee 400 milligram" → LowConfidence; correct product not ranked; top: SYN-006584 Pheepe 40 mg Tablet [0.72]
- `syn-01177` "kinur 100" → LowConfidence; correct product not ranked; top: SYN-018543 Clinur 200 mg/5 ml Powder for Suspension [0.67]
- `syn-01455` "do nira fifty" → Ambiguous; correct at rank 2; top: SYN-005597 Nile 50 mg Tablet [0.70]
- `syn-01515` "shaiiflak 50" → Ambiguous; correct at rank 2; top: SYN-010098 Ciflac 50 mcg Tablet [0.92]
- `syn-01594` "plebira 50" → NeedsConfirmation; FALSE MATCH; top: SYN-007820 Flevira 50 mg Tablet [0.90]
- `syn-01987` "socelen 10" → Ambiguous; correct at rank 2; top: SYN-012897 Chesleen 10 mg Tablet [0.76]
- `syn-02003` "wifi password ki" → Ambiguous; wrong products offered as a choice; top: SYN-001829 Shasholid 0.4 mg Modified Release Capsule [0.59]
- `syn-02004` "matha byatha korche" → Ambiguous; wrong products offered as a choice; top: SYN-014000 Matata 20 mg Tablet [0.67]
- `syn-02005` "Clateera 20" → Ambiguous; wrong products offered as a choice; top: SYN-008085 Clatroum 20 mg/ml Injection [0.65]
- `syn-02008` "tomar nam ki" → Ambiguous; wrong products offered as a choice; top: SYN-017480 Tomir 100 mg/5 ml Suspension [0.84]
- `syn-02011` "Prislis 20" → Ambiguous; wrong products offered as a choice; top: SYN-001786 Prikis 20 mg Tablet [0.70]
- `syn-02017` "network nai" → Ambiguous; wrong products offered as a choice; top: SYN-006841 Naideebai 20 mg/ml Injection [0.76]
- `syn-02018` "shondhay dekha hobe" → Ambiguous; wrong products offered as a choice; top: SYN-009636 Shona 200 mg/5 ml Powder for Suspension [0.62]
- `syn-02019` "Flaiplopraum syrup" → NeedsConfirmation; FALSE MATCH; top: SYN-007467 Flaiuplopraum 200 ml Suspension [0.93]
- `syn-02025` "put it in my account" → Ambiguous; wrong products offered as a choice; top: SYN-015226 Putira 1% Cream [0.77]
- `syn-02026` "rain is coming" → Ambiguous; wrong products offered as a choice; top: SYN-010766 Prainu 200 mg/5 ml Powder for Suspension [0.62]
- `syn-02028` "আজকে হাট বার" → Ambiguous; wrong products offered as a choice; top: SYN-015877 Habara 40 mg Injection [0.67]
- `syn-02029` "Phaibrona" → Ambiguous; wrong products offered as a choice; top: SYN-016604 Phaibraon 500 mg Tablet [0.86]
- `syn-02039` "jhor hoyeche kal theke" → Ambiguous; wrong products offered as a choice; top: SYN-005928 Kaleol 20 mg Tablet [0.75]
- `syn-02043` "Kejijed" → Ambiguous; wrong products offered as a choice; top: SYN-000451 Kechoid 400 mg Tablet [0.58]
- `syn-02044` "Glajouex syrup" → Ambiguous; wrong products offered as a choice; top: SYN-018727 Glijaiex 200 mg/5 ml Suspension [0.82]
- `syn-02046` "Brebrujain 250" → NeedsConfirmation; FALSE MATCH; top: SYN-010054 Brerujaid 250 mg Tablet [0.78]
- `syn-02054` "Traijita" → Ambiguous; wrong products offered as a choice; top: SYN-019198 Trageeta 5 mg/5 ml Syrup [0.78]
- `syn-02055` "lunch korechen" → Ambiguous; wrong products offered as a choice; top: SYN-009211 Cocheene 10 mg Tablet [0.60]
- `syn-02060` "Fatrophouil" → Ambiguous; wrong products offered as a choice; top: SYN-019276 Fatofouor 100 mg Tablet [0.74]
- `syn-02064` "Zeegliglana Extra 250" → Confident; FALSE MATCH; top: SYN-007186 Zeegliglana 250 mg Tablet [0.93]
- `syn-02068` "মেয়েটা কলেজে পড়ে" → Ambiguous; wrong products offered as a choice; top: SYN-014303 Clezara 8 mg Tablet [0.69]
- `syn-02071` "ফ্রিজটা নষ্ট হয়ে গেছে" → Ambiguous; wrong products offered as a choice; top: SYN-012185 Cechee 200 mg/5 ml Suspension [0.57]
- `syn-02072` "Volouix 10 mg" → Ambiguous; wrong products offered as a choice; top: SYN-017170 Botraix 10 mg/5 ml Syrup [0.64]
- `syn-02074` "রিকশা ভাড়া কত" → Ambiguous; wrong products offered as a choice; top: SYN-001213 Rashavar 5 mg Tablet [0.70]
- `syn-02076` "Jikina Plus tablet" → Confident; FALSE MATCH; top: SYN-001078 Jikina 10 mg Tablet [0.93]
- `syn-02077` "Babraol" → Ambiguous; wrong products offered as a choice; top: SYN-006241 Prabraol 10 mg Tablet [0.76]
- `syn-02079` "the bus was late again" → NeedsConfirmation; FALSE MATCH; top: SYN-002962 Vushas 5 mg Tablet [0.84]
- `syn-02080` "Fofliplouac 20" → Ambiguous; wrong products offered as a choice; top: SYN-011762 Feefliac 20 mg Tablet [0.61]
- `syn-02087` "Preespa" → Ambiguous; wrong products offered as a choice; top: SYN-007998 Prespi 40 mg Tablet [0.93]
- `syn-02103` "Niplilara" → Ambiguous; wrong products offered as a choice; top: SYN-016767 Pliler 30 mg Tablet [0.67]
- `syn-02104` "পাশের দোকানে জিজ্ঞেস করেন" → Ambiguous; wrong products offered as a choice; top: SYN-007580 Hasheer 500 mg Capsule [0.65]
- `syn-02105` "Tanophion" → Ambiguous; wrong products offered as a choice; top: SYN-013842 Trouphion 2 mg/5 ml Syrup [0.68]
- `syn-02106` "বিদ্যুৎ বিল দিতে হবে" → NeedsConfirmation; FALSE MATCH; top: SYN-010297 Fidut 100 mg Tablet [0.78]
- `syn-02107` "Gaivihoan" → Ambiguous; wrong products offered as a choice; top: SYN-017944 Gainian 100 mg Tablet [0.65]
- `syn-02110` "Doucac SR cream" → NeedsConfirmation; FALSE MATCH; top: SYN-015158 Doucac 0.5% + 500 IU Ointment [0.90]
- `syn-02111` "kono shomossha nai" → Ambiguous; wrong products offered as a choice; top: SYN-003398 Naijas 500 mg Tablet [0.77]
- `syn-02113` "Spustochaiac" → Ambiguous; wrong products offered as a choice; top: SYN-011944 Spastoucok 5% Infusion [0.70]
