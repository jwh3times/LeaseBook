# MICR E-13B evidence for blank-stock refund checks

- **Audience:** Maintainers
- **Status:** Research for issue #474; evidence for the spike, not an implementation decision
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-01

## Scope and conclusion

This note gathers evidence for printing a complete refund check, MICR line included, on blank stock.
All linked sources were checked on 2026-10-01 and 2026-10-02. Nothing was printed, no MICR tester
was used, and no bank evaluated a sample. The three governing US standards are paid ANSI documents
that were not read: X9.100-20 (E-13B character, print and test specification), X9.100-160-1 (MICR
placement and location) and X9.100-187 (image exchange). ISO 1004-1 was not read either. Every US
figure below is labelled with its actual source, and the gaps are listed at the end.

**Recommendations:**

1. **Glyph source: draw the 14 E-13B glyphs as vector paths and render them through QuestPDF's
   `Svg()`.** The public Payments Canada Standard 006 reproduces dimensioned drawings of all 14
   characters, taken from ISO 1004-1995. QuestPDF 2026.9.1 draws SVG as vector paths with exact
   geometry, which this spike confirmed by rendering. The approach needs no font license, needs no
   system font, and produces geometry a test can measure. Reject GnuMICR: it is GPL v2-or-later with
   no font exception, and its author asks that it not be built into proprietary applications. Keep a
   commercial developer-licensed font as the fallback if bank testing rejects the drawn glyphs.
2. **Placement: the Canadian standard is public, and the US standard is not.** Standard 006 gives a
   complete, sourced layout. X9's own published glossary confirms the US clear band, print band,
   character pitch and field order, and those values agree with Standard 006. It does not confirm
   the absolute US offsets (where position 1 starts) or most tolerances. Treat the bank's MICR
   specification sheet as the authority for each account, and do not hard-code the Canadian offsets
   as a US guarantee.
3. **Bank acceptance is an operator gate.** The bank must test and approve samples printed on the
   operator's own printer, toner and stock before any live check is issued. This repeats whenever a
   component changes.
4. **Contract corrections for the issue.** Nobody publishes the arrangement of the On-Us field for
   all banks: each bank specifies it. A routing number plus an account number is therefore not
   enough. The layout must follow the bank's specification sheet. A business-size check carries the
   check number in the Auxiliary On-Us field. The existing voucher layout prints the memo inside
   the clear band. It must move before blank stock ships.

## 1. Glyph source and license

### (a) GnuMICR

