using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using OutSystems.ExternalLibraries.SDK;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

//
// -------- Version 1.3.0 --------
// - Keeps UploadFromRestToPresignedUrl (single-part PUT) for small/medium files
// - Adds UploadFromRestToS3Multipart (ODC REST -> S3 multipart) for large files
// - Restores DownloadFromPresignedUrlToRest (S3 -> ODC REST, chunked) with:
//     A) Always send X-Chunk-Total (length discovered via HEAD or 1-byte ranged GET)
//     B) Force a single Content-Type for all chunks
//

namespace AWSS3PreSignedUploader
{
  // -------- Data structures --------
  [OSStructure(Description = "AWS S3 credentials and region")]
  public struct S3AuthInfo
  {
    [OSStructureField(Description = "AWS Access Key Id")]
    public string AccessKeyId { get; set; }

    [OSStructureField(Description = "AWS Secret Access Key")]
    public string SecretAccessKey { get; set; }

    [OSStructureField(Description = "Region name, e.g., eu-central-1")]
    public string Region { get; set; }
  }

  [OSStructure(Description = "Result of downloading from S3 into ODC REST")]
  public struct DownloadToRestResult
  {
    [OSStructureField(Description = "ID of the inserted DB record returned by the ODC REST")]
    public string BinGuid { get; set; }

    [OSStructureField(Description = "True if the ODC REST reported success")]
    public bool Success { get; set; }

    [OSStructureField(Description = "Error message from the ODC REST (if any)")]
    public string ErrorMessage { get; set; }
  }

  // -------- Interface (icon in resources folder) --------
  [OSInterface(
      Name = "AWSS3PreSignedUploader",
      IconResourceName = "AWSS3PreSignedUploader.resources.AWSS3PresignedUploader_lib.png",
      Description = "Generate pre-signed URLs for S3 GET/PUT and stream files between ODC REST and S3"
  )]
  public interface IPreSigner
  {
    [OSAction(Description = "Create a pre-signed GET URL for an S3 object")]
    string GetObjectPreSignedUrl(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Bucket name")] string bucketName,
      [OSParameter(Description = "Object key")] string key,
      [OSParameter(Description = "Duration in minutes")] int durationInMinutes);

    [OSAction(Description = "Create a pre-signed PUT URL for an S3 object")]
    string PutObjectPreSignedUrl(
      [OSParameter(Description = "Auth info")] S3AuthInfo authInfo,
      [OSParameter(Description = "Bucket name")] string bucketName,
      [OSParameter(Description = "Object key")] string key,
      [OSParameter(Description = "Content-Type for the upload")] string contentType,
      [OSParameter(Description = "Duration in minutes")] int durationInMinutes);

    // Existing: single GET (ODC) -> single PUT (S3). Suitable while source responses stay under the platform cap.
    [OSAction(Description = "Upload to S3 using a pre-signed single-part PUT by streaming a binary from an ODC REST Source URL")]
    string UploadFromRestToPresignedUrl(
      [OSParameter(Description = "Source REST URL in the ODC app")] string sourceUrl,
      [OSParameter(Description = "GUID identifying the binary in the ODC REST")] string binGuid,
      [OSParameter(Description = "Auth header name for Source (e.g., Authorization)")] string authHeaderName,
      [OSParameter(Description = "Auth header value for Source (e.g., Bearer <token>)")] string authHeaderValue,
      [OSParameter(Description = "Pre-signed S3 PUT URL (single-part)")] string presignedPutUrl,
      [OSParameter(Description = "Content-Type to enforce on PUT (must match the presign)")] string contentType,
      [OSParameter(Description = "Timeout in seconds (default 300)")] int timeoutSeconds);

