using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace JustEnoughTools.Updates
{
    public sealed class UpdateStatus
    {
        public UpdateStatus(string text, double? fraction = null, bool ready = false)
        {
            Text = text;
            Fraction = fraction;
            Ready = ready;
        }

        public string Text { get; }
        public double? Fraction { get; }
        public bool Ready { get; }
    }

    [DataContract]
    internal sealed class UpdateManifest
    {
        [DataMember(Name = "schemaVersion")] public int SchemaVersion { get; set; }
        [DataMember(Name = "releaseNumber")] public int ReleaseNumber { get; set; }
        [DataMember(Name = "commitSha", EmitDefaultValue = false)] public string CommitSha { get; set; }
        [DataMember(Name = "version", EmitDefaultValue = false)] public string Version { get; set; }
        [DataMember(Name = "publishedAtUtc", EmitDefaultValue = false)] public string PublishedAtUtc { get; set; }
        [DataMember(Name = "assetName")] public string AssetName { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        [DataMember(Name = "sizeBytes")] public long SizeBytes { get; set; }
        [DataMember(Name = "deltaPatches", EmitDefaultValue = false)] public List<DeltaManifest> DeltaPatches { get; set; }
    }

    [DataContract]
    internal sealed class DeltaManifest
    {
        [DataMember(Name = "algorithm")] public string Algorithm { get; set; }
        [DataMember(Name = "algorithmVersion")] public int AlgorithmVersion { get; set; }
        [DataMember(Name = "baseReleaseNumber")] public int BaseReleaseNumber { get; set; }
        [DataMember(Name = "baseCommitSha", EmitDefaultValue = false)] public string BaseCommitSha { get; set; }
        [DataMember(Name = "baseSha256")] public string BaseSha256 { get; set; }
        [DataMember(Name = "assetName")] public string AssetName { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        [DataMember(Name = "sizeBytes")] public long SizeBytes { get; set; }
    }

    internal sealed class PreparedUpdate
    {
        internal PreparedUpdate(string packagePath, UpdateManifest manifest, long payloadLength)
        {
            PackagePath = packagePath;
            Manifest = manifest;
            PayloadLength = payloadLength;
        }

        internal string PackagePath { get; }
        internal UpdateManifest Manifest { get; }
        internal long PayloadLength { get; }
    }
}
