# E-13B reference outlines (test fixture)

Independent SVG outlines of the 14 E-13B characters, used only by `E13BGlyphTests` (LeaseBook.Tests.Web) to cross-check
LeaseBook's own glyph geometry (#474, ADR-051). They are not shipped, rendered or embedded anywhere.

- **Source:** [`zaxbux/MICR_E13-B_Font`](https://github.com/zaxbux/MICR_E13-B_Font), commit
  `d5c81f63f925922ee9bae0dfd09a74a187ef483c`, directory `svgs/`, unmodified.
- **Licence:** SIL Open Font License 1.1 — see `LICENCE.md` in this directory.
- **Known deviation:** the zero (`u0030.svg`) is 13.61 grid units wide with a thin left stroke;
  Standard 006 Fig. 1.8.1 dimensions it at 14 units. The tests pin that difference rather than follow
  the outline.
