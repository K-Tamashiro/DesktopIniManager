using System;

namespace FastVolumeIndex
{
    public static class DevelopmentScanner
    {
        private static readonly string[] SolutionExtensions =
        {
            ".sln", ".slnx"
        };

        private static readonly string[] ProjectExtensions =
        {
            ".csproj", ".vbproj", ".vcxproj", ".fsproj", ".vbp", ".dproj", ".groupproj"
        };

        private static readonly string[] ProjectMarkerNames =
        {
            "package.json", "CMakeLists.txt", "pom.xml", "build.gradle", "settings.gradle",
            "Cargo.toml", "go.mod", "Android.bp", "Android.mk", "Makefile"
        };

        public static DevelopmentInventory Analyze(NtfsVolumeIndex index, string searchRoot)
        {
            if (index == null)
                throw new ArgumentNullException(nameof(index));

            var repositories = index.FindGitRepositoryRoots(searchRoot);
            var solutions = index.FindFiles(searchRoot, SolutionExtensions);
            var projects = index.FindFiles(searchRoot, ProjectExtensions, ProjectMarkerNames);
            return new DevelopmentInventory(repositories, solutions, projects);
        }
    }
}
