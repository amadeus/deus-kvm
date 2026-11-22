#!/usr/bin/env bash

which xcodegen || brew install xcodegen

cd "${CI_PRIMARY_REPOSITORY_PATH:-$(cd "$(dirname "$0")/.." && pwd)}"
xcodegen generate
