# RDS MySQL (T27b, decided 2026-11-24): MySQL 8.4 as in docker-compose.yml, db.t4g.micro, single AZ, 20 GB gp3,
# encrypted, 7-day backups. Easy to destroy: no deletion protection and no final snapshot, so `terraform destroy`
# removes the data. The master password is managed by RDS in Secrets Manager and never appears in state; only the
# `db migrate` task uses it. The schema and the API's and worker's users are created by `db migrate` (T27a).

resource "aws_db_subnet_group" "main" {
  name       = local.name
  subnet_ids = aws_subnet.private[*].id
}

resource "aws_db_instance" "main" {
  identifier     = local.name
  engine         = "mysql"
  engine_version = "8.4"
  instance_class = "db.t4g.micro"

  allocated_storage = 20
  storage_type      = "gp3"
  storage_encrypted = true

  db_name                     = "booking"
  username                    = "admin"
  manage_master_user_password = true

  db_subnet_group_name   = aws_db_subnet_group.main.name
  vpc_security_group_ids = [aws_security_group.db.id]
  publicly_accessible    = false
  multi_az               = false

  backup_retention_period    = 7
  auto_minor_version_upgrade = true
  deletion_protection        = false
  skip_final_snapshot        = true
  apply_immediately          = true
}

locals {
  db_host = aws_db_instance.main.address
  db_name = aws_db_instance.main.db_name
  # The RDS-managed master secret: JSON with "username" and "password" keys.
  db_master_secret_arn = aws_db_instance.main.master_user_secret[0].secret_arn
}
