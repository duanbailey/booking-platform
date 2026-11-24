#!/usr/bin/env bash
# One-command deploy (T27b, decided 2026-11-24). Runs in Git Bash, macOS/Linux and CI.
#
#   infra/deploy.sh bootstrap   Once per account: the state bucket and the API's ECR repository (infra/bootstrap).
#   infra/deploy.sh plan        Build the worker package and show what a deploy would change (no image push).
#   infra/deploy.sh             Deploy the current commit:
#                                 1. build and push the API image (tag = commit), build the worker package
#                                 2. apply: point the `db migrate` task at the new image (the API keeps its image)
#                                 3. run `db migrate` as a one-off task and wait for it to succeed
#                                 4. apply: point the API service at the new image, and wait until it is stable
#                               Migrations therefore run before new code serves traffic. The API image and the worker
#                               come from the same commit, so the grants file db migrate applies matches the worker.
#
# Needs: docker, terraform (>= 1.11), aws (v2), node and npm on the PATH; AWS credentials for the target account
# (e.g. AWS_PROFILE); infra/dev.tfvars (copy infra/dev.tfvars.example). Every apply shows its plan and asks for
# confirmation unless AUTO_APPROVE=1. A dirty working tree is refused unless ALLOW_DIRTY=1, since images are tagged
# by commit.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
INFRA="$ROOT/infra"
TFVARS="${TFVARS:-$INFRA/dev.tfvars}"

die() { echo "deploy: $*" >&2; exit 1; }
step() { echo; echo "==> $*"; }
need() { command -v "$1" >/dev/null 2>&1 || die "$1 is not on the PATH"; }
tf() { terraform -chdir="$INFRA" "$@"; }
approve() { if [ "${AUTO_APPROVE:-0}" = "1" ]; then echo "-auto-approve"; fi; }

check_tools() {
  for tool in "$@"; do need "$tool"; done
  aws sts get-caller-identity --query Account --output text >/dev/null || die "no AWS credentials (set AWS_PROFILE or run aws configure)"
}

bootstrap_output() {
  terraform -chdir="$INFRA/bootstrap" output -raw "$1" 2>/dev/null || die "bootstrap stack not applied: run infra/deploy.sh bootstrap first"
}

init_main() {
  tf init -input=false -reconfigure -backend-config="bucket=$(bootstrap_output state_bucket)" >/dev/null
}

# The worker's Lambda package: compiled dist (with dist/schemas) plus production dependencies only.
build_worker_package() {
  step "Building the worker package"
  (cd "$ROOT/worker" && npm ci --no-audit --no-fund && npm run build)
  rm -rf "$ROOT/worker/.package"
  mkdir -p "$ROOT/worker/.package"
  cp -R "$ROOT/worker/dist/." "$ROOT/worker/.package/"
  cp "$ROOT/worker/package.json" "$ROOT/worker/package-lock.json" "$ROOT/worker/.package/"
  (cd "$ROOT/worker/.package" && npm ci --omit=dev --ignore-scripts --no-audit --no-fund)
}

current_api_image() {
  tf output -raw api_image 2>/dev/null || true
}

# --------------------------------------------------------------------------------------------------- commands

cmd_bootstrap() {
  check_tools terraform aws
  step "Applying the bootstrap stack (state bucket, ECR repository)"
  terraform -chdir="$INFRA/bootstrap" init -input=false >/dev/null
  # shellcheck disable=SC2046
  terraform -chdir="$INFRA/bootstrap" apply -input=false $(approve)
}

cmd_plan() {
  check_tools terraform aws node npm
  [ -f "$TFVARS" ] || die "missing $TFVARS (copy infra/dev.tfvars.example)"
  build_worker_package
  init_main
  local image
  image="$(current_api_image)"
  [ -n "$image" ] || image="$(bootstrap_output api_repository_url):not-built-yet"
  step "Planning against image $image"
  tf plan -input=false -var-file="$TFVARS" -var "api_image=$image" -var "migrate_image=$image"
}

