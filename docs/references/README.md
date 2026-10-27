# Research references

Summaries of research papers that shape Mod2Xgm's design, written for agents and contributors. **Read the
relevant summaries before working on FM fitting, scoring or the optimizer (plan M6–M9).**

| File | Topic |
|---|---|
| [fm-matching.md](fm-matching.md) | **Start here.** Combined view: where the papers agree, and the FM-fitting design (T8.2) |
| [horner-1993-ga-fm-matching.md](horner-1993-ga-fm-matching.md) | Horner, Beauchamp & Haken 1993: genetic algorithms for FM matching synthesis |
| [sound2synth-2022.md](sound2synth-2022.md) | Chen et al. 2022 (IJCAI): Sound2Synth, learned DX7 parameter estimation |
| [ddx7-2022.md](ddx7-2022.md) | Caspe, McPherson & Sandler 2022: DDX7, differentiable FM synthesis |

## Rules

- **No copies of copyrighted papers in this repository** (it's public). Summaries are written in our own words,
  with citations and links. Keep local copies in `docs/references/papers/`, which is git-ignored.
- Every summary states its **access status**: whether the full text was read, and which claims are unverified.
  Mark unverified details *(unverified)*, and remove the mark only after checking the source.
- When a paper changes a design decision, update the design doc it affects and log it in
  [../decisions.md](../decisions.md).

## Adding a summary

Name the file `<first-author>-<year>-<topic>.md` (or `<system>-<year>.md` for a named system) and use these
sections:

1. **Header table:** citation, DOI, links, copyright, access status, which plan tasks use it
2. **TL;DR for agents:** 3–5 sentences: the idea and what we do with it
3. **Problem**
4. **Method:** enough detail to re-implement the parts we use
5. **Results:** the key numbers, with the metric named
6. **Limitations:** stated or implied
7. **What applies to Mod2Xgm:** a table mapping each paper idea to a concrete decision, YM2612 parameter or plan task

Then add a row to the table above and to [fm-matching.md](fm-matching.md) or a new topic page.
