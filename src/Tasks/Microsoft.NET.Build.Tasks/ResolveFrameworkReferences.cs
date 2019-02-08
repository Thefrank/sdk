﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NuGet.Frameworks;

namespace Microsoft.NET.Build.Tasks
{
    /// <summary>
    /// This class processes the FrameworkReference items.  It adds PackageReferences for the
    /// targeting packs which provide the reference assemblies, and creates RuntimeFramework
    /// items, which are written to the runtimeconfig file
    /// </summary>
    public class ResolveFrameworkReferences : TaskBase
    {
        public string TargetFrameworkIdentifier { get; set; }

        public string TargetFrameworkVersion { get; set; }

        public string TargetingPackRoot { get; set; }

        [Required]
        public string RuntimeGraphPath { get; set; }

        public bool SelfContained { get; set; }

        public string RuntimeIdentifier { get; set; }

        public string RuntimeFrameworkVersion { get; set; }

        public bool TargetLatestRuntimePatch { get; set; }

        public bool EnableTargetingPackDownload { get; set; }

        public ITaskItem[] FrameworkReferences { get; set; } = Array.Empty<ITaskItem>();

        public ITaskItem[] KnownFrameworkReferences { get; set; } = Array.Empty<ITaskItem>();

        [Output]
        public ITaskItem[] PackagesToDownload { get; set; }

        [Output]
        public ITaskItem[] RuntimeFrameworks { get; set; }

        [Output]
        public ITaskItem[] TargetingPacks { get; set; }

        [Output]
        public ITaskItem[] RuntimePacks { get; set; }

        //  Runtime packs which aren't available for the specified RuntimeIdentifier
        [Output]
        public ITaskItem[] UnavailableRuntimePacks { get; set; }

