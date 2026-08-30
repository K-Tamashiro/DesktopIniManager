using System.Collections.Generic;

namespace FastVolumeIndex
{
    public sealed class SolutionCoverage
    {
        internal SolutionCoverage(IReadOnlyList<SolutionMap> solutions,
            IReadOnlyList<MftEntry> unreferencedProjects,
            IReadOnlyList<SolutionProject> missingProjects)
        {
            Solutions = solutions;
            UnreferencedProjects = unreferencedProjects;
            MissingProjects = missingProjects;
        }

        public IReadOnlyList<SolutionMap> Solutions { get; }
        public IReadOnlyList<MftEntry> UnreferencedProjects { get; }
        public IReadOnlyList<SolutionProject> MissingProjects { get; }
    }
}
