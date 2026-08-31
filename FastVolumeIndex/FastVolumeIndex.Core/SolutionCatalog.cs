using System;
using System.Collections.Generic;
using System.Linq;

namespace FastVolumeIndex
{
    public sealed class SolutionCatalog
    {
        private SolutionCatalog(IReadOnlyList<SolutionMap> maps,
            Dictionary<string, SolutionMap> bySolution,
            Dictionary<string, IReadOnlyList<SolutionMap>> byProject)
        { Maps = maps; BySolutionPath = bySolution; ByProjectPath = byProject; }

        public IReadOnlyList<SolutionMap> Maps { get; }
        public IReadOnlyDictionary<string, SolutionMap> BySolutionPath { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<SolutionMap>> ByProjectPath { get; }

        public static SolutionCatalog Build(NtfsVolumeIndex index, VolumePathIndex paths)
        {
            IReadOnlyList<SolutionMap> maps = SolutionMapService.Build(index, paths);
            var bySolution = maps.ToDictionary(map => VolumePathIndex.Normalize(index.GetFullPath(map.Solution)),
                StringComparer.OrdinalIgnoreCase);
            var projectGroups = new Dictionary<string, List<SolutionMap>>(StringComparer.OrdinalIgnoreCase);
            foreach (SolutionMap map in maps)
                foreach (SolutionProject project in map.Projects.Where(project => !project.IsSolutionFolder && !string.IsNullOrEmpty(project.FullPath)))
                {
                    string path = VolumePathIndex.Normalize(project.FullPath);
                    List<SolutionMap> owners;
                    if (!projectGroups.TryGetValue(path, out owners)) projectGroups[path] = owners = new List<SolutionMap>();
                    if (!owners.Contains(map)) owners.Add(map);
                }
            return new SolutionCatalog(maps, bySolution, projectGroups.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<SolutionMap>)pair.Value, StringComparer.OrdinalIgnoreCase));
        }
    }
}