        protected override void ExecuteCore()
        {
            //  Perf optimization: If there are no FrameworkReference items, then don't do anything
            //  (This means that if you don't have any direct framework references, you won't get any transitive ones either
            if (FrameworkReferences == null || FrameworkReferences.Length == 0)
            {
                return;
            }

            var knownFrameworkReferencesForTargetFramework = KnownFrameworkReferences.Select(item => new KnownFrameworkReference(item))
                .Where(kfr => kfr.TargetFramework.Framework.Equals(TargetFrameworkIdentifier, StringComparison.OrdinalIgnoreCase) &&
                              NormalizeVersion(kfr.TargetFramework.Version) == NormalizeVersion(new Version(TargetFrameworkVersion)))
                .ToList();

            var frameworkReferenceDict = FrameworkReferences.ToDictionary(fr => fr.ItemSpec);

            List<ITaskItem> packagesToDownload = new List<ITaskItem>();
            List<ITaskItem> runtimeFrameworks = new List<ITaskItem>();
            List<ITaskItem> targetingPacks = new List<ITaskItem>();
            List<ITaskItem> runtimePacks = new List<ITaskItem>();
            List<ITaskItem> unavailableRuntimePacks = new List<ITaskItem>();

            bool reportedUnrecognizedRuntimeIdentifier = false;

            foreach (var knownFrameworkReference in knownFrameworkReferencesForTargetFramework)
            {
                frameworkReferenceDict.TryGetValue(knownFrameworkReference.Name, out ITaskItem frameworkReference);

                //  Get the path of the targeting pack in the targeting pack root (e.g. dotnet/ref)
                TaskItem targetingPack = new TaskItem(knownFrameworkReference.Name);
                targetingPack.SetMetadata(MetadataKeys.PackageName, knownFrameworkReference.TargetingPackName);

                string targetingPackVersion = null;
                if (frameworkReference != null)
                {
                    //  Allow targeting pack version to be overridden via metadata on FrameworkReference
                    targetingPackVersion = frameworkReference.GetMetadata("TargetingPackVersion");
                }
                if (string.IsNullOrEmpty(targetingPackVersion))
                {
                    targetingPackVersion = knownFrameworkReference.TargetingPackVersion;
                }
                targetingPack.SetMetadata(MetadataKeys.PackageVersion, targetingPackVersion);

                string targetingPackPath = null;
                if (!string.IsNullOrEmpty(TargetingPackRoot))
                {
                    targetingPackPath = Path.Combine(TargetingPackRoot, knownFrameworkReference.TargetingPackName, knownFrameworkReference.TargetingPackVersion);
                }
                if (targetingPackPath != null && Directory.Exists(targetingPackPath))
                {
                    targetingPack.SetMetadata(MetadataKeys.Path, targetingPackPath);
                    targetingPack.SetMetadata(MetadataKeys.PackageDirectory, targetingPackPath);
                }
                else
                {
                    if (EnableTargetingPackDownload)
                    {
                        //  Download targeting pack
                        TaskItem packageToDownload = new TaskItem(knownFrameworkReference.TargetingPackName);
                        packageToDownload.SetMetadata(MetadataKeys.Version, targetingPackVersion);

                        packagesToDownload.Add(packageToDownload);
                    }
                }

                targetingPacks.Add(targetingPack);

                var runtimeFrameworkVersion = GetRuntimeFrameworkVersion(frameworkReference, knownFrameworkReference);

                if (SelfContained &&
                    !string.IsNullOrEmpty(RuntimeIdentifier) &&
                    !string.IsNullOrEmpty(knownFrameworkReference.RuntimePackNamePatterns))
                {
                    foreach (var runtimePackNamePattern in knownFrameworkReference.RuntimePackNamePatterns.Split(';'))
                    {
                        string runtimePackRuntimeIdentifier = NuGetUtils.GetBestMatchingRid(
                            new RuntimeGraphCache(this).GetRuntimeGraph(RuntimeGraphPath),
                            RuntimeIdentifier,
                            knownFrameworkReference.RuntimePackRuntimeIdentifiers.Split(';'),
                            out bool wasInGraph);

                        if (runtimePackRuntimeIdentifier == null)
                        {
                            if (wasInGraph)
                            {
                                //  Report this as an error later, if necessary.  This is because we try to download
                                //  all available runtime packs in case there is a transitive reference to a shared
                                //  framework we don't directly reference.  But we don't want to immediately error out
                                //  here if a runtime pack that we might not need to reference isn't available for the
                                //  targeted RID (e.g. Microsoft.WindowsDesktop.App for a linux RID).
                                var unavailableRuntimePack = new TaskItem(knownFrameworkReference.Name);
                                unavailableRuntimePack.SetMetadata(MetadataKeys.RuntimeIdentifier, RuntimeIdentifier);
                                unavailableRuntimePacks.Add(unavailableRuntimePack);
                            }
                            else if (!reportedUnrecognizedRuntimeIdentifier)
                            {
                                //  NETSDK1083: The specified RuntimeIdentifier '{0}' is not recognized.
                                Log.LogError(Strings.RuntimeIdentifierNotRecognized, RuntimeIdentifier);
                                reportedUnrecognizedRuntimeIdentifier = true;
                            }
                        }
                        else
                        {
                            string runtimePackName = runtimePackNamePattern.Replace("**RID**", runtimePackRuntimeIdentifier);

                            TaskItem runtimePackItem = new TaskItem(runtimePackName);
                            runtimePackItem.SetMetadata(MetadataKeys.PackageName, runtimePackName);
                            runtimePackItem.SetMetadata(MetadataKeys.PackageVersion, runtimeFrameworkVersion);
                            runtimePackItem.SetMetadata(MetadataKeys.FrameworkName, knownFrameworkReference.Name);
                            runtimePackItem.SetMetadata(MetadataKeys.RuntimeIdentifier, runtimePackRuntimeIdentifier);

                            runtimePacks.Add(runtimePackItem);

                            TaskItem packageToDownload = new TaskItem(runtimePackName);
                            packageToDownload.SetMetadata(MetadataKeys.Version, runtimeFrameworkVersion);

                            packagesToDownload.Add(packageToDownload);
                        }
                    }
                }

                TaskItem runtimeFramework = new TaskItem(knownFrameworkReference.RuntimeFrameworkName);

                runtimeFramework.SetMetadata(MetadataKeys.Version, runtimeFrameworkVersion);
                runtimeFramework.SetMetadata(MetadataKeys.FrameworkName, knownFrameworkReference.Name);

                runtimeFrameworks.Add(runtimeFramework);
            }
                                                      
            if (packagesToDownload.Any())
            {
                PackagesToDownload = packagesToDownload.ToArray();
            }

            if (runtimeFrameworks.Any())
            {
                RuntimeFrameworks = runtimeFrameworks.ToArray();
            }

            if (targetingPacks.Any())
            {
                TargetingPacks = targetingPacks.ToArray();
            }

            if (runtimePacks.Any())
            {
                RuntimePacks = runtimePacks.ToArray();
            }

            if (unavailableRuntimePacks.Any())
            {
                UnavailableRuntimePacks = unavailableRuntimePacks.ToArray();
            }
        }

