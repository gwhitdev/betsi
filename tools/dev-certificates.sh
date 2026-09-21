#!/usr/bin/env bash
#
# Creates the two certificates a deployment needs, for local development.
#
#   1. A SQL Server certificate that encrypts backups (`tenants backup --certificate`).
#   2. A PKCS#12 certificate that encrypts the Data Protection key ring at rest, which the
#      service otherwise warns about on every start.
#
# Usage:
#   dev-certificates.sh
#
# These are self-signed and their passwords are in this file. That is fine for a container on
# your own machine and is not fine anywhere else: a deployment gets both from its own
# certificate authority or key vault, and the private keys never touch a repository. The two
# procedures below are what a site follows — see docs/runbooks/deployment.md §4 and
# docs/runbooks/backup-and-restore.md §2.
#
# The backup certificate is the one people forget: an encrypted backup cannot be restored
# without it, so a copy of it has to live somewhere other than the server it protects.

set -euo pipefail

CONTAINER="${CONTAINER:-betsi-sqlserver}"
SA_PASSWORD="${SA_PASSWORD:-Betsi_Dev_Password1}"
MASTER_KEY_PASSWORD="${MASTER_KEY_PASSWORD:-Betsi_Dev_MasterKey1}"
CERT_PASSWORD="${CERT_PASSWORD:-Betsi_Dev_Cert1}"
KEYRING_DIR="${KEYRING_DIR:-secrets}"
SQLCMD=(/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SA_PASSWORD" -C)

echo "==> SQL Server backup encryption certificate"
docker exec "$CONTAINER" "${SQLCMD[@]}" -Q "
IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##')
    CREATE MASTER KEY ENCRYPTION BY PASSWORD = '$MASTER_KEY_PASSWORD';

IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'betsi_backup')
    CREATE CERTIFICATE betsi_backup WITH SUBJECT = 'Betsi backup encryption (development)';
"

echo "==> Backing the certificate up, because an encrypted backup is unreadable without it"
docker exec "$CONTAINER" mkdir -p /var/opt/mssql/certs
docker exec "$CONTAINER" "${SQLCMD[@]}" -Q "
IF NOT EXISTS (SELECT 1 FROM sys.certificates WHERE name = 'betsi_backup' AND pvt_key_encryption_type = 'MK')
    PRINT 'Certificate has no private key to export.';
"
docker exec "$CONTAINER" bash -c "test -f /var/opt/mssql/certs/betsi_backup.cer" 2>/dev/null || \
docker exec "$CONTAINER" "${SQLCMD[@]}" -Q "
BACKUP CERTIFICATE betsi_backup
    TO FILE = '/var/opt/mssql/certs/betsi_backup.cer'
    WITH PRIVATE KEY (
        FILE = '/var/opt/mssql/certs/betsi_backup.key',
        ENCRYPTION BY PASSWORD = '$CERT_PASSWORD');
"

echo "==> Data protection key ring certificate"
mkdir -p "$KEYRING_DIR"
if [[ -f "$KEYRING_DIR/keyring.pfx" ]]; then
    echo "    $KEYRING_DIR/keyring.pfx already exists; leaving it alone."
else
    TEMP=$(mktemp -d)
    trap 'rm -rf "$TEMP"' EXIT

    # Ten years: rotating this certificate means re-encrypting the key ring, which is a
    # deliberate operation rather than something to be surprised by mid-shift.
    openssl req -x509 -newkey rsa:2048 -nodes \
        -keyout "$TEMP/keyring.key" -out "$TEMP/keyring.crt" \
        -days 3650 -subj "/CN=Betsi data protection (development)" 2>/dev/null

    openssl pkcs12 -export \
        -inkey "$TEMP/keyring.key" -in "$TEMP/keyring.crt" \
        -out "$KEYRING_DIR/keyring.pfx" -passout "pass:$CERT_PASSWORD" 2>/dev/null

    printf '%s' "$CERT_PASSWORD" > "$KEYRING_DIR/keyring-password"
    chmod 600 "$KEYRING_DIR/keyring.pfx" "$KEYRING_DIR/keyring-password"
    echo "    wrote $KEYRING_DIR/keyring.pfx"
fi

cat <<NOTE

Done. To use them:

  # Encrypted backups
  dotnet run --project Betsi -- tenants backup --id <guid> \\
      --directory /var/opt/mssql/backups --certificate betsi_backup

  # Encrypted key ring (appsettings.Development.json, or environment variables)
  DataProtection__CertificatePath=$KEYRING_DIR/keyring.pfx
  DataProtection__CertificatePassword=secret:file:$KEYRING_DIR/keyring-password

$KEYRING_DIR/ is git-ignored. The SQL Server certificate and its private key are inside the
container at /var/opt/mssql/certs — in a deployment they belong somewhere else entirely, because
a backup and the key to read it should not share a failure.
NOTE
