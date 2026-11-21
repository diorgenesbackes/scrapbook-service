#!/usr/bin/env bash
set -euo pipefail
source .github/scripts/release_support.sh
source .github/scripts/release_ecs.sh
source .github/scripts/activate_ecs.sh

: "${ECS_CLUSTER:?}" "${ECS_SERVICE:?}" "${ALB_TARGET_GROUP_ARN:?}" "${DATABASE_GENERATION:?}" "${UI_ORIGIN:?}"
ensure_current_revision
check_database_generation
service=$(read_api_service)
check_target_group
current_task=$(jq -er .taskDefinition <<< "$service")
check_api_task_config "$current_task"

image=$(build_immutable_image --build-arg "SCRAPBOOK_RELEASE_VERSION=${RELEASE_VERSION#v}")
record image "$image"
task=$(register_task "$current_task" api "$image")
[[ "$task" == arn:aws:ecs:us-east-1:"$AWS_ACCOUNT_ID":task-definition/* ]]
record taskDefinition "$task"
ensure_current_revision
activate_api_task "$service" "$task" "$image" true
record activeImage "$image"
