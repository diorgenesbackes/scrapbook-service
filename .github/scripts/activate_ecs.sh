#!/usr/bin/env bash

check_database_generation() {
    local migration migration_provenance database_version database_revision
    migration=$(aws ssm get-parameter --name "/scrapbook/$target/liquibase/active" --query Parameter.Value --output text)
    migration_provenance=$(jq -er --arg environment "$target" --arg generation "$DATABASE_GENERATION" '
        select(type == "object" and .environment == $environment and .generation == $generation and .result == "success")
        | select(.version | strings | test("^v(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)\\z"))
        | select(.sha | strings | test("^[0-9a-f]{40}\\z"))
        | [.version, .sha] | @tsv
    ' <<< "$migration") || {
        echo "O marcador de migração deve identificar o ambiente, a geração, a versão e o SHA esperados." >&2
        exit 1
    }
    IFS=$'\t' read -r database_version database_revision <<< "$migration_provenance"
    record databaseVersion "$database_version"
    record databaseRevision "$database_revision"
    record databaseGeneration "$DATABASE_GENERATION"
}

read_api_service() {
    aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" --output json |
        jq -e --arg group "$ALB_TARGET_GROUP_ARN" --arg target "$target" '
            .services[0] | select(.status == "ACTIVE" and .deploymentController.type == "ECS" and
                ($target != "production" or (.availabilityZoneRebalancing == "ENABLED" and
                    (.desiredCount == 0 or .desiredCount >= 2))) and
                ([.loadBalancers[] | select(.targetGroupArn == $group and .containerName == "api" and
                    .containerPort == 8080)] | length) == 1)'
}

check_target_group() {
    aws elbv2 describe-target-groups --target-group-arns "$ALB_TARGET_GROUP_ARN" --output json |
        jq -e '.TargetGroups[0] | .TargetType == "ip" and .HealthCheckPath == "/health/ready" and
            .HealthCheckPort == "traffic-port" and .Matcher.HttpCode == "200"' >/dev/null
}

check_api_task_config() {
    local app_environment=Homologation
    if [[ "$target" == production ]]; then
        app_environment=Production
        [[ "${DATABASE_SECRET_ARN:?}" == "arn:aws:secretsmanager:us-east-1:$AWS_ACCOUNT_ID:secret:"?* ]] || return 1
    fi
    aws ecs describe-task-definition --task-definition "$1" --output json |
        jq -e --arg origin "$UI_ORIGIN" --arg appEnvironment "$app_environment" \
            --arg target "$target" --arg databaseSecret "${DATABASE_SECRET_ARN:-}" '
            .taskDefinition.containerDefinitions[] | select(.name == "api") |
            ([.environment[] | select(.name == "ASPNETCORE_ENVIRONMENT") | .value] == [$appEnvironment]) and
            any(.environment[]; .name == "Hosting__PublicUiOrigin" and .value == $origin) and
            any(.environment[]; .name == "Database__RequireTls" and .value == "true") and
            (all(.environment[]; .name != "ConnectionStrings__ScrapBook")) and
            any(.secrets[]; .name == "ConnectionStrings__ScrapBook" and (.valueFrom | startswith("arn:aws:secretsmanager:"))) and
            ($target != "production" or (
                ([.environment[] | select(.name == "DevelopmentIdentity__Enabled") | .value] == ["false"]) and
                all(.environment[]; .name != "DOTNET_ENVIRONMENT") and
                all(.secrets[]; .name != "ASPNETCORE_ENVIRONMENT" and .name != "DOTNET_ENVIRONMENT" and
                    (.name | startswith("DevelopmentIdentity__") | not)) and
                ((.environmentFiles // []) | length) == 0 and
                ((.entryPoint // []) | length) == 0 and ((.command // []) | length) == 0 and
                ([.secrets[] | select(.name == "ConnectionStrings__ScrapBook") | .valueFrom] == [$databaseSecret])
            )) and
            .logConfiguration.logDriver == "awslogs"' >/dev/null
}

activate_api_task() {
    local service="$1" task="$2" image="$3" bootstrap="$4"
    local task_arns running addresses task_arn desired minimum=1
    local -a arguments tasks
    [[ "$target" != production ]] || minimum=2
    arguments=(--cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --task-definition "$task")
    if [[ $(jq -er '.desiredCount' <<< "$service") == 0 && "$bootstrap" == true ]]; then
        arguments+=(--desired-count "$minimum")
    fi
    aws ecs update-service "${arguments[@]}" >/dev/null
    aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE"
    desired=$(read_api_service | jq -er --arg task "$task" --argjson minimum "$minimum" '
        select(.taskDefinition == $task and .runningCount == .desiredCount
            and .runningCount >= $minimum and (.deployments | length) == 1) | .desiredCount')
    task_arns=$(aws ecs list-tasks --cluster "$ECS_CLUSTER" --service-name "$ECS_SERVICE" --output json |
        jq -er '.taskArns | if length > 0 then .[] else empty end')
    tasks=()
    while IFS= read -r task_arn; do
        tasks+=("$task_arn")
    done <<< "$task_arns"
    running=$(aws ecs describe-tasks --cluster "$ECS_CLUSTER" --tasks "${tasks[@]}" --output json)
    jq -e --arg task "$task" --arg digest "${image##*@}" --argjson desired "$desired" \
        --arg target "$target" '
            (.failures | length) == 0 and (.tasks | length) == $desired and
            ($target != "production" or ([.tasks[].availabilityZone | strings | select(length > 0)] | unique | length) >= 2) and
            all(.tasks[]; .taskDefinitionArn == $task and .lastStatus == "RUNNING" and
                any(.containers[]; .name == "api" and .imageDigest == $digest))' <<< "$running" >/dev/null || {
        echo "As tarefas em execução divergem da revisão, do digest ou da distribuição exigida por zona." >&2
        return 1
    }
    addresses=$(jq -ce '.tasks | map(
        [.attachments[]?.details[]? | select(.name == "privateIPv4Address") | .value]
        | if length == 1 then .[0] else error("A tarefa deve ter um endereço IPv4 privado.") end
    )' <<< "$running")
    aws elbv2 describe-target-health --target-group-arn "$ALB_TARGET_GROUP_ARN" --output json |
        jq -e --argjson addresses "$addresses" '.TargetHealthDescriptions as $targets |
            all($addresses[]; . as $ip | any($targets[]; .Target.Id == $ip and .Target.Port == 8080
                and .TargetHealth.State == "healthy"))' >/dev/null
    record runningTasks "$desired"
    if [[ "$target" == production ]]; then
        record availabilityZones "$(jq -r '[.tasks[].availabilityZone | strings] | unique | join(",")' <<< "$running")"
    fi
}
