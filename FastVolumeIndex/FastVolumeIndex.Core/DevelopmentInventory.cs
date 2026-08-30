using System.Collections.Generic;

namespace FastVolumeIndex
{
    public sealed class DevelopmentInventory
    {
        internal DevelopmentInventory(
            IReadOnlyList<MftEntry> repositories,
            IReadOnlyList<MftEntry> solutions,
            IReadOnlyList<MftEntry> projects)
        {
            Repositories = repositories;
            Solutions = solutions;
            Projects = projects;
        }

        public IReadOnlyList<MftEntry> Repositories { get; }
        public IReadOnlyList<MftEntry> Solutions { get; }
        public IReadOnlyList<MftEntry> Projects { get; }
    }
}
