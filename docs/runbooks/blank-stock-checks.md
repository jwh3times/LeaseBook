# Blank-stock refund checks

- **Audience:** Administrators and operators who print refund checks
- **Status:** Implemented; each bank account's approval by its bank is an operator gate
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-02

On blank check stock LeaseBook prints the whole check: the organization, the words "Trust Account",
the bank, the check number, the payee and amount fields, and the MICR line along the bottom edge.
[ADR-051](../adr/ADR-051-blank-stock-micr-details.md) records the design. The
[research note](../research/micr-e13b-refund-checks.md) holds the sources and the facts no public
source could confirm.

LeaseBook can check that the MICR line is laid out as intended. It cannot check magnetic signal
strength, toner fusion, printer registration, paper or cutting. Only the bank's test of real printed
checks covers those, so **no live check prints on blank stock until the bank has approved test checks
from this exact setup.**

## What you need from the bank

Ask the bank that holds the trust account for its **MICR specification sheet**. It gives:

- the routing number and the **On-Us field layout**: the account number plus any codes, dashes and
  On-Us symbols, position by position;
- the check stock it accepts: paper weight, grain and security features;
- how many test checks it wants, in what form, and where to send them.

## Equipment

- A laser printer with toner labelled **MICR**. Ordinary toner and inkjet ink are not magnetic, and
  bank reader-sorters cannot read the line without it.
- A printer kept for MICR printing, with the manufacturer's supplies. Everything printed with MICR toner
  is magnetic, so LeaseBook keeps the 5/8 in clear band at the bottom of the check free of anything else.
- Check-on-top voucher stock that meets the bank's sheet: the check is the top 3.5 in of a Letter sheet,
  with two stubs below.

## Set up the bank account in LeaseBook

An administrator does each step.

1. **Organization settings:** enter the legal name and address. The legal name prints at the top left
   of every check, with "Trust Account" and then the address below it.
2. **Bank account:** enter the bank's name (Institution). It prints at the top of the check.
3. **Check print settings → MICR details:** choose **Blank stock** and enter:
   - the nine-digit routing number;
   - the On-Us field exactly as the sheet prints it, left to right: digits, `-` for the dash symbol, a
     space for an empty position and `U` for the On-Us symbol, at most 18 characters with at least four
     digits. Leave the check
     number out; LeaseBook prints it in its own field.

   Saved numbers are never shown again, only their last four digits. To correct one, type it again.

Until both the legal name and the bank's name are saved, printing a check or the alignment page on
this account is refused with `blank_stock_incomplete`, and nothing is recorded as printed.

The print offsets in the same dialog apply only to pre-printed stock. On blank stock they do nothing,
because there are no pre-printed boxes to line up with.

## Print and measure the specimen

1. In the check print settings, choose **Print alignment page**. On blank stock it is a specimen
   marked **SPECIMEN — NON-NEGOTIABLE — VOID**. It carries the account's real routing number and On-Us
   field, with serial `0000`.
2. Print it at **actual size**. Turn off "fit to page" and any other scaling, which would move the MICR
   line.
3. Check the MICR line against the bank's sheet, ideally with a MICR gauge. Positions are counted from
   the right edge at 1/8 in each, and the characters sit centred 5/16 in above the bottom edge of the
   check.
4. If the whole line sits off its positions, set the **MICR offsets** and print again. Each offset is at
   most a quarter inch (18 points) either way; positive moves right and down.

## Get the bank's approval

1. Send the bank the test checks it asked for, printed from this account on this printer, toner and
   stock. Specimens are in final format, as Standard 006 recommends, not just the MICR line.
2. Wait for the bank's written approval before printing a live check.
3. **Repeat the approval whenever something in the chain changes:** the printer, the toner, the stock,
   the MICR offsets, the routing number or the On-Us field, or a LeaseBook release whose notes say the
   check layout changed.

If the bank rejects the checks, record what it measured. A rejection that points at the shape of the
characters, rather than their position, reopens ADR-051's glyph decision.

## Printing live checks

- Load the stock with the check at the top of the sheet, and print at actual size.
- The check number on the face is the number in the MICR line. If the stock is pre-numbered, issue each
  check with the number printed on its sheet, so the two match.
- Before the first mailing, put a printed check in your window envelope and confirm the payee's address
  shows through the window.