    // Restored: S3 -> ODC REST (chunked; large files), always sends X-Chunk-Total and fixed Content-Type per chunk.
    [OSAction(Description = "Download from S3 (pre-signed GET) and POST in parts (chunks) to an ODC REST target to bypass 30MB limit")]
    DownloadToRestResult DownloadFromPresignedUrlToRest(
      [OSParameter(Description = "Pre-signed S3 GET URL")] string presignedGetUrl,
      [OSParameter(Description = "Target ODC REST base URL (receives binary via POST)")] string targetUrl,
      [OSParameter(Description = "S3 object Key to append as URL parameter ?Key=<key>")] string s3ObjectKey,
      [OSParameter(Description = "Auth header name for the target (e.g., Authorization)")] string targetAuthHeaderName,
      [OSParameter(Description = "Auth header value for the target")] string targetAuthHeaderValue,
      [OSParameter(Description = "Content-Type to send (fixed for all chunks; default application/octet-stream)")] string targetContentType,
      [OSParameter(Description = "Chunk size in bytes (default 25,000,000 ≈ 25 MB)")] int chunkSizeBytes,
      [OSParameter(Description = "Timeout per chunk request in seconds (default 120)")] int timeoutSeconds);

    // NEW: Large files – source is fetched in many small responses; target uses S3 Multipart Upload (no presigned URL).
    [OSAction(Description = "Upload a large binary from ODC REST to S3 using MULTIPART (pull source in chunks)")]
    string UploadFromRestToS3Multipart(
      [OSParameter(Description = "Auth info for S3 (AccessKey/Secret/Region)")] S3AuthInfo authInfo,
      [OSParameter(Description = "S3 bucket name")] string bucketName,
      [OSParameter(Description = "S3 object key to create")] string key,
      [OSParameter(Description = "Final Content-Type of the object (e.g., application/pdf)")] string contentType,
      [OSParameter(Description = "Source REST base URL in the ODC app")] string sourceUrl,
      [OSParameter(Description = "GUID identifying the binary in the ODC REST")] string binGuid,
      [OSParameter(Description = "Auth header name for Source (e.g., Authorization)")] string authHeaderName,
      [OSParameter(Description = "Auth header value for Source (e.g., Bearer <token>)")] string authHeaderValue,
      [OSParameter(Description = "Chunk size in bytes (min 5MB for S3 multipart; default 8MB)")] int chunkSizeBytes,
      [OSParameter(Description = "Timeout per request in seconds (default 120)")] int timeoutSeconds);
  }

  // -------- Implementation --------
  public class PreSignerImpl : IPreSigner
  {
    public PreSignerImpl() { }

    public string GetObjectPreSignedUrl(S3AuthInfo authInfo, string bucketName, string key, int durationInMinutes)
    {
      Validate(authInfo, bucketName, key, durationInMinutes);
      using var s3 = CreateClient(authInfo);
      var req = new GetPreSignedUrlRequest {
        BucketName = bucketName,
        Key = key,
        Verb = HttpVerb.GET,
        Expires = DateTime.UtcNow.AddMinutes(durationInMinutes)
      };
      return s3.GetPreSignedURL(req);
    }

    public string PutObjectPreSignedUrl(S3AuthInfo authInfo, string bucketName, string key, string contentType, int durationInMinutes)
    {
      Validate(authInfo, bucketName, key, durationInMinutes);
      using var s3 = CreateClient(authInfo);
      var req = new GetPreSignedUrlRequest {
        BucketName = bucketName,
        Key = key,
        Verb = HttpVerb.PUT,
        Expires = DateTime.UtcNow.AddMinutes(durationInMinutes),
        ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType
      };
      return s3.GetPreSignedURL(req);
    }

