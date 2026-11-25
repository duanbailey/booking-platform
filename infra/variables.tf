variable "region" {
  description = "AWS region (decided 2026-11-24: us-east-2). Must match the backend's region in versions.tf."
  type        = string
  default     = "us-east-2"
}

variable "project" {
  description = "Name prefix for resources; must match the bootstrap stack's."
  type        = string
  default     = "booking-platform"
}

variable "environment" {
  description = "Environment name, part of resource names."
  type        = string
  default     = "dev"
}

variable "vpc_cidr" {
  description = "The VPC's address range. Also the API's trusted proxy network (ForwardedHeaders:KnownNetworks)."
  type        = string
  default     = "10.0.0.0/16"
}

variable "api_image" {
  description = "Full image reference (repository:tag) the API service runs. infra/deploy.sh sets it."
  type        = string
}

variable "migrate_image" {
  description = "Image the `db migrate` task runs; the deploy script moves it to a new image before api_image (migrate, then roll out)."
  type        = string
}

variable "worker_package_dir" {
  description = "Directory holding the worker's Lambda package (dist plus production node_modules), staged by infra/deploy.sh."
  type        = string
  default     = "../worker/.package"
}

variable "email_from" {
  description = "Sender address for all email. SES sends a verification email to it on the first apply; the sandbox only delivers to verified addresses."
  type        = string
}

variable "alarm_email" {
  description = "Where alarm notifications go (SNS email subscription; AWS sends a confirmation email first). Empty: no subscription."
  type        = string
  default     = ""
}

variable "app_base_url" {
  description = "APP_BASE_URL for invite and recovery links (no frontend yet). Empty: the API's CloudFront URL."
  type        = string
  default     = ""
}

variable "app_name" {
  description = "APP_NAME: the product name in emails."
  type        = string
  default     = "Bookly"
}

variable "secret_version" {
  description = "Bump to generate new values for the JWT signing key and the database users' passwords (write-only secrets)."
  type        = number
  default     = 1
}

variable "api_desired_count" {
  description = "Number of API tasks."
  type        = number
  default     = 1
}

variable "github_repository" {
  description = "The repository whose main branch may assume the CI role for the real-AWS tests (infra/ci.tf), written as in GitHub's OIDC subject claim: owner@<owner id>/<name>@<repository id> (GitHub includes the immutable IDs; the failed AssumeRoleWithWebIdentity event in CloudTrail shows the exact value). Empty: no CI role."
  type        = string
  default     = ""
}