cmd_deploy() {
  check_tools docker terraform aws node npm
  [ -f "$TFVARS" ] || die "missing $TFVARS (copy infra/dev.tfvars.example)"
  if [ "${ALLOW_DIRTY:-0}" != "1" ] && [ -n "$(git -C "$ROOT" status --porcelain)" ]; then
    die "the working tree has uncommitted changes; commit them (images are tagged by commit) or set ALLOW_DIRTY=1"
  fi

  local repository tag new_image registry
  repository="$(bootstrap_output api_repository_url)"
  tag="$(git -C "$ROOT" rev-parse --short=12 HEAD)"
  [ "${ALLOW_DIRTY:-0}" = "1" ] && [ -n "$(git -C "$ROOT" status --porcelain)" ] && tag="$tag-dirty-$(date +%s)"
  new_image="$repository:$tag"
  registry="${repository%%/*}"

  step "API image $new_image"
  if aws ecr describe-images --repository-name "${repository#*/}" --image-ids "imageTag=$tag" >/dev/null 2>&1; then
    echo "Already in ECR; not rebuilding."
  else
    aws ecr get-login-password | docker login --username AWS --password-stdin "$registry" >/dev/null
    docker build --platform linux/amd64 -f "$ROOT/src/BookingPlatform.Api/Dockerfile" -t "$new_image" "$ROOT"
    docker push "$new_image"
  fi

  build_worker_package
  init_main

  local current_image
  current_image="$(current_api_image)"
  if [ -z "$current_image" ]; then
    # First deploy: no running API yet, so the service starts on the new image; its health check doesn't need the
    # schema, and requests made before db migrate finishes fail rather than run against a wrong schema.
    current_image="$new_image"
  fi

  step "Apply 1 of 2: the migrate task gets the new image (the API keeps $current_image)"
  # shellcheck disable=SC2046
  tf apply -input=false -var-file="$TFVARS" -var "api_image=$current_image" -var "migrate_image=$new_image" $(approve)

  run_migrate

  if [ "$current_image" != "$new_image" ]; then
    step "Apply 2 of 2: the API service gets the new image"
    # shellcheck disable=SC2046
    tf apply -input=false -var-file="$TFVARS" -var "api_image=$new_image" -var "migrate_image=$new_image" $(approve)
  fi

  step "Waiting for the API service to be stable"
  aws ecs wait services-stable --cluster "$(tf output -raw cluster)" --services "$(tf output -raw api_service)"

  local url
  url="$(tf output -raw api_url)"
  step "Deployed $tag"
  echo "API: $url"
  curl -fsS "$url/health" && echo || echo "(the health check through CloudFront did not answer yet; CloudFront can take a few minutes after the first deploy)"
}

run_migrate() {
  step "Running db migrate"
  local cluster subnets security_group task_arn exit_code
  cluster="$(tf output -raw cluster)"
  subnets="$(tf output -json private_subnets | tr -d '[]" \n')"
  security_group="$(tf output -raw api_security_group)"
  task_arn="$(aws ecs run-task --cluster "$cluster" --launch-type FARGATE \
    --task-definition "$(tf output -raw migrate_task_definition)" \
    --network-configuration "awsvpcConfiguration={subnets=[$subnets],securityGroups=[$security_group],assignPublicIp=DISABLED}" \
    --query 'tasks[0].taskArn' --output text)"
  [ -n "$task_arn" ] && [ "$task_arn" != "None" ] || die "could not start the migrate task"
  echo "Task $task_arn"
  aws ecs wait tasks-stopped --cluster "$cluster" --tasks "$task_arn"
  exit_code="$(aws ecs describe-tasks --cluster "$cluster" --tasks "$task_arn" --query 'tasks[0].containers[0].exitCode' --output text)"
  if [ "$exit_code" != "0" ]; then
    aws ecs describe-tasks --cluster "$cluster" --tasks "$task_arn" --query 'tasks[0].[stoppedReason,containers[0].reason]' --output text >&2 || true
    die "db migrate failed (exit $exit_code); its output is in the $(tf output -raw api_log_group) log group, stream prefix migrate. The API was not updated."
  fi
  echo "db migrate succeeded."
}

# Sourcing the script (e.g. to test one step) defines the functions without running a command.
if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  case "${1:-deploy}" in
    bootstrap) cmd_bootstrap ;;
    plan) cmd_plan ;;
    deploy) cmd_deploy ;;
    -h | --help | help) sed -n '2,20p' "${BASH_SOURCE[0]}" ;;
    *) die "unknown command '$1' (bootstrap, plan or deploy)" ;;
  esac
fi
