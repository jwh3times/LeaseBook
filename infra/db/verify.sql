-- Read-only restore spot-check. This is not a replacement for check-invariants --all.
\getenv org_id LEASEBOOK_ORG_ID
BEGIN READ ONLY;
SELECT set_config('app.org_id', :'org_id', true) AS organization;
SELECT current_database() AS database, current_user AS role, inet_server_addr() AS server;
SELECT EXISTS (SELECT FROM public.orgs WHERE id = :'org_id'::uuid) AS org_exists \gset
\if :org_exists
SELECT count(*) AS journal_entry_count, max(entry_date) AS latest_entry_date
FROM public.journal_entries;
\else
DO $$ BEGIN RAISE EXCEPTION 'Organization does not exist'; END $$;
\endif
COMMIT;
