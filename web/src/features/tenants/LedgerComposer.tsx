// The composer-slot seam (§C.4). WP-04 ships this stub; WP-05 implements the inline composer (the
// Record-payment / Add-charge buttons, the in-place panel, the WP-01 mutations, and the ≤3-interaction
// telemetry). The prop shape is the contract WP-05 fills and the page compiles against.

export interface LedgerComposerProps {
  tenantId: string;
  /**
   * Called with the new entry id after a successful post. The page invalidates the ledger query and
   * flashes the new row (the "appears without navigation" contract, P59).
   */
  onPosted: (entryId: string) => void;
  /** Auto-open mode when the page is reached via the palette "Record payment" action. */
  initialMode?: 'payment' | 'charge';
}

/** Seam stub — renders nothing until WP-05 fills in the composer body. */
export function LedgerComposer(_props: LedgerComposerProps) {
  return null;
}
