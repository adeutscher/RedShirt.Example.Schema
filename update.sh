#!/bin/bash

cd "$(readlink -f "$(dirname "${0}")")"

dotnet run --project "./src/RedShirt.Example.Schema/RedShirt.Example.Schema.csproj"
