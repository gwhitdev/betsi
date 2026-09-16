# Local secret files

The `app` profile in `docker-compose.yml` mounts these as Docker secrets, and the service
resolves them through `secret:` references in its configuration (see
`Betsi/Infrastructure/Configuration/SecretReferences.cs`). They are deliberately not in git.

Create them before running `docker compose --profile app up`:

```bash
printf 'Server=sqlserver,1433;Database=betsi_control;User Id=sa;Password=Betsi_Dev_Password1;TrustServerCertificate=True' > secrets/control-plane
printf 'Server=sqlserver,1433;User Id=sa;Password=Betsi_Dev_Password1;TrustServerCertificate=True' > secrets/database-server
```

These are development credentials for a container on your own machine. A deployed environment
gets the same two files from its platform's secret store, never from a repository — see
`docs/runbooks/deployment.md`.
