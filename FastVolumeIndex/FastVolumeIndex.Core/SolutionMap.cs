using System.Collections.Generic;

namespace FastVolumeIndex
{
    public sealed class SolutionMap
    {
        internal SolutionMap(MftEntry solution, IReadOnlyList<SolutionProject> projects)
        {
            Solution = solution;
            Projects = projects;
        }

        public MftEntry Solution { get; }
        public IReadOnlyList<SolutionProject> Projects { get; }
    }

    public sealed class SolutionProject
    {
        internal SolutionProject(string name, string relativePath, string projectTypeId, string projectId,
            string fullPath, MftEntry entry, bool isSolutionFolder)
        {
            Name = name;
            RelativePath = relativePath;
            ProjectTypeId = projectTypeId;
            ProjectId = projectId;
            FullPath = fullPath;
            Entry = entry;
            IsSolutionFolder = isSolutionFolder;
        }

        public string Name { get; }
        public string RelativePath { get; }
        public string ProjectTypeId { get; }
        public string ProjectId { get; }
        public string FullPath { get; }
        public MftEntry Entry { get; }
        public bool IsSolutionFolder { get; }
        public bool Exists => IsSolutionFolder || Entry != null;
        public string ParentProjectId { get; internal set; }
    }
}