    // --- Upload ODC REST -> S3 (single PUT) --- Keep for smaller files ---
    public string UploadFromRestToPresignedUrl(
      string sourceUrl,
      string binGuid,
      string authHeaderName,
      string authHeaderValue,
      string presignedPutUrl,
      string contentType,
      int timeoutSeconds)
    {
      if (string.IsNullOrWhiteSpace(sourceUrl))        throw new ArgumentException("sourceUrl is required.");
      if (string.IsNullOrWhiteSpace(binGuid))          throw new ArgumentException("binGuid is required.");
      if (string.IsNullOrWhiteSpace(presignedPutUrl))  throw new ArgumentException("presignedPutUrl is required.");
      if (timeoutSeconds <= 0) timeoutSeconds = 300;

      using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
      { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

      var resolvedSourceUrl = AppendQueryParameter(sourceUrl, "binGuid", binGuid);
      using var getReq = new HttpRequestMessage(HttpMethod.Get, resolvedSourceUrl);
      if (!string.IsNullOrWhiteSpace(authHeaderName) && !string.IsNullOrWhiteSpace(authHeaderValue))
        getReq.Headers.TryAddWithoutValidation(authHeaderName, authHeaderValue);

      using var getResp = http.Send(getReq, HttpCompletionOption.ResponseHeadersRead);
      getResp.EnsureSuccessStatusCode();

      var srcStream = getResp.Content.ReadAsStream();
      var upstreamLength = getResp.Content.Headers.ContentLength;

      HttpContent putContent;
      if (upstreamLength.HasValue)
      {
        putContent = new StreamContent(srcStream);
        putContent.Headers.ContentLength = upstreamLength.Value;
      }
      else
      {
        // When Content-Length is missing, we must buffer to set Content-Length (presigned PUT does NOT accept unsigned chunked encoding)
        var buffered = new MemoryStream();
        srcStream.CopyTo(buffered);
        buffered.Position = 0;
        srcStream.Dispose();
        putContent = new StreamContent(buffered);
        putContent.Headers.ContentLength = buffered.Length;
      }

      using var putReq = new HttpRequestMessage(HttpMethod.Put, presignedPutUrl) { Content = putContent };
      if (!string.IsNullOrWhiteSpace(contentType))
        putReq.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
      putReq.Headers.ExpectContinue = false;

      using var putResp = http.Send(putReq, HttpCompletionOption.ResponseHeadersRead);
      putResp.EnsureSuccessStatusCode();

      var etag = putResp.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;
      return etag;
    }
    private static readonly HttpStatusCode[] TransientStatus =
      { HttpStatusCode.Forbidden, HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout };

    // --- S3 -> ODC REST (chunked; large files) ---
    public DownloadToRestResult DownloadFromPresignedUrlToRest(
      string presignedGetUrl,
      string targetUrl,
      string s3ObjectKey,
      string targetAuthHeaderName,
      string targetAuthHeaderValue,
      string targetContentType,
      int chunkSizeBytes,
      int timeoutSeconds)
    {
      if (string.IsNullOrWhiteSpace(presignedGetUrl)) throw new ArgumentException("presignedGetUrl is required.");
      if (string.IsNullOrWhiteSpace(targetUrl))       throw new ArgumentException("targetUrl is required.");
      if (string.IsNullOrWhiteSpace(s3ObjectKey))     throw new ArgumentException("s3ObjectKey is required.");

      // Keep headroom under the ~30MB gateway limit
      const int maxChunkSize = 25_000_000;
      if (chunkSizeBytes <= 0 || chunkSizeBytes > maxChunkSize) chunkSizeBytes = maxChunkSize; // default to 25 MB
      if (timeoutSeconds <= 0) timeoutSeconds = 120;

      using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
      { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

      // A) Determine total length up-front so we ALWAYS send X-Chunk-Total
      var contentLength = TryGetContentLength(http, presignedGetUrl);
      if (!contentLength.HasValue)
        throw new InvalidOperationException("Cannot determine source length (HEAD and ranged GET both failed).");

      int totalChunks = (int)((contentLength.Value + (chunkSizeBytes - 1)) / chunkSizeBytes);

      // Open streaming GET from S3
      using var getReq = new HttpRequestMessage(HttpMethod.Get, presignedGetUrl);
      using var getResp = http.Send(getReq, HttpCompletionOption.ResponseHeadersRead);
      getResp.EnsureSuccessStatusCode();

      var srcStream = getResp.Content.ReadAsStream();

      // B) Force a single content-type for all chunks
      var forcedContentType = string.IsNullOrWhiteSpace(targetContentType)
        ? "application/octet-stream"
        : targetContentType;

      var targetWithKey = AppendQueryParameter(targetUrl, "Key", s3ObjectKey);

      var uploadId = Guid.NewGuid().ToString("N");
      var buffer   = ArrayPool<byte>.Shared.Rent(chunkSizeBytes);
      try
      {
        long totalRead = 0;
        int  index     = 0;

        DownloadToRestResult finalResult = new DownloadToRestResult { BinGuid = "", Success = false, ErrorMessage = "" };

        for (;; index++)
        {
          int read = ReadFull(srcStream, buffer, 0, chunkSizeBytes);
          if (read <= 0)
          {
            if (index == 0)
              return new DownloadToRestResult { BinGuid = "", Success = false, ErrorMessage = "Source stream is empty." };
            break;
          }

          totalRead += read;
          bool isLast = (index == totalChunks - 1) || (totalRead >= contentLength.Value);

          DownloadToRestResult? attemptResult = null;

          const int maxAttempts = 3;
          for (int attempt = 1; attempt <= maxAttempts; attempt++)
          {
            using var content = new ByteArrayContent(buffer, 0, read);
            content.Headers.ContentType   = new MediaTypeHeaderValue(forcedContentType);
            content.Headers.ContentLength = read;

            using var postReq = new HttpRequestMessage(HttpMethod.Post, targetWithKey) { Content = content };

            // Chunk headers for assembly (server is 0-based)
            postReq.Headers.TryAddWithoutValidation("X-Upload-Id",       uploadId);
            postReq.Headers.TryAddWithoutValidation("X-Chunk-Index",     index.ToString());
            postReq.Headers.TryAddWithoutValidation("X-Chunk-Index-Base","0");
            postReq.Headers.TryAddWithoutValidation("X-Chunk-Total",     totalChunks.ToString());
            postReq.Headers.TryAddWithoutValidation("X-Last-Chunk",      isLast ? "true" : "false");

            if (!string.IsNullOrWhiteSpace(targetAuthHeaderName) && !string.IsNullOrWhiteSpace(targetAuthHeaderValue))
              postReq.Headers.TryAddWithoutValidation(targetAuthHeaderName, targetAuthHeaderValue);

            postReq.Headers.ExpectContinue = false;

            using var postResp = http.Send(postReq, HttpCompletionOption.ResponseHeadersRead);

            if (!postResp.IsSuccessStatusCode && TransientStatus.Contains(postResp.StatusCode) && attempt < maxAttempts)
            {
              System.Threading.Thread.Sleep(200 * attempt);
              continue;
            }

            if (isLast)
            {
              var successHeader = GetHeaderValue(postResp, "success");
              var errorHeader   = GetHeaderValue(postResp, "errorMessage");
              var body          = postResp.Content != null ? postResp.Content.ReadAsStringAsync().Result : string.Empty;
              var binGuid       = ExtractBinGuidFromBody(body);

              bool success      = ParseBool(successHeader ?? string.Empty) && postResp.IsSuccessStatusCode;
              string errorMsg   = errorHeader ?? (success ? "" : $"HTTP {(int)postResp.StatusCode} {postResp.ReasonPhrase}");

              attemptResult = new DownloadToRestResult { BinGuid = binGuid, Success = success, ErrorMessage = errorMsg };
            }
            else
            {
              if (!postResp.IsSuccessStatusCode)
              {
                var msg = postResp.Content != null ? postResp.Content.ReadAsStringAsync().Result : $"HTTP {(int)postResp.StatusCode} {postResp.ReasonPhrase}";
                attemptResult = new DownloadToRestResult { BinGuid = "", Success = false, ErrorMessage = msg };
              }
              else
              {
                attemptResult = new DownloadToRestResult { BinGuid = "", Success = true, ErrorMessage = "" };
              }
            }

            break;
          }

          if (attemptResult == null)
            return new DownloadToRestResult { BinGuid = "", Success = false, ErrorMessage = "Unknown error sending chunk." };

          if (!attemptResult.Value.Success)
            return attemptResult.Value;

          if (isLast)
          {
            finalResult = attemptResult.Value;
            return finalResult;
          }
        }

        return new DownloadToRestResult { BinGuid = "", Success = false, ErrorMessage = "Unexpected end of stream without final chunk." };
      }
      finally
      {
        ArrayPool<byte>.Shared.Return(buffer);
      }
    }

    // ---------- NEW: ODC REST (chunked GETs) -> S3 MULTIPART ----------
    public string UploadFromRestToS3Multipart(
      S3AuthInfo authInfo,
      string bucketName,
      string key,
      string contentType,
      string sourceUrl,
      string binGuid,
      string authHeaderName,
      string authHeaderValue,
      int chunkSizeBytes,
      int timeoutSeconds)
    {
      if (string.IsNullOrWhiteSpace(bucketName))  throw new ArgumentException("bucketName is required.");
      if (string.IsNullOrWhiteSpace(key))         throw new ArgumentException("key is required.");
      if (string.IsNullOrWhiteSpace(sourceUrl))   throw new ArgumentException("sourceUrl is required.");
      if (string.IsNullOrWhiteSpace(binGuid))     throw new ArgumentException("binGuid is required.");
      if (chunkSizeBytes < 5 * 1024 * 1024)       chunkSizeBytes = 8 * 1024 * 1024; // S3 requires >= 5MB per part (except last)
      if (timeoutSeconds <= 0)                    timeoutSeconds = 120;

      using var s3   = CreateClient(authInfo);
      using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
      { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

      // Discover total size (so we know how many parts)
      var length = TryProbeLength(http, sourceUrl, binGuid, authHeaderName, authHeaderValue);
      if (!length.HasValue)
        throw new InvalidOperationException("Source endpoint must provide total length (HEAD or 1-byte probe failed).");

      long totalLength = length.Value;
      int  totalParts  = (int)((totalLength + (long)chunkSizeBytes - 1) / chunkSizeBytes);

      // Initiate multipart
      var initiate = s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest {
        BucketName  = bucketName,
        Key         = key,
        ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
      }).GetAwaiter().GetResult();

      string uploadId = initiate.UploadId;
      var partETags   = new System.Collections.Generic.List<PartETag>(capacity: totalParts);

      // Loop parts
      var buffer = ArrayPool<byte>.Shared.Rent(chunkSizeBytes);
      try
      {
        long offset = 0;
        for (int partNumber = 1; partNumber <= totalParts; partNumber++)
        {
          int thisLen = (int)Math.Min(chunkSizeBytes, totalLength - offset);

          // GET slice from ODC REST (?binGuid=&offset=&length=)
          var sliceUrl = AppendQueryParameter(
                           AppendQueryParameter(
                             AppendQueryParameter(sourceUrl, "binGuid", binGuid),
                             "offset", offset.ToString()),
                           "length", thisLen.ToString());

          using var getReq = new HttpRequestMessage(HttpMethod.Get, sliceUrl);
          if (!string.IsNullOrWhiteSpace(authHeaderName) && !string.IsNullOrWhiteSpace(authHeaderValue))
            getReq.Headers.TryAddWithoutValidation(authHeaderName, authHeaderValue);

          using var getResp = http.Send(getReq, HttpCompletionOption.ResponseHeadersRead);
          getResp.EnsureSuccessStatusCode();

          using var src = getResp.Content.ReadAsStream();
          // Read exactly thisLen bytes
          int read = 0;
          while (read < thisLen)
          {
            int n = src.Read(buffer, read, thisLen - read);
            if (n <= 0) throw new EndOfStreamException($"Unexpected EOF at offset {offset}, expected {thisLen - read} more bytes.");
            read += n;
          }

          // Upload part
          using var ms = new MemoryStream(buffer, 0, read, writable: false);
          var uploadPartResponse = s3.UploadPartAsync(new UploadPartRequest {
            BucketName   = bucketName,
            Key          = key,
            UploadId     = uploadId,
            PartNumber   = partNumber,
            PartSize     = read,
            InputStream  = ms
          }).GetAwaiter().GetResult();

          partETags.Add(new PartETag(partNumber, uploadPartResponse.ETag));
          offset += read;
        }

        // Complete multipart
        var complete = new CompleteMultipartUploadRequest {
          BucketName = bucketName,
          Key        = key,
          UploadId   = uploadId
        };
        complete.AddPartETags(partETags);
        var completed = s3.CompleteMultipartUploadAsync(complete).GetAwaiter().GetResult();

        return completed.ETag?.Trim('\"') ?? string.Empty;
      }
      catch
      {
        try { s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucketName, Key = key, UploadId = uploadId }).GetAwaiter().GetResult(); }
        catch { /* best effort */ }
        throw;
      }
      finally
      {
        ArrayPool<byte>.Shared.Return(buffer);
      }
    }