        private string GetRuntimeFrameworkVersion(ITaskItem frameworkReference, KnownFrameworkReference knownFrameworkReference)
        {
            //  Precedence order for selecting runtime framework version
            //  - RuntimeFrameworkVersion metadata on FrameworkReference item
            //  - RuntimeFrameworkVersion MSBuild property
            //  - Then, use either the LatestRuntimeFrameworkVersion or the DefaultRuntimeFrameworkVersion of the KnownFrameworkReference, based on
            //      - The value (if set) of TargetLatestRuntimePatch metadata on the FrameworkReference
            //      - The TargetLatestRuntimePatch MSBuild property (which defaults to True if SelfContained is true, and False otherwise)

            string runtimeFrameworkVersion = null;

            if (frameworkReference != null)
            {
                runtimeFrameworkVersion = frameworkReference.GetMetadata("RuntimeFrameworkVersion");
            }
            if (string.IsNullOrEmpty(runtimeFrameworkVersion))
            {
                runtimeFrameworkVersion = RuntimeFrameworkVersion;
            }
            if (string.IsNullOrEmpty(runtimeFrameworkVersion))
            {
                bool? useLatestRuntimeFrameworkVersion = null;
                if (frameworkReference != null)
                {
                    string useLatestRuntimeFrameworkMetadata = frameworkReference.GetMetadata("TargetLatestRuntimePatch");
                    if (!string.IsNullOrEmpty(useLatestRuntimeFrameworkMetadata))
                    {
                        useLatestRuntimeFrameworkVersion = MSBuildUtilities.ConvertStringToBool(useLatestRuntimeFrameworkMetadata,
                            defaultValue: false);
                    }
                }
                if (useLatestRuntimeFrameworkVersion == null)
                {
                    useLatestRuntimeFrameworkVersion = TargetLatestRuntimePatch;
                }
                if (useLatestRuntimeFrameworkVersion.Value)
                {
                    runtimeFrameworkVersion = knownFrameworkReference.LatestRuntimeFrameworkVersion;
                }
                else
                {
                    runtimeFrameworkVersion = knownFrameworkReference.DefaultRuntimeFrameworkVersion;
                }
            }

            return runtimeFrameworkVersion;
        }

        internal static Version NormalizeVersion(Version version)
        {
            if (version.Revision == 0)
            {
                if (version.Build == 0)
                {
                    return new Version(version.Major, version.Minor);
                }
                else
                {
                    return new Version(version.Major, version.Minor, version.Build);
                }
            }

            return version;
        }

        private struct KnownFrameworkReference
        {
            ITaskItem _item;
            public KnownFrameworkReference(ITaskItem item)
            {
                _item = item;
                TargetFramework = NuGetFramework.Parse(item.GetMetadata("TargetFramework"));
            }

            //  The name / itemspec of the FrameworkReference used in the project
            public string Name => _item.ItemSpec;

            //  The framework name to write to the runtimeconfig file (and the name of the folder under dotnet/shared)
            public string RuntimeFrameworkName => _item.GetMetadata("RuntimeFrameworkName");
            public string DefaultRuntimeFrameworkVersion => _item.GetMetadata("DefaultRuntimeFrameworkVersion");
            public string LatestRuntimeFrameworkVersion => _item.GetMetadata("LatestRuntimeFrameworkVersion");

            //  The ID of the targeting pack NuGet package to reference
            public string TargetingPackName => _item.GetMetadata("TargetingPackName");
            public string TargetingPackVersion => _item.GetMetadata("TargetingPackVersion");

            public string RuntimePackNamePatterns => _item.GetMetadata("RuntimePackNamePatterns");

            public string RuntimePackRuntimeIdentifiers => _item.GetMetadata("RuntimePackRuntimeIdentifiers");

            public NuGetFramework TargetFramework { get; }
        }
    }
}
