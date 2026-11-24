# Bootstrap stack (T27b, ADR 0009): what must exist before the main stack can run. Applied once per account and
# region with local state (`infra/deploy.sh bootstrap`); its state file stays on the operator's machine and is
# git-ignored, and losing it only means these two resources must be imported to change them.
#   - the S3 bucket holding the main stack's state (versioned, encrypted, private; S3 native locking, no DynamoDB)
#   - the ECR repository for the API image, which the deploy script pushes to before the main stack's first apply

terraform {
  required_version = ">= 1.11"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}

variable "region" {
  description = "AWS region for every resource (decided 2026-11-24: us-east-2)."
  type        = string
  default     = "us-east-2"
}

variable "project" {
  description = "Name prefix for resources."
  type        = string
  default     = "booking-platform"
}

provider "aws" {
  region = var.region
  default_tags {
    tags = { Project = var.project, ManagedBy = "terraform", Stack = "bootstrap" }
  }
}

data "aws_caller_identity" "current" {}

resource "aws_s3_bucket" "state" {
  # Account ID in the name: bucket names are global.
  bucket = "${var.project}-tfstate-${data.aws_caller_identity.current.account_id}-${var.region}"
}

resource "aws_s3_bucket_versioning" "state" {
  bucket = aws_s3_bucket.state.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "state" {
  bucket = aws_s3_bucket.state.id
  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_public_access_block" "state" {
  bucket                  = aws_s3_bucket.state.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_ownership_controls" "state" {
  bucket = aws_s3_bucket.state.id
  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_ecr_repository" "api" {
  name                 = "${var.project}-api"
  image_tag_mutability = "IMMUTABLE" # tags are git commit hashes
  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "api" {
  repository = aws_ecr_repository.api.name
  policy = jsonencode({
    rules = [{
      rulePriority = 1
      description  = "Keep the 20 most recent images"
      selection    = { tagStatus = "any", countType = "imageCountMoreThan", countNumber = 20 }
      action       = { type = "expire" }
    }]
  })
}

output "state_bucket" {
  value = aws_s3_bucket.state.bucket
}

output "api_repository_url" {
  value = aws_ecr_repository.api.repository_url
}

output "region" {
  value = var.region
}
