#!/usr/bin/env bash
#
# qa-worktree.sh — manage the QA loop's isolated git worktree.
#
# The QA loop works in a worktree (branch qa) so it never writes the same
# files as the dev loop (main checkout). See docs/reference/QA_LOOP.md.
#
# Usage:
#   scripts/qa-worktree.sh create     # create the worktree on branch qa
#   scripts/qa-worktree.sh status     # show worktree, branch, uncommitted doc work
#   scripts/qa-worktree.sh sync       # fast-forward qa to the main branch
#   scripts/qa-worktree.sh merge      # merge qa into the main branch, reset qa
#   scripts/qa-worktree.sh destroy    # remove the worktree (only when clean)
#
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
WT_PATH="$REPO_ROOT/.worktrees/qa"
QA_BRANCH="qa"
MAIN_BRANCH="$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD)"

cmd="${1:-status}"

in_main() {
  # run a git command from the main checkout (not the worktree)
  git -C "$REPO_ROOT" "$@"
}

case "$cmd" in
  create)
    if [ -e "$WT_PATH" ]; then
      echo "worktree already exists at $WT_PATH" >&2
      exit 1
    fi
    mkdir -p "$REPO_ROOT/.worktrees"
    if git -C "$REPO_ROOT" show-ref --verify --quiet "refs/heads/$QA_BRANCH"; then
      git -C "$REPO_ROOT" worktree add "$WT_PATH" "$QA_BRANCH"
      echo "reused existing branch $QA_BRANCH at $WT_PATH"
    else
      git -C "$REPO_ROOT" worktree add "$WT_PATH" -b "$QA_BRANCH"
      echo "created worktree at $WT_PATH on new branch $QA_BRANCH"
    fi
    # A worktree checks out a COMMIT, not the working tree. Warn if the shared
    # QA docs are not yet committed on the main branch (they'd be missing here).
    if [ -z "$(git -C "$REPO_ROOT" ls-files docs/qa/ | head -1)" ]; then
      echo "WARNING: docs/qa/ is not committed on the main branch — the worktree" >&2
      echo "         will start WITHOUT the QA finding docs. Commit them first:" >&2
      echo "           git add docs/qa/ && git commit -m 'docs(qa): add finding docs'" >&2
      echo "         (see docs/reference/QA_LOOP.md — Isolation model, prerequisite note)" >&2
    fi
    ;;

  status)
    echo "repo root:    $REPO_ROOT"
    echo "main branch:  $MAIN_BRANCH"
    echo "qa branch:    $QA_BRANCH"
    echo "worktree:     $WT_PATH"
    if [ ! -e "$WT_PATH" ]; then
      echo "(worktree not created — run: scripts/qa-worktree.sh create)"
      exit 0
    fi
    echo
    echo "--- worktree branch + ahead/behind vs main ---"
    git -C "$WT_PATH" fetch . 2>/dev/null || true
    git -C "$WT_PATH" rev-list --left-right --count "$MAIN_BRANCH...$QA_BRANCH" \
      | awk '{print "  main ahead: "$1"   qa ahead: "$2}'
    echo
    echo "--- uncommitted QA doc work (should only be docs/qa + PLAN.md) ---"
    git -C "$WT_PATH" status --short || true
    ;;

  sync)
    [ -e "$WT_PATH" ] || { echo "no worktree — run create first" >&2; exit 1; }
    # Rebase qa/ onto the main branch so QA reads the latest docs/qa + PLAN.md
    # with its own committed work replayed on top. Only safe when qa has no
    # uncommitted work; divergent qa commits are exactly the normal case.
    if [ -n "$(git -C "$WT_PATH" status --porcelain)" ]; then
      echo "worktree has uncommitted changes — commit them first (see QA_LOOP.md step 5)" >&2
      exit 1
    fi
    git -C "$WT_PATH" rebase "$MAIN_BRANCH"
    echo "qa rebased onto $MAIN_BRANCH"
    ;;

  merge)
    [ -e "$WT_PATH" ] || { echo "no worktree — run create first" >&2; exit 1; }
    if [ -n "$(git -C "$WT_PATH" status --porcelain)" ]; then
      echo "worktree has uncommitted changes — commit them first:" >&2
      echo "  git -C "$WT_PATH" add docs/qa/ PLAN.md && git -C $WT_PATH commit -m 'qa: <summary>'" >&2
      exit 1
    fi
    # Merge qa into the current (main) branch from the main checkout.
    if [ "$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD)" != "$MAIN_BRANCH" ]; then
      echo "main checkout is on '$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD)', not $MAIN_BRANCH — check out $MAIN_BRANCH first" >&2
      exit 1
    fi
    git -C "$REPO_ROOT" merge --no-ff "$QA_BRANCH" -m "Merge $QA_BRANCH (QA docs) into $MAIN_BRANCH"
    echo "merged $QA_BRANCH into $MAIN_BRANCH"
    # Reset qa to main so the next pass starts clean (no divergent commits pile up).
    git -C "$WT_PATH" merge --ff-only "$MAIN_BRANCH"
    echo "reset $QA_BRANCH to $MAIN_BRANCH"
    ;;

  destroy)
    [ -e "$WT_PATH" ] || { echo "no worktree to remove" >&2; exit 0; }
    if [ -n "$(git -C "$WT_PATH" status --porcelain)" ]; then
      echo "worktree is not clean — commit or discard changes first" >&2
      exit 1
    fi
    git -C "$REPO_ROOT" worktree remove "$WT_PATH"
    echo "removed worktree at $WT_PATH (branch $QA_BRANCH kept)"
    echo "to delete the branch too: git branch -D $QA_BRANCH"
    ;;

  *)
    echo "unknown command: $cmd" >&2
    echo "usage: scripts/qa-worktree.sh {create|status|sync|merge|destroy}" >&2
    exit 2
    ;;
esac
