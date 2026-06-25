FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

COPY ClaimsTriageWorkflow.sln .
COPY src/ src/
COPY tests/ tests/

RUN dotnet restore

# Run unit tests during the build — they're pure (no LLM) and serve as a
# gate: the image won't be built if any test fails.
RUN dotnet test --configuration Release --no-restore --verbosity minimal

RUN dotnet publish src/ClaimsTriageWorkflow/ClaimsTriageWorkflow.csproj \
    --configuration Release \
    --no-restore \
    --output /app

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app .

# The HITL gate reads from stdin; keep it open so the operator can type.
ENTRYPOINT ["dotnet", "ClaimsTriageWorkflow.dll"]
