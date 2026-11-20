#!/usr/bin/env bash

build_immutable_image() {
    : "${AWS_ACCOUNT_ID:?}" "${ECR_REPOSITORY:?}"
    local registry uri digest
    registry="$AWS_ACCOUNT_ID.dkr.ecr.us-east-1.amazonaws.com"
    uri="$registry/$ECR_REPOSITORY"
    [[ "$AWS_ACCOUNT_ID" =~ ^[0-9]{12}$ && "$ECR_REPOSITORY" =~ ^[a-z0-9][a-z0-9_./-]+$ ]] || return 1
    [[ $(aws ecr describe-repositories --repository-names "$ECR_REPOSITORY" \
        --query 'repositories[0].imageTagMutability' --output text) == IMMUTABLE ]] || return 1
    aws ecr get-login-password | docker login --username AWS --password-stdin "$registry" >&2 || return 1
    docker build --platform linux/amd64 --provenance=false --sbom=false \
        --label "org.opencontainers.image.revision=$GITHUB_SHA" \
        --label "org.opencontainers.image.version=$RELEASE_VERSION" --tag "$uri:$RELEASE_VERSION" "$@" . >&2 || return 1
    docker push "$uri:$RELEASE_VERSION" >&2 || return 1
    digest=$(aws ecr describe-images --repository-name "$ECR_REPOSITORY" \
        --image-ids "imageTag=$RELEASE_VERSION" --query 'imageDetails[0].imageDigest' --output text) || return 1
    [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || return 1
    printf '%s@%s\n' "$uri" "$digest"
}

register_task() {
    local base="$1" name="$2" image="$3"
    aws ecs describe-task-definition --task-definition "$base" --output json |
        jq -e --arg name "$name" --arg image "$image" '
            .taskDefinition
            | if .status != "ACTIVE" or .networkMode != "awsvpc"
                or (.requiresCompatibilities | index("FARGATE")) == null
                or (.runtimePlatform.cpuArchitecture // "X86_64") != "X86_64"
                or (.runtimePlatform.operatingSystemFamily // "LINUX") != "LINUX"
                or ([.containerDefinitions[] | select(.name == $name and .essential != false)] | length) != 1
              then error("A definição ECS deve conter o contêiner essencial no Fargate Linux x86-64.")
              else . end
            | del(.taskDefinitionArn, .revision, .status, .requiresAttributes, .compatibilities,
                  .registeredAt, .registeredBy, .deregisteredAt)
            | (.containerDefinitions[] | select(.name == $name) | .image) = $image
        ' | aws ecs register-task-definition --cli-input-json file:///dev/stdin \
            --query 'taskDefinition.taskDefinitionArn' --output text
}
