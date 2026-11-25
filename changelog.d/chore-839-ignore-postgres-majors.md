### Changed

- Dependabot no longer proposes PostgreSQL majors for the local `postgres` image in
  `docker-compose.yml`: they are ignored until the PostgreSQL 19 upgrade is chosen (#839), which
  moves every PostgreSQL 18 pin together with production's Flexible Server version. Local
  development and CI stay on the major production targets.
