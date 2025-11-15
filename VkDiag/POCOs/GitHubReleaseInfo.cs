using System;
using System.Text.Json.Serialization;

namespace VkDiag.POCOs;

public class GitHubReleaseInfo
{
    public string Url { get; set; }
    public string AssetsUrl { get; set; }
    public string HtmlUrl { get; set; }
    public int Id { get; set; }
    public string TagName { get; set; }
    public string Name { get; set; }
    public string Body { get; set; }
    public bool Prerelease { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime PublishedAt { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubReleaseInfo[]))]
internal partial class GithubReleaseSerializer: JsonSerializerContext;