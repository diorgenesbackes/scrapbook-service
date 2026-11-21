#!/usr/bin/env bash
set -euo pipefail
source .github/scripts/release_support.sh
source .github/scripts/release_ecs.sh
source .github/scripts/activate_ecs.sh

: "${ECS_CLUSTER:?}" "${ECS_SERVICE:?}" "${ALB_TARGET_GROUP_ARN:?}" "${DATABASE_GENERATION:?}" "${UI_ORIGIN:?}"
: "${AWS_ACCOUNT_ID:?}" "${ECR_REPOSITORY:?}" "${EXPECTED_TASK:?}" "${RECOVERY_EVIDENCE:?}"
[[ "$AWS_ACCOUNT_ID" =~ ^[0-9]{12}$ && "$ECR_REPOSITORY" =~ ^[a-z0-9][a-z0-9_./-]+$ ]] || exit 2
[[ "$EXPECTED_TASK" =~ ^arn:aws:ecs:us-east-1:[0-9]{12}:task-definition/[a-zA-Z0-9_-]+:[0-9]+$ ]] || exit 2
[[ "$EXPECTED_TASK" == arn:aws:ecs:us-east-1:"$AWS_ACCOUNT_ID":task-definition/* ]] || exit 2
image=$(jq -er --arg generation "$DATABASE_GENERATION" '
    select(.databaseGeneration == $generation and .activeImage == .image) | .image | strings
' "$RECOVERY_EVIDENCE")
digest=${image##*@}
[[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || exit 1
[[ "$image" == "$AWS_ACCOUNT_ID.dkr.ecr.us-east-1.amazonaws.com/$ECR_REPOSITORY@$digest" ]] || exit 1
registered_digest=$(aws ecr describe-images --repository-name "$ECR_REPOSITORY" \
    --image-ids "imageTag=$SOURCE_VERSION" --query 'imageDetails[0].imageDigest' --output text)
[[ "$registered_digest" == "$digest" ]] || {
    echo "A versão do ECR diverge do digest da publicação conhecida." >&2
    exit 1
}

check_database_generation
service=$(read_api_service)
jq -e --arg task "$EXPECTED_TASK" '.taskDefinition == $task and .desiredCount > 0' <<< "$service" >/dev/null
record previousTaskDefinition "$EXPECTED_TASK"
check_target_group
check_api_task_config "$EXPECTED_TASK"
task=$(register_task "$EXPECTED_TASK" api "$image")
[[ "$task" == arn:aws:ecs:us-east-1:"$AWS_ACCOUNT_ID":task-definition/* ]] || exit 1
record taskDefinition "$task"
record image "$image"
ensure_current_revision
current_task=$(read_api_service | jq -er '.taskDefinition')
[[ "$current_task" == "$EXPECTED_TASK" ]] || {
    echo "O serviço ECS mudou desde o diagnóstico." >&2
    exit 1
}
activate_api_task "$service" "$task" "$image" false
record activeImage "$image"
