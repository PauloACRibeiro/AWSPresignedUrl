# S3PresignedURL.cs – Detailed Documentation

## Module Overview
`S3PresignedURL.cs` provides an OutSystems-compatible integration layer that exposes pre-signed URL generation and high-volume streaming/multipart transfer utilities for Amazon S3. The file defines the publicly exposed interface (`IPreSigner`), corresponding data structures, and a concrete implementation (`PreSignerImpl`) that orchestrates HTTP interactions between OutSystems REST endpoints and AWS S3.

## Data Structures
### `S3AuthInfo`
- **Goal:** Package AWS credentials and region in a single value object.
- **Functionality:** Supplies the access key, secret, and region required to materialise an `AmazonS3Client`.
- **Pattern:** *Value Object* – immutable-like container that travels across layers without behaviour.

### `DownloadToRestResult`
- **Goal:** Report the outcome of streaming an object from S3 into an OutSystems REST endpoint.
- **Functionality:** Captures the REST API response metadata: inserted record identifier, success flag, and any error message.
- **Pattern:** *Result DTO* – simple response envelope that decouples transport concerns from business logic.

### `UploadToS3Result`
- **Goal:** Describe the result of uploading data from OutSystems REST into S3 via multipart upload.
- **Functionality:** Echoes the originating binary GUID together with success status and error information.
- **Pattern:** *Result DTO*, mirroring the download result contract for symmetry.

## Interface `IPreSigner`
- **Goal:** OutSystems-facing API with actions for creating pre-signed URLs and transferring data between REST and S3.
- **Pattern:** *Gateway Interface* – abstracts S3-specific operations behind an interface that the OutSystems runtime can invoke.

## Implementation `PreSignerImpl`
`PreSignerImpl` realises `IPreSigner` using AWS SDK primitives plus `HttpClient`-based streaming. Each public method acts as an orchestration unit that combines validation, HTTP I/O, and AWS interactions.

### `GetObjectPreSignedUrl(S3AuthInfo authInfo, string bucketName, string key, int durationInMinutes)`
- **Goal:** Generate a temporary download link for an existing S3 object.
- **Functionality:** Validates input, instantiates an `AmazonS3Client`, builds a `GetPreSignedUrlRequest` configured for `GET`, and delegates URL creation to the AWS SDK.
- **Pattern:** *Gateway / Facade* – wraps low-level AWS SDK operations behind a single method tailored to the consumer.

### `PutObjectPreSignedUrl(S3AuthInfo authInfo, string bucketName, string key, string contentType, int durationInMinutes)`
- **Goal:** Produce a time-bound upload link that accepts a single-part `PUT` request.
- **Functionality:** Shares the validation and client-creation workflow with the `GET` variant, but configures the request for `PUT` and optionally locks the expected `Content-Type`.
- **Pattern:** *Gateway / Facade* – provides a simplified, intention-revealing façade over the AWS SDK for upload pre-signing.

### `UploadFromRestToPresignedUrl(...)`
- **Goal:** Stream a binary exposed by an OutSystems REST endpoint directly into S3 through a pre-signed single-part `PUT`.
- **Functionality:**
  1. Validates required arguments and builds an `HttpClient` with the caller-specified timeout.
  2. Fetches the binary via `GET`, propagating authentication headers and streaming the response.
  3. Attempts to honour the upstream `Content-Length`; when absent, buffers into memory to obtain the length because single-part pre-signed uploads require a fixed size.
  4. Issues the `PUT` to the pre-signed URL without `Expect: 100-continue`, then returns the S3 `ETag`.
- **Pattern:** *Streaming Adapter* – adapts one streaming HTTP interface (OutSystems REST) to another (S3 pre-signed PUT) while reconciling protocol requirements such as content length.

### `DownloadFromPresignedUrlToRest(...)`
- **Goal:** Pull a large object from S3 (pre-signed `GET`) and push it into an OutSystems REST API in manageable chunks to bypass gateway size limits.
- **Functionality:**
  1. Validates inputs, normalises chunk size/timeout defaults, and prepares the `HttpClient`.
  2. Calls `TryGetContentLength` to ensure the total object size is known up front, required for consistent chunk headers.
  3. Streams the S3 response, slicing buffers from the shared array pool.
  4. For each chunk, constructs an authenticated `POST` with metadata headers (`X-Upload-Id`, `X-Chunk-*`) that allow the REST target to reconstruct the file.
  5. Implements limited retries for transient HTTP status codes and inspects the final response for success flags or errors, parsing response bodies to extract the resulting `binGuid`.
- **Pattern:** *Resilient Streaming Pipeline* – combines chunked transfer orchestration with retry logic, embodying elements of the *Pipes and Filters* pattern (chunk processing) and *Retry* pattern for transient faults.