    // ===== Helpers =====

    // Try to obtain Content-Length via HEAD; if missing, use 1-byte ranged GET and parse Content-Range.
    private static long? TryGetContentLength(HttpClient http, string url)
    {
      // Attempt HEAD
      try
      {
        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        using var resp = http.Send(head);
        if (resp.IsSuccessStatusCode)
        {
          var len = resp.Content.Headers.ContentLength;
          if (len.HasValue) return len.Value;
        }
      }
      catch { /* ignore and fall back */ }

      // Fallback: GET with Range: bytes=0-0 to get Content-Range: bytes 0-0/12345
      try
      {
        using var ranged = new HttpRequestMessage(HttpMethod.Get, url);
        ranged.Headers.Range = new RangeHeaderValue(0, 0);
        using var resp = http.Send(ranged, HttpCompletionOption.ResponseHeadersRead);
        if ((int)resp.StatusCode == 206) // Partial Content
        {
          var cr = resp.Content.Headers.ContentRange; // may be null depending on handler; handle manually if needed
          if (cr != null && cr.Length.HasValue) return cr.Length.Value;

          // Manual parse as a fallback
          if (resp.Content.Headers.TryGetValues("Content-Range", out var values))
          {
            var val = values.FirstOrDefault(); // e.g., "bytes 0-0/12345"
            if (!string.IsNullOrEmpty(val))
            {
              var slash = val.LastIndexOf('/');
              if (slash > 0 && long.TryParse(val.Substring(slash + 1), out var total))
                return total;
            }
          }
        }
      }
      catch { /* ignore */ }

      return null;
    }

