using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using OutSystems.ExternalLibraries.SDK;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

// -------- Version 1.2.0 of the Library --------
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

  // -------- Interface  -------- (added icon support below in folder resources ..)
  [OSInterface(
      Name = "AWSS3PreSignedUploader",
      IconResourceName = "AWSS3PreSignedUploader.resources.AWSS3PresignedUploader_lib.png",
      Description = "Generate pre-signed URLs for S3 GET/PUT operations, and stream files between ODC REST and S3"
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

    [OSAction(Description = "Stream a binary from an ODC REST endpoint directly into a pre-signed S3 PUT URL")]
    string UploadFromRestToPresignedUrl(
      [OSParameter(Description = "Source REST URL in the ODC app")] string sourceUrl,
      [OSParameter(Description = "GUID identifying the correct binary file in the ODC REST endpoint")] string binGuid,
      [OSParameter(Description = "Auth header name for Source(e.g., Authorization)")] string authHeaderName,
      [OSParameter(Description = "Auth header value for Source (e.g., Bearer <token>)")] string authHeaderValue,
      [OSParameter(Description = "Pre-signed S3 PUT URL (single-part)")] string presignedPutUrl,
      [OSParameter(Description = "Content-Type to enforce on PUT (must match the presign)")] string contentType,
      [OSParameter(Description = "Timeout in seconds (default 300)")] int timeoutSeconds);

    // NEW: S3 -> ODC (download from S3 via presigned GET into an ODC REST Target)
    [OSAction(Description = "Download from S3 (pre-signed GET) and POST the stream into an ODC REST Target URL")]
    string DownloadFromPresignedUrlToRest(
      [OSParameter(Description = "Target ODC REST URL (POST binary)")] string targetUrl,
      [OSParameter(Description = "Auth header name for Target (e.g., Authorization)")] string targetAuthHeaderName,
      [OSParameter(Description = "Auth header value for Target")] string targetAuthHeaderValue,
      [OSParameter(Description = "Pre-signed S3 GET URL")] string presignedGetUrl,
      [OSParameter(Description = "Override Content-Type sent to target (optional)")] string targetContentType,
      [OSParameter(Description = "Timeout in seconds (default 300)")] int timeoutSeconds);
  }

  // -------- Implementation --------
  public class PreSignerImpl : IPreSigner
  {
    public PreSignerImpl() { } // required by ODC

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
// NEW: Direct stream uploader to S3 from ODC REST endpoint     
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

      // 1) Open a streaming GET to the source ODC REST endpoint
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
        var buffered = new MemoryStream();
        srcStream.CopyTo(buffered);
        buffered.Position = 0;
        srcStream.Dispose();

        putContent = new StreamContent(buffered);
        putContent.Headers.ContentLength = buffered.Length;
      }

      // 2) Stream directly into S3 PUT using the pre-signed URL(pre-signed single-part)
      using var putReq = new HttpRequestMessage(HttpMethod.Put, presignedPutUrl)
      { 
        Content = putContent 
      };
      if (!string.IsNullOrWhiteSpace(contentType))
        putReq.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

      // Avoid the 100-continue round trip; keep headers small, 
      // saves a round trip, which matters when you already know the upload will be accepted
      putReq.Headers.ExpectContinue = false;

      using var putResp = http.Send(putReq, HttpCompletionOption.ResponseHeadersRead);
      putResp.EnsureSuccessStatusCode();

      // S3 returns ETag header for a successful single-part PUT
      var etag = putResp.Headers.ETag?.Tag?.Trim('"') ?? string.Empty;
      return etag;
    }

    // NEW: pre-signed GET (S3) -> POST binary to ODC REST target
    public string DownloadFromPresignedUrlToRest(
      string presignedGetUrl,
      string targetUrl,
      string targetAuthHeaderName,
      string targetAuthHeaderValue,
      string targetContentType,
      int timeoutSeconds)
    {
      if (string.IsNullOrWhiteSpace(presignedGetUrl)) throw new ArgumentException("presignedGetUrl is required.");
      if (string.IsNullOrWhiteSpace(targetUrl))         throw new ArgumentException("targetUrl is required.");
      if (timeoutSeconds <= 0) timeoutSeconds = 300;

      using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
      { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

      // 1) Open streaming GET from S3 pre-signed URL
      using var getReq = new HttpRequestMessage(HttpMethod.Get, presignedGetUrl);
      using var getResp = http.Send(getReq, HttpCompletionOption.ResponseHeadersRead);
      getResp.EnsureSuccessStatusCode();

      var srcStream = getResp.Content.ReadAsStream();
      var sourceContentType = getResp.Content.Headers.ContentType?.MediaType;
      var sourceLength = getResp.Content.Headers.ContentLength;

      // 2) POST directly into ODC REST target (streaming)
      using var postReq = new HttpRequestMessage(HttpMethod.Post, targetUrl) {
        Content = new StreamContent(srcStream)
      };

      // Prefer explicit content-type if provided; otherwise propagate S3's content-type if available
      var effectiveContentType = !string.IsNullOrWhiteSpace(targetContentType) ? targetContentType : (sourceContentType ?? "application/octet-stream");
      postReq.Content.Headers.ContentType = new MediaTypeHeaderValue(effectiveContentType);

      if (sourceLength.HasValue)
        postReq.Content.Headers.ContentLength = sourceLength.Value; // if known, set it (faster on some gateways)

      if (!string.IsNullOrWhiteSpace(targetAuthHeaderName) && !string.IsNullOrWhiteSpace(targetAuthHeaderValue))
        postReq.Headers.TryAddWithoutValidation(targetAuthHeaderName, targetAuthHeaderValue);

      postReq.Headers.ExpectContinue = false;

      using var postResp = http.Send(postReq, HttpCompletionOption.ResponseHeadersRead);
      postResp.EnsureSuccessStatusCode();

      // Return target response info (status + optional ETag/ID if provided by your API)
      var targetEtag = postResp.Headers.ETag?.Tag?.Trim('"');
      return targetEtag ?? $"OK:{(int)postResp.StatusCode}";
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
      if (string.IsNullOrWhiteSpace(auth.AccessKeyId))    throw new ArgumentException("AccessKeyId is required.");
      if (string.IsNullOrWhiteSpace(auth.SecretAccessKey))throw new ArgumentException("SecretAccessKey is required.");
      if (string.IsNullOrWhiteSpace(auth.Region))         throw new ArgumentException("Region is required.");
      if (string.IsNullOrWhiteSpace(bucket))              throw new ArgumentException("bucketName is required.");
      if (string.IsNullOrWhiteSpace(key))                 throw new ArgumentException("key is required.");
      if (mins <= 0 || mins > 10080)                      throw new ArgumentOutOfRangeException(nameof(mins), "durationInMinutes must be 1–10080.");
    }

    private static AmazonS3Client CreateClient(S3AuthInfo auth)
    {
      var region = RegionEndpoint.GetBySystemName(auth.Region);
      var cfg = new AmazonS3Config { RegionEndpoint = region };
      return new AmazonS3Client(auth.AccessKeyId, auth.SecretAccessKey, cfg);
    }
  }
}