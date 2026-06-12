using '../main.bicep'

param env = 'dev'
param location = 'eastus2'
param postgresAdminLogin = 'lbadmin'
// Supplied at deploy time via the LEASEBOOK_PG_ADMIN_PASSWORD env var; never committed.
param postgresAdminPassword = readEnvironmentVariable('LEASEBOOK_PG_ADMIN_PASSWORD', '')
