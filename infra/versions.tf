# Main stack (T27b, ADR 0009). State in the bootstrap stack's S3 bucket with S3 native locking; the bucket name is
# passed at init by infra/deploy.sh (-backend-config="bucket=..."), since it contains the account ID.

terraform {
  required_version = ">= 1.11" # ephemeral resources and write-only arguments keep generated secrets out of state

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.7"
    }
    archive = {
      source  = "hashicorp/archive"
      version = "~> 2.7"
    }
  }

  backend "s3" {
    key          = "booking-platform/dev/terraform.tfstate"
    region       = "us-east-2"
    encrypt      = true
    use_lockfile = true
  }
}

provider "aws" {
  region = var.region
  default_tags {
    tags = { Project = var.project, Environment = var.environment, ManagedBy = "terraform" }
  }
}

data "aws_caller_identity" "current" {}
data "aws_region" "current" {}
