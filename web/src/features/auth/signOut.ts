import { postApiAuthLogout, unwrap } from '@/api';
import { clearRecent } from '@/features/palette/recent';

/** Ends the server session before removing browser state that must not survive logout. */
export async function signOutCurrentSession(): Promise<void> {
  await unwrap(postApiAuthLogout(), 'Unable to sign out.', { allowNoContent: true });
  clearRecent();
}
