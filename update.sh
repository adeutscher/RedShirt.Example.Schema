#!/bin/bash

script_dir="$(readlink -f "$(dirname "${0}")")" && cd "${script_dir}" || exit 1

dotnet run --project "./src/RedShirt.Example.Schema/RedShirt.Example.Schema.csproj" -- "$@"