    private static long? TryProbeLength(HttpClient http, string sourceUrl, string binGuid, string authHeaderName, string authHeaderValue)
    {
      // HEAD first
      try
      {
        var headUrl = AppendQueryParameter(sourceUrl, "binGuid", binGuid);
        using var head = new HttpRequestMessage(HttpMethod.Head, headUrl);
        if (!string.IsNullOrWhiteSpace(authHeaderName) && !string.IsNullOrWhiteSpace(authHeaderValue))
          head.Headers.TryAddWithoutValidation(authHeaderName, authHeaderValue);

        using var resp = http.Send(head);
        if (resp.IsSuccessStatusCode)
        {
          var len = resp.Content.Headers.ContentLength;
          if (len.HasValue) return len.Value;
        }
      }
      catch { }

      // Fallback 1-byte probe (?offset=0&length=1) with optional X-Total-Length in response headers
      try
      {
        var probeUrl = AppendQueryParameter(
                         AppendQueryParameter(
                           AppendQueryParameter(sourceUrl, "binGuid", binGuid),
                           "offset", "0"),
                         "length", "1");

        using var get = new HttpRequestMessage(HttpMethod.Get, probeUrl);
        if (!string.IsNullOrWhiteSpace(authHeaderName) && !string.IsNullOrWhiteSpace(authHeaderValue))
          get.Headers.TryAddWithoutValidation(authHeaderName, authHeaderValue);

        using var resp = http.Send(get, HttpCompletionOption.ResponseHeadersRead);
        if (resp.IsSuccessStatusCode)
        {
          if (resp.Headers.TryGetValues("X-Total-Length", out var vals))
            if (long.TryParse(vals.FirstOrDefault(), out var total))
              return total;
        }
      }
      catch { }

      return null;
    }

