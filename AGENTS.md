# AGENTS.md

This repository contains an OutSystems ODC External Library implementation in `.NET 8` plus OML examples used as reference artifacts.

## Cloud Knowledge Routing

- Use the hosted `workspace-knowledge` MCP for shared OutSystems public/internal guidance and support assets.
- Do not assume the umbrella workspace root is mounted in cloud tasks.
- `knowledge/private/personal` is local-only and must not be used in cloud workflows.

## Repository Scope

- Primary implementation project: `AWSS3PreSignedUploader/AWSS3PreSignedUploader/AWSS3PreSignedUploader.csproj`
- Reference artifacts: `S3 PreSigned File Upload Helper PS ODC Portal/`
- Repo-local docs describe this implementation. For platform behavior, prefer shared OutSystems knowledge over local notes.

## Build And Packaging

- Build:
  - `dotnet build AWSS3PreSignedUploader/AWSS3PreSignedUploader/AWSS3PreSignedUploader.csproj -c Release`
- Publish:
  - `dotnet publish AWSS3PreSignedUploader/AWSS3PreSignedUploader/AWSS3PreSignedUploader.csproj -c Release -o publish`
- Package for ODC upload:
  - `../workspace-agent-tools/scripts/publish_and_package.sh AWSS3PreSignedUploader/AWSS3PreSignedUploader/AWSS3PreSignedUploader.csproj --no-bump`

If this repo runs as a standalone cloud task, vendor the packaging script under repo `tools/` or install `workspace-agent-tools` during setup.

## Change Boundaries

- Keep exposed OutSystems action and structure names stable unless the task explicitly requires a breaking interface change.
- Treat the OML files as reference/demo assets unless the user explicitly asks to update them.
