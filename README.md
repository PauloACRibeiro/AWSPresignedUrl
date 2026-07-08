# AWSPresignedUrl

`AWSPresignedUrl` is an OutSystems ODC External Library built with `.NET 8` that generates AWS S3 pre-signed URLs for runtime file transfers.

The current implementation exposes two actions:

- `GetObjectPreSignedUrl` for time-limited download URLs
- `PutObjectPreSignedUrl` for time-limited upload URLs

This repository also includes OML reference artifacts and packaged outputs that were used to validate or distribute the library.

## Repository Layout

- `AWSS3PreSignedUploader/AWSS3PreSignedUploader/`
  The .NET external library project.
- `AWSS3PreSignedUploader/S3PresignedURL_Documentation.md`
  Detailed implementation notes for the current C# code.
- `S3 PreSigned File Upload Helper PS ODC Portal/`
  Reference OML artifacts from the Portal and Forge variants.
- `ODCPortal/`
  Previously generated package archives for upload/distribution.

## Exposed OutSystems Actions

### `GetObjectPreSignedUrl`

Creates a pre-signed `GET` URL for an object that already exists in S3.

Inputs:

- AWS access key id
- AWS secret access key
- AWS region
- S3 bucket name
- S3 object key
- Expiration in minutes

### `PutObjectPreSignedUrl`

Creates a pre-signed `PUT` URL for uploading an object to S3.

Inputs:

- AWS access key id
- AWS secret access key
- AWS region
- S3 bucket name
- S3 object key
- Content type
- Expiration in minutes

## Build

```bash
dotnet build AWSS3PreSignedUploader/AWSS3PreSignedUploader/AWSS3PreSignedUploader.csproj -c Release
```

## Publish

```bash
dotnet publish AWSS3PreSignedUploader/AWSS3PreSignedUploader/AWSS3PreSignedUploader.csproj -c Release -o publish
```

## Package For ODC Upload

```bash
../workspace-agent-tools/scripts/publish_and_package.sh AWSS3PreSignedUploader/AWSS3PreSignedUploader/AWSS3PreSignedUploader.csproj --no-bump
```

If this repository is used outside the umbrella workspace, copy that packaging helper into a local `tools/` folder or install `workspace-agent-tools` first.

## Current Scope

The code in `S3PresignedURL.cs` currently covers pre-signed URL generation only. Earlier or adjacent documentation in this repo may reference broader streaming or multipart transfer flows, but those actions are not part of the active `IPreSigner` interface in this branch.

## License

This project is licensed under `GPL-3.0`. See [LICENSE](LICENSE).