    // Read up to count bytes; returns actual read (0 = EOF)
    private static int ReadFull(Stream s, byte[] buffer, int offset, int count)
    {
      int total = 0;
      while (total < count)
      {
        int n = s.Read(buffer, offset + total, count - total);
        if (n <= 0) break;
        total += n;
      }
      return total;
    }

    private static string? GetHeaderValue(HttpResponseMessage resp, string headerName)
    {
      if (resp.Headers.TryGetValues(headerName, out var values))
        return values.FirstOrDefault();
      if (resp.Content?.Headers != null && resp.Content.Headers.TryGetValues(headerName, out var v2))
        return v2.FirstOrDefault();
      return null;
    }

    private static bool ParseBool(string value)
    {
      if (string.IsNullOrWhiteSpace(value)) return false;
      return value.Equals("true", StringComparison.OrdinalIgnoreCase)
          || value.Equals("1")
          || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractBinGuidFromBody(string body)
    {
      if (string.IsNullOrWhiteSpace(body)) return string.Empty;
      var trimmed = body.Trim().Trim('"');

      if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
      {
        try
        {
          using var doc = JsonDocument.Parse(trimmed);
          var root = doc.RootElement;
          if (root.TryGetProperty("binGUID", out var v)) return v.GetString() ?? string.Empty;
          if (root.TryGetProperty("binguid", out var v2)) return v2.GetString() ?? string.Empty;
        }
        catch { /* fall back to plain text */ }
      }
      return trimmed;
    }

    private static string AppendQueryParameter(string url, string name, string value)
    {
      if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL cannot be empty.", nameof(url));
      var trimmed = url.Trim();
      var hasQuery = trimmed.Contains('?');
      var needsAmpersand = hasQuery && !trimmed.EndsWith("?") && !trimmed.EndsWith("&");
      var prefix = hasQuery ? (needsAmpersand ? "&" : string.Empty) : "?";
      return $"{trimmed}{prefix}{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value ?? string.Empty)}";
    }

    private static void Validate(S3AuthInfo auth, string bucket, string key, int mins)
    {
      if (string.IsNullOrWhiteSpace(auth.AccessKeyId))     throw new ArgumentException("AccessKeyId is required.");
      if (string.IsNullOrWhiteSpace(auth.SecretAccessKey)) throw new ArgumentException("SecretAccessKey is required.");
      if (string.IsNullOrWhiteSpace(auth.Region))          throw new ArgumentException("Region is required.");
      if (string.IsNullOrWhiteSpace(bucket))               throw new ArgumentException("bucketName is required.");
      if (string.IsNullOrWhiteSpace(key))                  throw new ArgumentException("key is required.");
      if (mins <= 0 || mins > 10080)                       throw new ArgumentOutOfRangeException(nameof(mins), "durationInMinutes must be 1–10080.");
    }

    private static AmazonS3Client CreateClient(S3AuthInfo auth)
    {
      var region = RegionEndpoint.GetBySystemName(auth.Region);
      var cfg = new AmazonS3Config { RegionEndpoint = region };
      return new AmazonS3Client(auth.AccessKeyId, auth.SecretAccessKey, cfg);
    }
  }
}
