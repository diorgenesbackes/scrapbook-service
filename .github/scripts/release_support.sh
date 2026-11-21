#!/usr/bin/env bash

verify_reservation() {
    local current_key="${GITHUB_RUN_ID:?}.${GITHUB_RUN_ATTEMPT:?}"
    if [[ "${RESERVED_RUN_KEY:-}" != "$current_key" ]]; then
        echo "A reserva pertence a outra tentativa. Reexecute todos os jobs para reservar uma nova versão." >&2
        return 1
    fi
}

start_evidence() {
    verify_reservation || return 1
    : "${RELEASE_VERSION:?}" "${GITHUB_SHA:?}" "${GITHUB_REPOSITORY:?}"
    jq -n --arg sha "$GITHUB_SHA" --arg version "$RELEASE_VERSION" --arg repository "$GITHUB_REPOSITORY" \
        --arg runId "$GITHUB_RUN_ID" --arg attempt "$GITHUB_RUN_ATTEMPT" \
        --arg time "$(date -u +%FT%TZ)" \
        '{sha: $sha, version: $version, repository: $repository, branch: "hlg", environment: "hlg",
          runId: $runId, attempt: $attempt, timestamp: $time, result: "incomplete"}' > release-evidence.json
}

record() {
    local temporary
    temporary=$(mktemp)
    jq --arg field "$1" --arg value "$2" '.[$field] = $value' release-evidence.json > "$temporary"
    mv "$temporary" release-evidence.json
}

ensure_current_revision() {
    : "${GH_TOKEN:?}"
    local remote_sha
    remote_sha=$(gh api "repos/$GITHUB_REPOSITORY/git/ref/heads/hlg" --jq .object.sha)
    [[ "${GITHUB_REF_NAME:-}" == hlg && "$remote_sha" == "$GITHUB_SHA" ]] || {
        echo "A revisão da execução já não é a ponta de hlg." >&2
        return 1
    }
}
