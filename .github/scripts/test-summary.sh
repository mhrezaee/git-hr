#!/bin/sh
# Prints a Markdown summary of one CI job: xUnit reports of both suites and, if present, Core coverage.
# Usage: test-summary.sh <platform name>     (POSIX sh + awk, so it runs on Windows, Linux and macOS runners)
set -u
results="${RESULTS_DIR:-TestResults}"

echo "## $1"
for suite in core app; do
  report="$results/$suite/$suite.md"
  case "$suite" in core) title="Core tests (unit + integration)" ;; *) title="App tests (headless UI)" ;; esac
  if [ -f "$report" ]; then
    echo "#### $title"
    grep -v '^### Test Results' "$report"
    echo
  else
    echo "#### $title"
    echo "No report (the suite did not run)."
    echo
  fi
done

coverage=$(ls "$results"/core/coverage.cobertura.*.xml 2>/dev/null | head -n 1)
if [ -n "$coverage" ]; then
  echo "#### GitHr.Core coverage"
  echo
  awk 'match($0, /<package name="GitHr\.Core" line-rate="[0-9.]+" branch-rate="[0-9.]+"/) {
         s = substr($0, RSTART, RLENGTH)
         split(s, parts, "\"")
         printf "%.1f%% lines, %.1f%% branches\n", parts[4] * 100, parts[6] * 100
         exit
       }' "$coverage"
fi
