# ADR-051: Blank-stock checks draw their own E-13B glyphs, and bank numbers are write-only

- **Status:** Accepted
- **Date:** 2026-10-02
- **Deciders:** Maintainers

## Context

Refund checks print on pre-printed stock ([ADR-050](ADR-050-refund-checks.md)), which already carries
the bank's MICR line. Printing on blank stock means LeaseBook prints that line itself: the routing
number, the bank's On-Us field and the check number in E-13B characters, in magnetic toner, inside a
5/8 in clear band at the bottom of the check (#474). That raises three questions the
[research note](../research/micr-e13b-refund-checks.md) settles as far as public sources allow.

- **Glyphs.** The runtime image has no system fonts and QuestPDF renders only the font the image bundles. The common
  free E-13B font, GnuMICR, is GPL v2-or-later without the font exception, and its author asks that it
  not be built into proprietary software. Commercial fonts allow embedding in PDFs sent outside the
  licensee's organization only under a developer license. Payments Canada Standard 006 publishes
  dimensioned drawings of all 14 E-13B characters, taken from ISO 1004.
- **Placement.** The clear band, print band, character pitch and field order are confirmed by X9's
  public glossary and match Standard 006. The absolute US offsets and tolerances are in the paid
  X9.100-160-1 and could not be verified. Each bank's MICR specification sheet governs its accounts.
- **The numbers.** A routing number and an account number together are enough to draw on the trust
  account. The On-Us field's arrangement varies by bank and carries the account number.

## Decision

**Glyphs are vector paths LeaseBook authors.** The 14 E-13B characters are drawn as paths from the
Standard 006 dimensions and rendered through QuestPDF's `Svg()`. No font file is shipped, nothing
depends on a font license, and a test can measure the geometry with PdfPig. A commercial
developer-licensed font is the fallback only if bank testing rejects the drawn glyphs.

**Placement is Standard 006 by default and adjustable per bank account.** The MICR line's nominal
position comes from Standard 006. A per-account offset of at most a quarter inch either way moves the
whole line, independently of the print offsets that move the other fields, so an operator can match
the bank's specification sheet. Bank test-check approval is the operator's gate before any live check.

**The line is laid out by position, from the right edge.** Character n's right edge sits
5/16 in + (n − 1) × 1/8 in from the check's right edge (Standard 006 §4.4.1 and §4.5.3, which measure
spacing between right edges; the 1/8 in pitch matches X9's glossary), and a full character is centred on
5/16 in above the bottom edge, inside the 3/16–7/16 in print band (§4.4). From the right: positions 1–13
stay blank for the bank of first deposit; the On-Us field ends at position 14; the transit field
⑆ + routing number + ⑆ fills 33–43; the External Processing Code (44/45) stays blank; and the check number
is the Auxiliary On-Us field between On-Us symbols, the closing symbol at position 46, so the two EPC
positions are the only blanks between it and the transit field. Standard 006 §4.5 allows at most two
there. The Federal Reserve's X9.37 guide places the field's digits at "positions 48 - 62", which would
leave three; the line follows the stricter rule, and the bank test-check is where a bank that wants the
guide's placement says so. Nearly every Standard 006 glyph dimension is a multiple of 0.165 mm, so
the glyphs are outlines on a 0.0065 in grid; the exceptions are the seven's slanted joint, placed by its
0.271 mm and 0.216 mm dimensions, and the amount symbol, which is not drawn. An independent OFL-licensed
drawing of the same figures is the tests' cross-check, and where the two disagree (its zero) the figure
decides.

**Blank stock prints the whole face, and nothing but the MICR line enters the clear band.** On blank
stock LeaseBook prints what pre-printed stock carried: the organization's legal name and address, with
the words "Trust Account" that 21 NCAC 58A .0117(b)
requires on every check drawn on a trust account (refund checks draw only on trust accounts); the
bank's name; the check number, which is also the MICR serial; and the field labels and lines. Every face
field and line ends at least 11 pt above the clear band.

- The print offsets do not apply on blank stock. They exist to line fields up with pre-printed boxes,
  and blank stock has none; applied, they could push a field into the band, and QuestPDF cannot clip.
  Only the MICR offset moves anything, and it moves only the MICR line.
- A print is refused with `blank_stock_incomplete` (409) while the organization's legal name or the
  bank's name is missing. A refused print records nothing.
- The alignment page on blank stock is a specimen marked non-negotiable and void. It carries the
  account's real transit and On-Us fields and an all-zero serial, so it can serve as the bank's test
  sample.
- The full numbers are decrypted in Payments for the two print routes alone and go nowhere but the
  renderer. On the page they are paths, never text.
- Pre-printed output is unchanged. QuestPDF stamps a creation date into every file, so the bytes never
  repeat; the page's drawing operations do, and a test pins them by hash.

The settings shipped before the MICR line printed (part 1), and until part 3 a blank-stock print was
refused outright.

**The numbers are write-only, encrypted, and administrator-owned.** `bank_micr_profiles` (Payments,
RLS-scoped with the deny-by-default persona gate) holds, per bank account, the stock kind, the
routing number, the On-Us field and the MICR line offsets.

- The routing number and the On-Us field are encrypted at rest with ASP.NET Data Protection (purpose
  `LeaseBook.BankMicr.v1`) over the durable keyring of [ADR-041](ADR-041-durable-keyring-and-proxy-trust.md).
- Every read, for staff and administrators alike, carries only the last four digits of each. The one
  exception is a printed blank-stock check or specimen, whose MICR line is the numbers: staff print
  checks, so whoever holds one can read them, exactly as on pre-printed stock. A save
  that leaves a number empty keeps the saved one, so no form ever needs the full value back.
- Only `PMAdmin` may save. Staff read the masked view, since they print the checks.
- The routing number must be nine digits with a valid ABA check digit (weights 3, 7, 1). The On-Us
  field is stored as the bank's sheet prints it, left to right — digits, `-` for the dash symbol, a
  space for an empty position, `U` for the On-Us symbol — at most 18 characters (positions 14–31) with
  at least four digits. The check number is never part of it: a business-size check carries it in the
  Auxiliary On-Us field.
- The numbers never reach logs, telemetry or browser storage, and the SPA saves them without a query
  or mutation cache entry that would hold them.

**Audit snapshots withhold secret-named values when they are written.** The automatic audit pass
writes a secret-named column (`AuditFieldRedaction.SensitiveNameFragments`, which includes
`routingnumber` and `accountnumber`) as `[redacted]`, or `[redacted] (changed)` on the after side of
an update that moved it. The trail records who changed a bank account's MICR details and which ones,
never the values. This amends [ADR-044](ADR-044-audit-log-review-surface.md), which withheld such
values only when rendering. Externally sourced import payloads are unchanged: they are still stored
and withheld on display. An audited entity that gains a secret-named column still fails
`AuditPayloadExposureTests` until the column is recorded as decided.

## Consequences

- No font license to track, and nothing in the image beyond code. The cost is authoring and
  cross-checking 14 glyphs from scanned figures whose dimension leaders are ambiguous in places; a
  geometry error is caught only by a bank test.
- Deferring US placement to bank testing and a per-account offset keeps the feature buildable without
  the paid standard, at the price of a systematic offset error surfacing late, at the bank.
- No screen or response shows a saved number back. Correcting one means typing it again, and an
  operator who needs to confirm it reads it from the bank's sheet. The printed MICR line carries the
  numbers, as any check does, and staff can print a specimen as well as a check.
- A changed routing number is visible in the audit trail as a change, but its old and new values are
  not recoverable from the trail.
- On blank stock a printer that registers the page off its nominal position is corrected only through
  the MICR offset; the face moves with the paper. The [operator runbook](../runbooks/blank-stock-checks.md)
  owns the bank's test-check approval.

## Revisit trigger

- A bank rejects test checks whose geometry matches the drawn glyphs: reopen the glyph source.
- The maintainers obtain X9.100-160-1 or X9.100-20: replace the Standard 006 defaults with the US
  values and narrow the per-account offset.
- A need arises to show full bank numbers to anyone, or to let a non-administrator change them.
