### Added

- **Edit a property from its page** (#783). The property page header now has **Edit**, beside
  **Transfer ownership**. It opens on the property as saved and changes its address, city, state and
  zip. Emptying the city, state or zip clears it. The management-fee override is not on the form
  and is saved as it was; ownership still changes only through **Transfer ownership**. The property
  page, the properties list, the owner's page, the property's tenant pages, search, statements, the
  rent roll and a run's issued-statement check show the new address with no reload.
- **Edit a tenant from their ledger** (#783). The tenant ledger header now has **Edit**, beside
  **Late fees**, for a tenant with or without a lease. It opens on the tenant as saved and changes
  their display name, contact email and phone, and lifecycle status (current, evicting or past).
  Emptying the email or phone clears it. The ledger header, the tenants list, property pages,
  search, the rent and late-fee run previews and the rent roll show the change with no reload.
- Both dialogs save on Enter or from their save button, report a missing or invalid field on that
  field before anything is sent, show the server's reason and its reference when it refuses the
  change, and work by keyboard.

### Changed

- The scenario catalogue gains `directory-edit-property-tenant`, which reaches
  `PUT /api/directory/properties/{id}` and `PUT /api/directory/tenants/{id}`, so neither route needs
  an exemption, and the coverage record says a browser test now reaches them. The accessibility scan
  covers both dialogs.
