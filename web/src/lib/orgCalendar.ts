import type { UseQueryResult } from '@tanstack/react-query';
import { useOrgSettings, type OrgSettings } from '@/lib/settings';

/**
 * The time zones an organization's books may keep (#642, ADR-037), in the order the Settings select
 * shows them. The server holds the same list (`SupportedTimeZones` in Directory) and validates every
 * save against it; `OrgTimeZoneListDriftTests` fails the build if this copy drifts from that one.
 */
export const ORG_TIME_ZONES = [
  { id: 'America/New_York', name: 'Eastern' },
  { id: 'America/Chicago', name: 'Central' },
  { id: 'America/Denver', name: 'Mountain' },
  { id: 'America/Phoenix', name: 'Arizona' },
  { id: 'America/Los_Angeles', name: 'Pacific' },
  { id: 'America/Anchorage', name: 'Alaska' },
  { id: 'Pacific/Honolulu', name: 'Hawaii' },
] as const;

/**
 * The calendar date, as `YYYY-MM-DD`, that `now` falls on in `timeZone`. Not `toISOString()`, which
 * gives the UTC date: from about 19:00 Eastern that is already tomorrow.
 */
export function dateInZone(timeZone: string, now: Date = new Date()): string {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).formatToParts(now);
  const part = (type: Intl.DateTimeFormatPartTypes) =>
    parts.find((p) => p.type === type)?.value ?? '';
  return `${part('year')}-${part('month')}-${part('day')}`;
}

/** What the organization's today is, or why it is not known yet. */
export type OrgToday =
  | { status: 'ready'; today: string }
  | { status: 'pending' }
  | {
      status: 'error';
      error: NonNullable<UseQueryResult<OrgSettings>['error']>;
      retry: () => void;
      retrying: boolean;
    };

/**
 * Which day it is for the organization's books: today in the zone its settings keep (#642). A posting
 * date defaults to this, and the server refuses money dated after it. While the settings read is
 * pending or has failed there is no today to offer: a surface that posts blocks the write rather than
 * guess one, because the viewer's own zone is not the organization's.
 */
export function useOrgToday(): OrgToday {
  const settings = useOrgSettings();
  if (settings.isError) {
    return {
      status: 'error',
      error: settings.error,
      retry: () => void settings.refetch(),
      retrying: settings.isFetching,
    };
  }
  if (!settings.isSuccess || !settings.data.timeZone) return { status: 'pending' };
  return { status: 'ready', today: dateInZone(settings.data.timeZone) };
}

/** The message a posting surface shows when it cannot know the organization's today yet. */
export function orgTodayUnavailableMessage(state: Exclude<OrgToday, { status: 'ready' }>): string {
  return state.status === 'pending'
    ? 'Still loading the organization’s settings — try again in a moment.'
    : 'The organization’s settings couldn’t be loaded, so today’s date isn’t known and this can’t be posted yet.';
}
