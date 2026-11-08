/**
 * Runs the browser's own constraints (`required`, `min`/`max`, `step`, `pattern`, a partly typed
 * date) on every field inside `container`, in document order, and reports the first that fails: the
 * browser shows its message and focus moves to that field. Returns whether every field passed.
 *
 * For the dialogs that post or move money (#711). They have no form, so Enter in a field never posts,
 * and a click handler skips these constraints unless it asks for them, which this does.
 */
export function reportFirstInvalidField(container: HTMLElement | null): boolean {
  const fields = container?.querySelectorAll<
    HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement
  >('input, select, textarea');
  const invalid = Array.from(fields ?? []).find((field) => !field.checkValidity());
  if (!invalid) return true;
  invalid.reportValidity();
  // Browsers focus it already; saying so keeps the field focused wherever reportValidity doesn't.
  invalid.focus();
  return false;
}