The original site (`sandeen.net/GnuMICR`) did not answer on 2026-10-02. The evidence below comes from
the Internet Archive's copies of the
[project page](https://web.archive.org/web/20030411213628/http://sandeen.net:80/GnuMICR/) and the
[0.30 release tarball](https://web.archive.org/web/20070106015053/http://sandeen.net:80/GnuMICR/download/GnuMICR-0.30.tar.gz),
which was downloaded and unpacked for this note.

- **License:** `COPYING` is the GNU GPL version 2. The header of the `.raw`/`.pfa` source reads
  "either version 2 of the License, or (at your option) any later version". The TTF/OTF copyright
  name record reads "Released under the terms of the Gnu Public License". No file contains the FSF
  font exception (verified by reading the files).
- **Author's stated intent (README, 0.30):** "it is my wish that this font not be distributed in such
  a way that it is built into a proprietary piece of software ... you should not hard-code or embed
  this font into your application." The 0.30 changelog says the author "removed blather about
  distribution w/ a commercial application", but the README in the same tarball still contains the
  passage.
- **Embedding flags:** the reports of an embedding restriction do not hold for 0.30. Both
  `GnuMICR.ttf` and `GnuMICR.otf` carry OS/2 `fsType = 8` (verified by parsing the files). Under
  the [OpenType OS/2 specification](https://learn.microsoft.com/en-us/typography/opentype/spec/os2#fstype),
  that value is "Editable embedding: the font may be embedded". The Type 1 files have no `fsType`.
  The obstacle is the license, not the embedding bit.
- **What GPL without the exception means here:** the
  [FSF's font-exception FAQ entry](https://www.gnu.org/licenses/gpl-faq.html#FontException)
  supplies exception text that says embedding a font "does not by itself cause the resulting
  document to be covered by the GNU General Public License". GnuMICR does not carry that exception.
  Whether embedding a GPL font subset in a served PDF puts any obligation on the document or on the
  application is unresolved by any source read here. This note is not a legal opinion. Because the
  author's stated wish is explicit, GnuMICR is not a defensible choice for a proprietary SaaS.
- **Fitness:** the author states that the font was hand-built from the Canadian Payments Association
  site, that "I have not had this font tested by any bank", that the TTF was "converted by a third
  party" and is of low confidence, and "Use this font at your own risk!".

### (b) Permissively licensed E-13B outlines

[`zaxbux/MICR_E13-B_Font`](https://github.com/zaxbux/MICR_E13-B_Font) (single commit, 2021-08-17)
contains 14 SVG glyph outlines (`u0030`–`u0039`, `u2446`–`u2449`) and a `LICENCE.md` that is the SIL
OFL 1.1 with no Reserved Font Name. The OFL text states that the license "does not apply to any
document created using the fonts". The README says the outlines were drawn from Payments Canada
Standard 006 and warns: "This font has not been professionally tested, so I don't recommend using it
for real cheques."

A spot check found a possible discrepancy. The digit-zero outline has a `viewBox` of `6.37 × 8.42`.
Read as points, that is 2.247 mm × 2.970 mm. The height matches Standard 006's 2 × 1.486 mm. The width
matches neither reading of the standard's Fig. 1.8.1 that this note could make (2.146 mm, or
2.311 mm if the right-stroke dimensions are read as straddling the centre line). The character
tolerance is ±0.038 mm. Use these outlines as a cross-check, not as the source of truth.

### (c) Commercial fonts

IDAutomation is one example vendor; its terms were read directly from the vendor's own pages. Its
[software license agreement](https://www.idautomation.com/licensing/software-license/) §7.3 says the
licensee "may only embed ... fonts in files, documents, or reports (for example, in a PDF file) that
are distributed outside Licensee's Organization if Licensee has purchased a Developer License ...
and Licensee does not encourage users to extract the embedded ... fonts". The agreement defines
"Server" to include "cloud-based environments such as Google Cloud, Azure, and AWS". The vendor's
[product page](https://www.idautomation.com/micr-fonts/e13b) claims its fonts follow ISO 1004:1995,
meet ANSI X9.27-1995, and have been "tested on several MICR readers and sorters". These are vendor
claims and were not verified. Their developer-licensing terms would need a legal read for a
multi-organization SaaS before purchase. Other vendors were not surveyed.

### (d) Vector paths drawn from the public specification

- **Authoritative public geometry:** the
  [Payments Canada Standard 006](https://payments.ca/sites/default/files/standard006eng.pdf) (2024
  edition), Appendix I §1.8, reproduces Figures 1.8.1–1.8.14 of all 14 characters ("Diagrams taken
  from ISO 1004-1995"). The figures are dimensioned in millimetres. The notes give these values: all
  radii 0.165 mm except the zero, a ±0.038 mm tolerance, and a minimum horizontal bar width of
  0.279 mm. The figures are low-resolution scans, and some dimension leaders are ambiguous (the
  zero's right stroke, for example). Each glyph has to be interpreted carefully and cross-checked.
  The standard is copyrighted by the CPA. The plan is to author our own paths from its dimensions,
  not to copy the figures. Whether the dimensions are free to use is a legal question that this
  note does not settle.
- **Character height:** Fig. 1.8.1 dimensions the zero as 1.486 mm above and below the horizontal
  centre line. The height is therefore 2.972 mm, about 0.117 in. This was computed from the figure;
  the standard does not state it in prose.
- **QuestPDF support (repo pins 2026.9.1 in `Directory.Packages.props`):** the public API is
  `IContainer.Svg(string)`, `Svg(SvgImage)` and a dynamic `Svg(size => ...)`
  ([QuestPDF SVG docs](https://www.questpdf.com/api-reference/image/svg.html)). In the 2026.9.1
  source, the `SvgImage` element calls `Canvas.DrawSvg` with no rasterization step
  ([`Elements/SvgImage.cs`](https://github.com/QuestPDF/QuestPDF/blob/2026.9.1/src/dotnet/library/QuestPDF/Elements/SvgImage.cs)).
  A lower-level `SvgPath` element exists, but its fluent method is `internal`. A throwaway probe on
  2026-10-02 rendered a 2 × 3 mm SVG rectangle at a known offset. The PDF had no images and one
  filled vector path at exactly the expected coordinates (74.835 pt to 80.504 pt). `SkiaSharp` is not
  needed: the [`SkiaSharp` integration](https://www.questpdf.com/api-reference/skiasharp-integration.html)
  is a separate opt-in package.
- **Testability:** `PdfPig` 0.1.16 is already a test dependency and exposes `Page.Paths` and
  `PdfPath.GetBoundingRectangle()` (confirmed from its XML docs). The trade-off: a path-drawn MICR
  line is not extractable as text, so tests must assert geometry, not strings.

**Recommendation: (d).** It has no license dependency, it is consistent with the bundled-fonts-only
runtime, and its geometry is deterministic and measurable. The risk is that geometry correct to the
specification can still fail on a laser printer. Standard 006 Appendix I §1.2 warns of "E13-B
character distortion" with laser printing, and §1.3 notes that letterpress fonts are cut 0.001 in
smaller to allow for squeeze. Bank testing (section 3) is the only check that closes this risk. If
the bank rejects the drawn glyphs and the cause is not fixable, fall back to (c).

## 2. Dimensions and placement

### Verified from public primary sources

| Fact                         | Value                                                                                                                                                         | Source                                                  |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------- |
| Character set                | 10 digits plus transit, amount, on-us, dash                                                                                                                   | Std 006 §4.1; X9 TR 100 def. 56                         |
| Character space (pitch)      | 0.125 in                                                                                                                                                      | X9 TR 100 def. 57 (citing X9.100-160-1); Std 006 §4.5.3 |
| Character height             | 2.972 mm (≈ 0.117 in), from figure                                                                                                                            | Std 006 Fig. 1.8.1                                      |
| Glyph tolerance              | ±0.038 mm (±0.0015 in); horizontal bars ≥ 0.279 mm                                                                                                            | Std 006 App. I §1.8 notes                               |
| MICR clear band              | 0.625 in high, front and back, from the aligning (bottom) edge; no magnetic ink other than E-13B                                                              | X9 TR 100 def. 185 (citing X9.100-20); Std 006 §4.2     |
| MICR print band              | 0.250 in high, inside the clear band                                                                                                                          | X9 TR 100 def. 187 (citing X9.100-20)                   |
| Optical clear band           | 0.300 in high, starting 0.150 in above the aligning edge                                                                                                      | X9 TR 100 def. 202 (citing X9.100-20)                   |
| Reference edges              | Horizontal measurements from the right (leading) edge, vertical from the bottom (aligning) edge                                                               | Std 006 §4.3; X9 TR 100 defs. 9, 171                    |
| US field order (from right)  | Amount positions 1–12; On-Us between the closing amount symbol and the opening transit symbol; routing positions 33–43; EPC at 44 or 45; Auxiliary On-Us left | X9 TR 100 defs. 11, 120, 197, 249; FRB X9.37 guide      |
| Spacing (Canada)             | 0.125 in ±0.010 in in the transit and amount fields; never under 0.115 in elsewhere                                                                           | Std 006 §4.5.3                                          |
| Vertical (Canada)            | Bottom of the encoding line ≥ 3/16 in above the bottom edge; a 1/4 in encoding area; 3/16 in clear above it                                                   | Std 006 §4.4                                            |
| Amount field (Canada)        | Rightmost symbol's right edge 5/16 in ±1/16 in from the right edge; not printed by the drawer                                                                 | Std 006 §4.4.1                                          |
| Field boundaries (Canada)    | On-Us 1 7/8–4 1/4 in; Transit 4 1/4–5 3/4 in (11 spaces); Serial 5 3/4 in to 1/8 in from the left edge                                                        | Std 006 §4.4.2–4.4.4                                    |
| Horizontal tolerance, skew   | ±1/16 in on field boundaries; skew ≤ 1.5°; adjacent bottom-edge alignment within 0.007 in                                                                     | Std 006 §4.5, §4.5.1–4.5.2                              |
| Signal level (Canada)        | 80%–200% of nominal per character                                                                                                                             | Std 006 App. I §1.10                                    |
| Magnetic ink (Canada, press) | 50%–60% iron oxide                                                                                                                                            | Std 006 App. I §1.7.8                                   |

X9 TR 100 is [ASC X9 TR 100-2013, _Organization of Check-related Payments Standards_](https://x9.org/wp-content/uploads/2016/11/X9-TR-100-2013.pdf),
which X9 publishes free of charge. Its Part 2 glossary quotes definitions from the paid standards and
names the standard behind each one, which is why it can be cited for US facts the paid standards own.
It also records the old numbering: X9.100-20 was X9.27, and X9.100-160-1 was X9.13. The FRB X9.37 guide is the
[Federal Reserve adoption of draft standard X9.37-2003](https://www.frbservices.org/binaries/content/assets/crsocms/financial-services/check/setup/frb-x937-standards-reference.pdf)
(v1.9, 2019-04-26). It locates the On-Us field "between positions 14 and 31" and the 15-digit
Auxiliary On-Us at "positions 48 - 62". It calls the Auxiliary On-Us "field 7 or the serial number"
and puts the EPC at "position 44 or 45".

**Does US placement match Canada?** The US values that could be verified agree with Standard 006.
The clear band is 5/8 in in both. The 1/4 in print band at 3/16–7/16 in falls inside the X9 optical
clear band of 0.150–0.450 in. The pitch is 0.125 in in both. The transit field is 11 spaces in both,
matching US positions 33–43. The absolute US horizontal origin of position 1, the vertical offset of
the print band, and the US tolerances are owned by X9.100-160-1 and could not be confirmed. A vendor
font manual ([placement chapter](https://www.morovia.com/manuals/micr4/ch03.php), citing ANSI X9.13) reports that
the line begins 5/16 in from the right edge, that the print band runs 3/16–7/16 in from the bottom,
and a 1/16 in cutting tolerance. That source is secondary and these values are unverified. Standard
006 is also inconsistent with itself: §4.4.1 says 5/16 in ±1/16 in, but §4.5 says "1/4 in ±1/16 in
from the right edge".

**Layout consequence for the existing voucher.** `RefundCheckPdf` treats the check as the top 3.5 in
(252 pt) of the Letter page. On blank check-on-top stock, the check's aligning edge is that
perforation, so the clear band runs from 207 pt to 252 pt. The current memo field (y 212–226 pt)
lies inside the band, and the address block (150–208 pt) touches it. Std 006 §4.2 and the bank
sheet cited below forbid any non-E-13B printing in the band. Signature lines are included. With
MICR toner, everything printed on the sheet is magnetic. The calibration offsets (up to 1 in) can
also push fields into the band. Std 006 §3.6.1 also recommends that a detachable voucher sit at the
top or left of the item, and a check-on-top layout leaves the stubs below the check. The text does
not resolve whether that recommendation also applies to US banks, so ask the bank.

**How placement is verified in practice:** Std 006 §1.3–1.4 describes printing and layout gauges,
grid comparators (App. II §1.3.2: 0.010 in squares with 0.003 in channels), signal-level testers, and
samples submitted to the bank's quality assurance department ("highly recommended"). X9 TR 100
def. 188 defines a MICR tester as a device that measures signal strength and compares waveforms.
None of this can run in CI.

**What a deterministic layout test can assert** (using PdfPig paths on the rendered PDF):

- The fields appear in the right order. The routing field is a transit symbol, nine digits whose
  checksum passes, and a closing transit symbol. Then come the On-Us content in the bank's specified
  arrangement and the Auxiliary On-Us check number between On-Us symbols. The amount field is empty.
- Every glyph's bounding box sits at the nominal right-edge offset for its position, with a
  0.125 in pitch, inside the 1/4 in print band, and matches its reference outline to well under the
  ±0.038 mm glyph tolerance.
- No other path or text intersects the 5/8 in clear band on the front of the check, at any allowed
  calibration offset.
- The MICR line is identical across calibration offsets, while the other fields move with the
  offsets.
- Pre-printed output is byte-identical to the output before the change.

A test cannot assert magnetic signal, toner fusion, printer registration, paper or cutting tolerance.

## 3. Bank acceptance and operator steps (not engineering)

- **Magnetic toner:** "All E-13B characters must be printed in magnetic ink. Laser-printer toner
  cartridges must be clearly labeled 'MICR' toner ... Magnetic ink is not available for inkjet
  printers." This comes from a
  [2008 Bank of America specification-sheet form](http://www.internationalpcg.com/documents/MICRSpecs.pdf)
  hosted by a third-party printer. It is illustrative only and was not confirmed as current.
  Std 006 App. I §1.2 recommends a printer dedicated to MICR, manufacturer-matched supplies, and
  professional calibration. It warns that poor toner fusion lets characters "flake off" in
  reader-sorters.
- **Specification sheet first:** the On-Us field arrangement "is variable specified by the financial
  institution on which the check is written" (X9 TR 100 def. 197). Std 006 §4.4.2.2 requires customers
  to obtain the account-number layout "from their financial institution in the form of a
  specification sheet". The example bank sheet carries per-position layouts for checks and for
  deposit tickets.
- **Test-check submission and approval:** Std 006 App. I §1.2 recommends sending samples "in their
  final format (i.e., not just the MICR-line)" for testing "on a regular basis, or every time a
  component in their end-to-end solution changes". The Supplement requires sample approval before
  an order is accepted. The example US sheet asks for "a minimum of 25 voided checks" per depositing
  location. The quantity varies by bank.
- **Check stock:** the example sheet lists 24–44 lb paper, grain direction, and security features
  (artificial watermark, laid lines, void pantograph, chemical void, numbered stock). ASC X9 TR 8
  (_Check Security_) and X9.100-170 (_Check Fraud Deterrent Icon_) cover these and were not read.
  Std 006 §5.2 adds void pantographs.
- **Image quality (Check 21):** the Federal Reserve's image cash letter guide (§4.3) requires images
  of at least 200 dpi, black and white, in TIFF 6.0 with Group 4 compression. Its image-quality engine flags
  missing corners, document dimensions, skew, brightness and noise. Rejected items "will be adjusted
  back to the depositor". The guide's linked IQA settings PDF returned an HTML page on 2026-10-02.
  The exchange format X9.100-187 was not read.

## 4. Implementation-contract notes

- **Routing checksum:** the [ABA Routing Number Policy](https://www.aba.com/-/media/documents/reference-and-guides/routingnumberpolicy.pdf)
  (revised 9/20) defines the structure `XXXX YYYY C`: Federal Reserve routing symbol, ABA institution
  identifier, check digit. It assigns meaning to the leading two-digit series. For the calculation
  it refers readers to the "User Instructions of the ABA Key to Routing Numbers", a paid publication
  that was not read. The Federal Reserve guide confirms that a "Mod Check will be done" and that a
  failing item "will reject". The weights 3-7-1 repeated, with a weighted sum that must be ≡ 0
  (mod 10), are reported by secondary sources such as
  [Apache Commons Validator `ABANumberCheckDigit`](https://commons.apache.org/validator/apidocs/org/apache/commons/validator/routines/checkdigit/ABANumberCheckDigit.html),
  which cites Wikipedia. As a consistency check, both routing numbers printed on the example bank
  sheet (`111000012`, `540900071`) satisfy the rule. Consider validating the leading series against
  the ABA policy table as well.
- **Where the check number goes:** "On checks of sufficient length it generally appears in the
  auxiliary On-Us field. On shorter personal-sized checks it generally appears to the left or to the
  right of the account number in the On-Us field" (X9 TR 100 def. 70). The example bank sheet
  requires the serial number in the Auxiliary On-Us field for business accounts, with On-Us symbols
  before and after it and no blank spaces. It also requires the face check number to match the MICR
  serial. Canada (Std 006 §4.4.4) uses a serial field of up to 12 digits between On-Us symbols. An
  8.5 in voucher check is business-sized, so the check number belongs in Auxiliary On-Us.
- **Stored data must cover the bank's On-Us layout**, not just an account number: transaction or
  process codes, dashes and positions come from the specification sheet. The EPC (position 44) is
  "used for special purposes as authorized by ASC X9" (def. 120) and should normally be left blank.
- **The amount field stays blank.** The bank of first deposit encodes it (Std 006 §4.4.1; example
  bank sheet item 8).

## Gaps and unverifiable claims

- **Not read (paid):** ANSI X9.100-20, X9.100-160-1, X9.100-160-2, X9.100-187, ISO 1004-1, ASC X9 TR 2
  (_Understanding, Designing and Producing Checks_), TR 6 (_Guide to Quality MICR Printing and
  Evaluation_), TR 8, and the ABA Key to Routing Numbers user instructions. The ANSI store
  abstract pages refused automated access (HTTP 403 or a challenge page).
- **US absolute offsets and tolerances:** where position 1 sits, the print band's offset above the
  bottom edge, horizontal and vertical tolerances, skew, and the US signal-level range. Values for
  these appear only in Standard 006 (Canada) or in the secondary vendor manual. They are unverified
  for US checks.
- **E-13B identity across standards:** that ISO 1004 glyphs equal X9.100-20 glyphs is reported by the
  GnuMICR author and implied by a vendor claim. It is not verified.
- **Glyph-figure interpretation:** the Standard 006 figures are scanned, and some dimension leaders are
  ambiguous. Any drawn glyph set needs an independent cross-check before a bank test.
- **GPL font embedding:** whether embedding a GPL font without the exception creates obligations for
  served PDFs is unresolved; no legal opinion was sought.
- **Routing checksum algorithm:** verified only from secondary sources plus two real routing numbers.
- **Bank sheet currency:** the example US specification sheet is a 2008 form hosted by a third party.
  Each operator must obtain their own bank's current sheet.
- **Not done:** no physical print, MICR tester run, toner evaluation or bank submission.
