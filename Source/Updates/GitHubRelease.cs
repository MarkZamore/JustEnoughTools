using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace JustEnoughTools.Updates
{
    [DataContract]
    internal sealed class GitHubRelease
    {
        [DataMember(Name = "tag_name", IsRequired = true)] public string Tag;
        [DataMember(Name = "draft", IsRequired = true)] public bool Draft;
        [DataMember(Name = "prerelease", IsRequired = true)] public bool Prerelease;
        [DataMember(Name = "published_at", IsRequired = true)] public string PublishedAt;
        [DataMember(Name = "assets", IsRequired = true)] public List<GitHubAsset> Assets;

        internal static GitHubAsset SelectAsset(byte[] metadata, string name, int limit, long? expectedSize, string expectedHash)
        {
            GitHubRelease release;
            using (var stream = new MemoryStream(metadata, false))
                release = (GitHubRelease)new DataContractJsonSerializer(typeof(GitHubRelease)).ReadObject(stream);
            DateTimeOffset published;
            if (release == null || release.Tag != "latest" || release.Draft || release.Prerelease ||
                !DateTimeOffset.TryParse(release.PublishedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out published) || release.Assets == null || release.Assets.Count > 1000)
                throw new InvalidDataException("API GitHub не подтвердил опубликованный выпуск latest.");
            GitHubAsset[] matches = release.Assets.Where(asset => asset != null && asset.Name == name).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Файл выпуска отсутствует или неоднозначен в API GitHub.");
            GitHubAsset selected = matches[0];
            if (selected.Id <= 0 || release.Assets.Count(asset => asset != null && asset.Id == selected.Id) != 1 ||
                selected.State != "uploaded" || selected.Size <= 0 || selected.Size > limit ||
                selected.Digest == null || !Regex.IsMatch(selected.Digest, @"\Asha256:[0-9a-fA-F]{64}\z"))
                throw new InvalidDataException("API GitHub вернул недопустимые параметры файла выпуска.");
            if ((expectedSize.HasValue && expectedSize.Value != selected.Size) ||
                (expectedHash != null && !string.Equals(expectedHash, selected.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Файл в API GitHub отличается от проверенного манифеста.");
            return selected;
        }
    }

    [DataContract]
    internal sealed class GitHubAsset
    {
        [DataMember(Name = "id", IsRequired = true)] public long Id;
        [DataMember(Name = "name", IsRequired = true)] public string Name;
        [DataMember(Name = "state", IsRequired = true)] public string State;
        [DataMember(Name = "size", IsRequired = true)] public long Size;
        [DataMember(Name = "digest", IsRequired = true)] public string Digest;
        internal string Sha256 { get { return Digest.Substring(7); } }
    }
}
