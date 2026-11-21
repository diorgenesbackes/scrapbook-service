#!/usr/bin/env bash

start_recovery_evidence() {
    jq -n --arg sha "$GITHUB_SHA" --arg repository "$GITHUB_REPOSITORY" \
        --arg runId "$GITHUB_RUN_ID" --arg attempt "$GITHUB_RUN_ATTEMPT" \
        --arg time "$(date -u +%FT%TZ)" \
        '{sha: $sha, repository: $repository, branch: "hlg", environment: "hlg",
          runId: $runId, attempt: $attempt, timestamp: $time, operation: "rollback", result: "incomplete"}' \
        > release-evidence.json
}

fetch_recovery_evidence() {
    local run evidence_dir="$1"
    [[ "$SOURCE_RUN_ID" =~ ^[1-9][0-9]*$ && "$SOURCE_ATTEMPT" =~ ^[1-9][0-9]*$ ]] || return 1
    [[ "$SOURCE_SHA" =~ ^[0-9a-f]{40}$ && "$SOURCE_VERSION" =~ ^v(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)$ ]] || return 1
    ensure_current_revision || return 1
    run=$(gh api "repos/$GITHUB_REPOSITORY/actions/runs/$SOURCE_RUN_ID/attempts/$SOURCE_ATTEMPT") || return 1
    jq -e --arg repository "$GITHUB_REPOSITORY" --arg sha "$SOURCE_SHA" \
        --arg id "$SOURCE_RUN_ID" --arg attempt "$SOURCE_ATTEMPT" '
        .head_repository.full_name == $repository and .path == ".github/workflows/publish-hlg.yml" and
        .head_sha == $sha and .head_branch == "hlg" and .event == "push" and .conclusion == "success" and
        (.id | tostring) == $id and (.run_attempt | tostring) == $attempt
    ' <<< "$run" >/dev/null || return 1
    gh run download "$SOURCE_RUN_ID" --repo "$GITHUB_REPOSITORY" \
        --name "release-$SOURCE_VERSION-$SOURCE_ATTEMPT" --dir "$evidence_dir" || return 1
    jq -e --arg repository "$GITHUB_REPOSITORY" --arg sha "$SOURCE_SHA" --arg version "$SOURCE_VERSION" \
        --arg id "$SOURCE_RUN_ID" --arg attempt "$SOURCE_ATTEMPT" '
        .repository == $repository and .branch == "hlg" and .environment == "hlg" and
        .sha == $sha and .version == $version and .runId == $id and .attempt == $attempt and .result == "success"
    ' "$evidence_dir/release-evidence.json" >/dev/null || return 1
    record recoveredSha "$SOURCE_SHA" || return 1
    record recoveredVersion "$SOURCE_VERSION" || return 1
    record sourceRunId "$SOURCE_RUN_ID" || return 1
    record sourceAttempt "$SOURCE_ATTEMPT"
}
