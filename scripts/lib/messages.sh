#!/bin/bash
#
# Common message functions, following the colour conventions of the core-tools scripts:
#
#   Label: message
#
# Messages go to stderr so that stdout is left for the calling script's output. Colours are only used when stderr is
# a terminal and NO_COLOR is not set.
#
# Colours hold the escape character itself (not a printf escape sequence) so that they can also be passed to awk.

if [ -t 2 ] && [ -z "${NO_COLOR:-}" ]; then
  BLUE=$'\033[1;34m'
  GREEN=$'\033[1;32m'
  RED=$'\033[1;31m'
  YELLOW=$'\033[1;93m'
  NC=$'\033[0m' # No Color
else
  BLUE=""
  GREEN=""
  RED=""
  YELLOW=""
  NC=""
fi

error() {
  printf '%sError%s: %s\n' "${RED}" "${NC}" "$*" >&2
}

notice() {
  printf '%sNotice%s: %s\n' "${BLUE}" "${NC}" "$*" >&2
}

success() {
  printf '%sSuccess%s: %s\n' "${GREEN}" "${NC}" "$*" >&2
}

warning() {
  printf '%sWarning%s: %s\n' "${YELLOW}" "${NC}" "$*" >&2
}
