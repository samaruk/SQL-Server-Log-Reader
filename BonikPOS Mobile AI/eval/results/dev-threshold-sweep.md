# DEV-split sweep of MinFuzzyConfirmNameScore

Dataset: synthetic-20k DEV (seed 7, phrase set A), final matcher code. All rows from one run of the same build.

Selection rule (stated after the first sweep on an earlier generator version and applied unchanged afterwards): the smallest value with at most 3% wrong products offered on DEV negatives. Selected: **0.84**. Ranking (top-1/top-5) does not depend on this threshold; it only decides which correct answers are shown as weak suggestions.

| min fuzzy confirm | Top-1 | Top-5 | False matches (all) | False matches (negatives) | Confident false matches | Wrong offered (negatives) | Correct but low confidence |
|---|---:|---:|---:|---:|---:|---:|---:|
| 0 | 1989/2000 | 1994/2000 | 16/2600 | 14/600 | 0/2600 | 60/600 | 19/2000 |
| 0.80 | 1989/2000 | 1994/2000 | 11/2600 | 9/600 | 0/2600 | 25/600 | 26/2000 |
| 0.82 | 1989/2000 | 1994/2000 | 9/2600 | 7/600 | 0/2600 | 20/600 | 33/2000 |
| 0.84 | 1989/2000 | 1994/2000 | 8/2600 | 6/600 | 0/2600 | 14/600 | 46/2000 |
| 0.86 | 1989/2000 | 1994/2000 | 8/2600 | 6/600 | 0/2600 | 11/600 | 109/2000 |
| 0.88 | 1989/2000 | 1994/2000 | 5/2600 | 3/600 | 0/2600 | 7/600 | 154/2000 |
| 0.90 | 1989/2000 | 1994/2000 | 3/2600 | 1/600 | 0/2600 | 2/600 | 228/2000 |
| 0.92 | 1989/2000 | 1994/2000 | 1/2600 | 0/600 | 0/2600 | 1/600 | 309/2000 |
