#!/usr/bin/env bash
set -euo pipefail
source .github/scripts/release_support.sh
source .github/scripts/release_ecs.sh

: "${ECS_CLUSTER:?}" "${ECS_SERVICE:?}" "${ALB_TARGET_GROUP_ARN:?}" "${DATABASE_GENERATION:?}" "${UI_ORIGIN:?}"
ensure_current_revision
migration=$(aws ssm get-parameter --name /scrapbook/hlg/liquibase/active --query Parameter.Value --output text)
jq -e --arg generation "$DATABASE_GENERATION" \
    '.environment == "hlg" and .generation == $generation and .result == "success"' <<< "$migration" >/dev/null
record databaseVersion "$(jq -er .version <<< "$migration")"
record databaseRevision "$(jq -er .sha <<< "$migration")"
record databaseGeneration "$DATABASE_GENERATION"

service=$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" --output json |
    jq -e --arg group "$ALB_TARGET_GROUP_ARN" '
        .services[0] | select(.status == "ACTIVE" and .deploymentController.type == "ECS" and
            ([.loadBalancers[] | select(.targetGroupArn == $group and .containerName == "api" and
                .containerPort == 8080)] | length) == 1)')
aws elbv2 describe-target-groups --target-group-arns "$ALB_TARGET_GROUP_ARN" --output json |
    jq -e '.TargetGroups[0] | .TargetType == "ip" and .HealthCheckPath == "/health/ready" and
        .HealthCheckPort == "traffic-port" and .Matcher.HttpCode == "200"' >/dev/null

current_task=$(jq -er .taskDefinition <<< "$service")
aws ecs describe-task-definition --task-definition "$current_task" --output json |
    jq -e --arg origin "$UI_ORIGIN" '
        .taskDefinition.containerDefinitions[] | select(.name == "api") |
        any(.environment[]; .name == "ASPNETCORE_ENVIRONMENT" and .value == "Homologation") and
        any(.environment[]; .name == "Hosting__PublicUiOrigin" and .value == $origin) and
        any(.environment[]; .name == "Database__RequireTls" and .value == "true") and
        (all(.environment[]; .name != "ConnectionStrings__ScrapBook")) and
        any(.secrets[]; .name == "ConnectionStrings__ScrapBook" and (.valueFrom | startswith("arn:aws:secretsmanager:"))) and
        .logConfiguration.logDriver == "awslogs"' >/dev/null

image=$(build_immutable_image --build-arg "SCRAPBOOK_RELEASE_VERSION=${RELEASE_VERSION#v}")
record image "$image"
task=$(register_task "$current_task" api "$image")
[[ "$task" == arn:aws:ecs:us-east-1:"$AWS_ACCOUNT_ID":task-definition/* ]]
record taskDefinition "$task"
ensure_current_revision
arguments=(--cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --task-definition "$task")
if [[ $(jq -er '.desiredCount' <<< "$service") == 0 ]]; then
    arguments+=(--desired-count 1)
fi
aws ecs update-service "${arguments[@]}" >/dev/null
aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE"
aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" --output json |
    jq -e --arg task "$task" '.services[0] | .taskDefinition == $task and .runningCount == .desiredCount
        and .runningCount > 0 and (.deployments | length) == 1' >/dev/null
task_arns=$(aws ecs list-tasks --cluster "$ECS_CLUSTER" --service-name "$ECS_SERVICE" --output json |
    jq -er '.taskArns | if length > 0 then .[] else empty end')
tasks=()
while IFS= read -r task_arn; do
    tasks+=("$task_arn")
done <<< "$task_arns"
running=$(aws ecs describe-tasks --cluster "$ECS_CLUSTER" --tasks "${tasks[@]}" --output json)
jq -e --arg task "$task" --arg digest "${image##*@}" '
        (.failures | length) == 0 and (.tasks | length) > 0 and
        all(.tasks[]; .taskDefinitionArn == $task and .lastStatus == "RUNNING" and
            any(.containers[]; .name == "api" and .imageDigest == $digest))' <<< "$running" >/dev/null
addresses=$(jq -ce '.tasks | map(
    [.attachments[]?.details[]? | select(.name == "privateIPv4Address") | .value]
    | if length == 1 then .[0] else error("A tarefa deve ter um endereço IPv4 privado.") end
)' <<< "$running")
aws elbv2 describe-target-health --target-group-arn "$ALB_TARGET_GROUP_ARN" --output json |
    jq -e --argjson addresses "$addresses" '.TargetHealthDescriptions as $targets |
        all($addresses[]; . as $ip | any($targets[]; .Target.Id == $ip and .Target.Port == 8080
            and .TargetHealth.State == "healthy"))' >/dev/null
record activeImage "$image"
