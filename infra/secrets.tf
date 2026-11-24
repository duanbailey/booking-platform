# Generated secrets (T27b, decided 2026-11-24): the JWT signing key and the passwords of the API's and worker's
# database users. Ephemeral random values written with Secrets Manager's write-only argument, so they never reach the
# Terraform state or plan. They are written again only when var.secret_version changes: bump it to rotate, then deploy
# (db migrate sets the new database passwords, keeping the old ones valid until its next run).

ephemeral "random_password" "jwt_signing_key" {
  length  = 64
  special = false
}

ephemeral "random_password" "db_api" {
  length  = 40
  special = false
}

ephemeral "random_password" "db_worker" {
  length  = 40
  special = false
}

resource "aws_secretsmanager_secret" "jwt_signing_key" {
  name                    = "${local.name}/jwt-signing-key"
  recovery_window_in_days = 0 # dev: destroy deletes at once, so a rebuild can reuse the name
}

resource "aws_secretsmanager_secret_version" "jwt_signing_key" {
  secret_id                = aws_secretsmanager_secret.jwt_signing_key.id
  secret_string_wo         = ephemeral.random_password.jwt_signing_key.result
  secret_string_wo_version = var.secret_version
}

resource "aws_secretsmanager_secret" "db_api" {
  name                    = "${local.name}/db-api-password"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "db_api" {
  secret_id                = aws_secretsmanager_secret.db_api.id
  secret_string_wo         = ephemeral.random_password.db_api.result
  secret_string_wo_version = var.secret_version
}

resource "aws_secretsmanager_secret" "db_worker" {
  name                    = "${local.name}/db-worker-password"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "db_worker" {
  secret_id                = aws_secretsmanager_secret.db_worker.id
  secret_string_wo         = ephemeral.random_password.db_worker.result
  secret_string_wo_version = var.secret_version
}