### `UploadFromRestToS3Multipart(...)`
- **Goal:** Move large binaries from OutSystems REST into S3 by orchestrating an AWS multipart upload, overcoming the 5 MB single-part limit while keeping memory usage bounded.
- **Functionality:**
  1. Validates supplied parameters, ensuring chunk sizes meet S3 multipart requirements.
  2. Discovers the total source length via `TryProbeLength` (HEAD or single-byte probe) to pre-compute part boundaries.
  3. Initiates a multipart upload and iteratively:
     - Requests byte ranges from the source REST endpoint (`offset`/`length` query pattern).
     - Reads exactly the expected byte count into a rented buffer.
     - Uploads each slice to S3 using `UploadPartAsync`, storing returned `ETag`s.
  4. Completes the multipart upload once every part succeeds, or aborts and reports the error if any stage fails.
- **Pattern:** *Orchestrated Multipart Transfer* – functions as a *Process Manager* overseeing multiple dependent HTTP calls and S3 operations, coordinating state (`uploadId`, part list) until completion or compensating by aborting on failure.

## Helper Methods
### `TryGetContentLength(HttpClient http, string url)`
- **Goal:** Determine the total length of a remote resource that may not expose a straightforward `Content-Length`.
- **Functionality:** Attempts a `HEAD` request, falls back to a ranged `GET`, and inspects both content and header metadata to infer the total size.
- **Pattern:** *Robust Discovery / Tolerant Reader* – probes multiple representations to obtain required metadata while tolerating partial responses.

### `TryProbeLength(HttpClient http, string sourceUrl, string binGuid, string authHeaderName, string authHeaderValue)`
- **Goal:** Derive the full size of the source binary exposed by OutSystems REST.
- **Functionality:** Issues a `HEAD` request when available or a minimal ranged `GET`, checking headers such as `X-Total-Length` and `Content-Range` to determine total bytes.
- **Pattern:** *Tolerant Reader* – accepts various header conventions to maximise compatibility with upstream services.

### `ReadFull(Stream s, byte[] buffer, int offset, int count)`
- **Goal:** Fill the buffer with an exact number of bytes or detect end-of-stream.
- **Functionality:** Repeatedly reads from the stream until the requested count is satisfied or no more data is available.
- **Pattern:** *Utility / Guarded Read* – defensive helper that guarantees full-buffer semantics required by multipart uploads.

### `GetHeaderValue(HttpResponseMessage resp, string headerName)`
- **Goal:** Uniformly access headers that might live in either the main response or content headers collection.
- **Functionality:** Checks both header bags in priority order, returning the first match.
- **Pattern:** *Adapter* – harmonises differing header storage models.

### `ParseBool(string value)`
- **Goal:** Interpret varied truthy string representations.
- **Functionality:** Accepts common variations (`true`, `1`, `yes`) in a case-insensitive manner.
- **Pattern:** *Normalization Helper* – simplifies tolerance for multiple input formats.

### `ExtractBinGuidFromBody(string body)`
- **Goal:** Derive a binary GUID identifier from REST responses that may return JSON or plain text.
- **Functionality:** Strips quotes, optionally parses JSON, and looks for canonical or lowercase property names.
- **Pattern:** *Tolerant Reader* – gracefully handles schema variations without strict coupling.

### `AppendQueryParameter(string url, string name, string value)`
- **Goal:** Append query parameters safely without duplicating logic across the class.
- **Functionality:** Detects existing query strings, encodes name/value pairs, and returns the augmented URL.
- **Pattern:** *Builder Helper* – encapsulates query composition with guard clauses.

### `Validate(S3AuthInfo auth, string bucket, string key, int mins)`
- **Goal:** Enforce preconditions for pre-signing operations.
- **Functionality:** Checks all required fields and guards the duration range prior to AWS SDK invocations.
- **Pattern:** *Guard Clauses* – consolidates argument validation so public methods stay focused on orchestration.

### `CreateClient(S3AuthInfo auth)`
- **Goal:** Instantiate an S3 client configured for the caller’s region.
- **Functionality:** Resolves the AWS region and returns a credentialled `AmazonS3Client`.
- **Pattern:** *Factory Method* – centralises client creation to promote reuse and consistent configuration.

## Architectural Themes
- The class acts as a **service gateway** bridging OutSystems and AWS S3, concentrating external communication concerns.
- Streaming operations employ **buffer pooling** and **chunk management** to maintain performance under size constraints.
- Error handling follows **defensive programming** patterns: retries for transient HTTP faults, tolerant header parsing, and compensating transactions (abort multipart) on failure.
- Helper methods encapsulate cross-cutting responsibilities (validation, probing, query composition) to keep orchestration code readable and consistent.

