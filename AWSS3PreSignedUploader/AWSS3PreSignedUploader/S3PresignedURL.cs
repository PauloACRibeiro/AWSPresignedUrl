using System;
using OutSystems.ExternalLibraries.SDK;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
// -------- Initial version of the Library --------
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
      Description = "Generate pre-signed URLs for S3 GET/PUT operations"
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
  }

  // -------- Implementation --------
  public class PreSignerImpl : IPreSigner
  {
    // Public parameterless constructor required by ODC..
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

    private static void Validate(S3AuthInfo auth, string bucket, string key, int mins)
    {
      if (string.IsNullOrWhiteSpace(auth.AccessKeyId)) throw new ArgumentException("AccessKeyId is required.");
      if (string.IsNullOrWhiteSpace(auth.SecretAccessKey)) throw new ArgumentException("SecretAccessKey is required.");
      if (string.IsNullOrWhiteSpace(auth.Region)) throw new ArgumentException("Region is required.");
      if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentException("BucketName is required.");
      if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.");
      if (mins <= 0 || mins > 10080) throw new ArgumentOutOfRangeException(nameof(mins), "DurationInMinutes must be 1–10080.");
    }

    private static AmazonS3Client CreateClient(S3AuthInfo auth)
    {
      var region = RegionEndpoint.GetBySystemName(auth.Region);
      var cfg = new AmazonS3Config { RegionEndpoint = region };
      return new AmazonS3Client(auth.AccessKeyId, auth.SecretAccessKey, cfg);
    }
  }
}